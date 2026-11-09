using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// HTTP downloads with bounded connections, per-file locks, resumable
    /// partial files, cryptographic validation and official/BMCLAPI fallback.
    /// NetworkMode: official, mirror, auto. Unknown API endpoints remain official.
    /// </summary>
    public sealed class NetworkDownloader : IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> OutputLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        readonly HttpClient http;
        readonly int attempts;
        readonly LauncherConfig settings;
        public NetworkDownloader(LauncherConfig settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            var handler = new HttpClientHandler {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = Math.Max(2, Math.Min(32, settings.DownloadThreads))
            };
            if (!string.IsNullOrWhiteSpace(settings.Proxy))
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(settings.Proxy);
            }
            // 45 seconds was too short for large JARs on domestic links.
            // Metadata requests have their own shorter timeout below.
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.31");
            attempts = Math.Max(1, Math.Min(6, settings.DownloadRetries));
        }

        public static string MirrorMojangUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo))
                return null;
            string prefix;
            switch (uri.Host.ToLowerInvariant())
            {
                case "piston-meta.mojang.com":
                case "launchermeta.mojang.com":
                case "launcher.mojang.com":
                case "piston-data.mojang.com":
                    prefix = ""; break;
                case "libraries.minecraft.net": prefix = "/maven"; break;
                case "resources.download.minecraft.net": prefix = "/assets"; break;
                case "maven.fabricmc.net": prefix = "/maven"; break;
                case "meta.fabricmc.net": prefix = "/fabric-meta"; break;
                default: return null;
            }
            return "https://bmclapi2.bangbang93.com" + prefix + uri.AbsolutePath + uri.Query;
        }

        public static IReadOnlyList<string> Routes(string official, string mode)
        {
            if (!Uri.TryCreate(official, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                !uri.IsDefaultPort) throw new ArgumentException("只允许标准 HTTPS 下载链接");
            string mirror = MirrorMojangUri(official);
            if (mirror == null || mode == "official") return new[] { official };
            if (mode == "mirror" || mode == "china") return new[] { mirror, official };
            // Auto has a conservative official-first fallback for public CDN
            // hosts; the source choice can be explicitly set by the user.
            return new[] { official, mirror };
        }

        /// <summary>
        /// Optional, opt-in MCIM acceleration for official Modrinth metadata
        /// and CDN files or approved CurseForge CDN files. Unknown hosts and
        /// auth/account endpoints are never mirrored.
        /// </summary>
        internal static string MirrorCommunityUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                return null;
            string prefix;
            switch (uri.Host.ToLowerInvariant())
            {
                case "api.modrinth.com": prefix = "/modrinth"; break;
                case "cdn.modrinth.com":
                case "edge.forgecdn.net":
                case "mediafilez.forgecdn.net": prefix = ""; break;
                default: return null;
            }
            return "https://mod.mcimirror.top" + prefix + uri.AbsolutePath + uri.Query;
        }

        internal static IReadOnlyList<string> RoutesForDownload(string official, string mode, bool mcimEnabled)
        {
            if (!Uri.TryCreate(official, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                throw new ArgumentException("只允许标准 HTTPS 下载链接");
            if (mcimEnabled && mode != "official")
            {
                string optional = MirrorCommunityUri(official);
                if (optional != null)
                    return new[] { optional, official };
            }
            return Routes(official, mode);
        }

        static string PartialOriginId(string candidate, string sha1)
        {
            using (var sha = SHA256.Create())
            {
                // Do not persist URLs or tokens in the sidecar on disk.
                byte[] data = System.Text.Encoding.UTF8.GetBytes(candidate + "\n" + sha1.ToLowerInvariant());
                return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "");
            }
        }

        /// <summary>
        /// Never resume a .part file against a different mirror URL or hash.
        /// Resume is safe only for the exact same source; final SHA-1 still
        /// validates the whole downloaded payload before moving into place.
        /// </summary>
        internal static long PreparePartialForRoute(string part, string candidate, string sha1)
        {
            string marker = part + ".origin";
            string expected = PartialOriginId(candidate, sha1);
            if (File.Exists(part))
            {
                string previous = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
                if (string.Equals(previous, expected, StringComparison.OrdinalIgnoreCase))
                    return new FileInfo(part).Length;
                File.Delete(part);
            }
            File.WriteAllText(marker, expected);
            return 0;
        }

        public async Task<string> GetStringWithFallbackAsync(string officialUrl, string networkMode, CancellationToken ct)
        {
            Exception last = null;
            foreach (string url in RoutesForDownload(officialUrl, networkMode, settings.McimEnabled))
            {
                for (int i = 0; i < Math.Min(2, attempts); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Fail over rapidly for metadata; the 20-minute file
                        // timeout must never stall a lightweight API request.
                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            linked.CancelAfter(TimeSpan.FromSeconds(18));
                            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linked.Token))
                            {
                                if (TemporaryStatus(response.StatusCode))
                                    throw new HttpRequestException("线路暂时不可用：" + response.StatusCode);
                                response.EnsureSuccessStatusCode();
                                const int maxMetadataBytes = 12 * 1024 * 1024;
                                if (response.Content.Headers.ContentLength > maxMetadataBytes)
                                    throw new InvalidDataException("远端元数据超过大小限制");
                                // Capped streaming avoids large JSON responses
                                // exhausting memory and observes per-request
                                // cancellation even after response headers.
                                using (var input = await response.Content.ReadAsStreamAsync())
                                using (var memory = new MemoryStream())
                                {
                                    byte[] buffer = new byte[32768];
                                    int count;
                                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length,
                                        linked.Token)) > 0)
                                    {
                                        if (memory.Length + count > maxMetadataBytes)
                                            throw new InvalidDataException("远端元数据超过大小限制");
                                        memory.Write(buffer, 0, count);
                                    }
                                    return Encoding.UTF8.GetString(memory.ToArray());
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException ||
                        ex is OperationCanceledException ||
                        (ex is IOException && !(ex is InvalidDataException)))
                    {
                        if (ct.IsCancellationRequested) throw;
                        last = ex;
                        if (i + 1 < Math.Min(2, attempts)) await Task.Delay(300 * (i + 1), ct);
                    }
                }
            }
            throw new IOException("全部元数据下载线路均失败，请切换下载模式或检查代理。", last);
        }

        static bool TemporaryStatus(HttpStatusCode code) => (int)code == 429 || (int)code >= 500;
        static bool ValidSha1(string sha) => sha != null && sha.Length == 40 && sha.All(Uri.IsHexDigit);

        public Task DownloadVerifiedAsync(string url, string destination, string sha1,
            CancellationToken ct, IProgress<long> progress = null)
        {
            IProgress<DownloadBytes> converted = progress == null ? null :
                new Progress<DownloadBytes>(info => progress.Report(info.Completed));
            return DownloadVerifiedDetailedAsync(url, destination, sha1, ct, converted);
        }

        public async Task DownloadVerifiedDetailedAsync(string url, string destination, string sha1,
            CancellationToken ct, IProgress<DownloadBytes> progress = null)
        {
            if (string.IsNullOrEmpty(sha1) || !ValidSha1(sha1))
                throw new ArgumentException("下载文件必须具有有效的 SHA-1，用于防止损坏文件被安装");
            string fullPath = Path.GetFullPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            var gate = OutputLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (File.Exists(fullPath))
                {
                    using (var existing = File.OpenRead(fullPath))
                    using (var hasher = SHA1.Create())
                    {
                        string digest = BitConverter.ToString(hasher.ComputeHash(existing)).Replace("-", "");
                        if (string.Equals(digest, sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            progress?.Report(new DownloadBytes(existing.Length, existing.Length));
                            return; // Reuse only when content hash matches.
                        }
                    }
                }
                await TransferVerifiedAsync(url, fullPath, sha1, ct, progress);
            }
            finally { gate.Release(); }
        }

        async Task TransferVerifiedAsync(string url, string fullPath, string sha1,
            CancellationToken ct, IProgress<DownloadBytes> progress)
        {
            string part = fullPath + ".blockcraft.part";
            Exception last = null;
            var routes = RoutesForDownload(url, settings.NetworkMode, settings.McimEnabled);
            foreach (var candidate in routes)
            {
                for (int retry = 0; retry < attempts; retry++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        long offset = PreparePartialForRoute(part, candidate, sha1);
                        if (offset > 1024L * 1024 * 1024 * 4)
                        {
                            File.Delete(part);
                            offset = 0;
                        }
                        using (var request = new HttpRequestMessage(HttpMethod.Get, candidate))
                        {
                            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                            using (var response = await http.SendAsync(request,
                                HttpCompletionOption.ResponseHeadersRead, ct))
                            {
                                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                                {
                                    if (File.Exists(part)) File.Delete(part);
                                    continue;
                                }
                                if (TemporaryStatus(response.StatusCode))
                                {
                                    var after = response.Headers.RetryAfter?.Delta;
                                    int ms = after.HasValue ? (int)Math.Max(0, Math.Min(10000, after.Value.TotalMilliseconds)) : 0;
                                    throw new RetryableHttpException("服务器繁忙：" + response.StatusCode, ms);
                                }
                                response.EnsureSuccessStatusCode();
                                bool continued = offset > 0 &&
                                    response.StatusCode == HttpStatusCode.PartialContent &&
                                    response.Content.Headers.ContentRange?.From == offset;
                                // If the CDN ignored the range request or the ETag changed,
                                // replace the incomplete .part file rather than appending.
                                if (!continued) offset = 0;
                                long total = response.Content.Headers.ContentLength.HasValue
                                    ? response.Content.Headers.ContentLength.Value + offset : 0;
                                if (total > 4L * 1024 * 1024 * 1024)
                                    throw new InvalidDataException("单文件超过 4 GiB 限制");
                                using (var incoming = await response.Content.ReadAsStreamAsync())
                                using (var output = new FileStream(part, offset > 0 ?
                                    FileMode.Append : FileMode.Create, FileAccess.Write,
                                    FileShare.None, 128 * 1024, true))
                                {
                                    byte[] buffer = new byte[128 * 1024];
                                    long written = offset;
                                    int count;
                                    while ((count = await incoming.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                                    {
                                        written += count;
                                        if (written > 4L * 1024 * 1024 * 1024)
                                            throw new InvalidDataException("下载大小超过安全限制");
                                        await output.WriteAsync(buffer, 0, count, ct);
                                        progress?.Report(new DownloadBytes(written, total));
                                    }
                                }
                                if (total > 0 && new FileInfo(part).Length != total)
                                    throw new IOException("下载未完成，保留断点续传临时文件");
                            }
                        }
                        string hash;
                        using (var file = File.OpenRead(part))
                        using (var sha = SHA1.Create())
                            hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                        if (!hash.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(part);
                            throw new InvalidDataException("SHA-1 校验失败，拒绝安装损坏资源");
                        }
                        if (File.Exists(fullPath)) File.Replace(part, fullPath, null);
                        else File.Move(part, fullPath);
                        string origin = part + ".origin";
                        try { if (File.Exists(origin)) File.Delete(origin); }
                        catch (IOException) { /* Completed payload is already verified and committed. */ }
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpRequestException ||
                                               ex is TaskCanceledException || ex is InvalidDataException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        last = ex;
                        if (retry + 1 < attempts)
                        {
                            int pause = ex is RetryableHttpException rate && rate.RetryAfterMs > 0
                                ? rate.RetryAfterMs : Math.Min(6000, 400 * (1 << retry));
                            await Task.Delay(pause, ct);
                        }
                    }
                }
            }
            throw new IOException("所有下载线路重试后仍失败，请检查网络、代理或更换线路。", last);
        }

        sealed class RetryableHttpException : HttpRequestException
        {
            public int RetryAfterMs { get; }
            public RetryableHttpException(string message, int retryAfterMs) : base(message)
            { RetryAfterMs = retryAfterMs; }
        }

        public void Dispose() => http.Dispose();
    }
}