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
using System.Web.Script.Serialization;
using System.Xml;
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
        static readonly SemaphoreSlim[] OutputLocks = Enumerable.Range(0, 256)
            .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        static readonly ConcurrentDictionary<string, DateTime> PreferredMirrors =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        internal static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromSeconds(25);
        internal static readonly TimeSpan AlternateRouteHeaderTimeout = TimeSpan.FromSeconds(6);

        // When a known, explicitly allowed backup route exists, do not spend
        // the full 10-second header timeout on an unreachable first endpoint.
        // Never truncate the timeout for a single-source request, and preserve
        // deliberately shorter test/user deadlines.
        internal static TimeSpan FirstRouteHeaderTimeout(TimeSpan original, bool hasAlternative)
        {
            return hasAlternative && original > AlternateRouteHeaderTimeout
                ? AlternateRouteHeaderTimeout : original;
        }
        const int MaxMetadataBytes = 12 * 1024 * 1024;
        const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

        readonly HttpClient http;
        readonly int attempts;
        readonly string mode;
        readonly bool mcimEnabled;
        readonly string proxy;
        readonly TimeSpan requestTimeout, idleTimeout;

        public NetworkDownloader(LauncherConfig settings)
            : this(settings, CreateHttpHandler(settings))
        {
        }

        internal NetworkDownloader(LauncherConfig settings, HttpMessageHandler handler,
            TimeSpan? requestTimeout = null, TimeSpan? idleTimeout = null)
        {
            mode = NormalizeMode(settings.NetworkMode);
            mcimEnabled = settings.McimEnabled;
            proxy = settings.Proxy ?? "";
            this.requestTimeout = requestTimeout ?? HeaderTimeout;
            this.idleTimeout = idleTimeout ?? TransferIdleTimeout;
            http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.37");
            attempts = Math.Max(1, Math.Min(6, settings.DownloadRetries));
        }

        internal static string NormalizeMode(string value)
        {
            switch (value)
            {
                case "china":
                case "mirror": return "china";
                case "international": return "international";
                case "official": return "official";
                default: return "auto";
            }
        }

        internal static bool TryNormalizeProxy(string value, out string normalized)
        {
            normalized = (value ?? "").Trim();
            if (normalized.Length == 0) return true;
            if (!normalized.Contains("://")) normalized = "http://" + normalized;
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.Host.Length == 0 || uri.UserInfo.Length != 0 ||
                uri.PathAndQuery != "/" || uri.Fragment.Length != 0)
                return false;
            normalized = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        internal static HttpClientHandler CreateHttpHandler(LauncherConfig settings, bool allowRedirects = true)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (!TryNormalizeProxy(settings.Proxy, out var proxy))
                throw new ArgumentException("代理地址格式错误。请输入 HTTP 代理地址，例如 http://127.0.0.1:7890。");
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = allowRedirects,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = Math.Max(2, Math.Min(32, settings.DownloadThreads))
            };
            // A null Proxy with UseProxy enabled uses Windows' configured proxy.
            if (proxy.Length > 0) handler.Proxy = new WebProxy(proxy);
            return handler;
        }

        public static string MirrorMojangUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) || uri.Fragment.Length != 0 ||
                uri.Query.Length != 0)
                return null;
            string path = uri.AbsolutePath;
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
                case "meta.quiltmc.org": prefix = "/quilt-meta"; break;
                case "maven.minecraftforge.net": prefix = "/maven"; break;
                case "files.minecraftforge.net" when path.StartsWith("/maven/", StringComparison.Ordinal):
                    prefix = ""; break;
                case "maven.neoforged.net" when path.StartsWith("/releases/", StringComparison.Ordinal):
                    prefix = "/maven";
                    path = path.Substring("/releases".Length);
                    break;
                case "maven.quiltmc.org" when path.StartsWith("/repository/release/", StringComparison.Ordinal):
                    prefix = "/maven";
                    path = path.Substring("/repository/release".Length);
                    break;
                default: return null;
            }
            return "https://bmclapi2.bangbang93.com" + prefix + path;
        }

        public static IReadOnlyList<string> Routes(string official, string mode)
        {
            if (!Uri.TryCreate(official, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                !uri.IsDefaultPort) throw new ArgumentException("只允许标准 HTTPS 下载链接");
            string mirror = MirrorMojangUri(official);
            return OrderRoutes(official, mirror, mode);
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
                case "mediafilez.forgecdn.net":
                    if (uri.Query.Length != 0) return null;
                    prefix = ""; break;
                default: return null;
            }
            return "https://mod.mcimirror.top" + prefix + uri.AbsolutePath + uri.Query;
        }

        internal static IReadOnlyList<string> RoutesForDownload(string official, string mode, bool mcimEnabled)
        {
            if (!Uri.TryCreate(official, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
                throw new ArgumentException("只允许标准 HTTPS 下载链接");
            string mirror = mcimEnabled ? MirrorCommunityUri(official) : null;
            return mirror == null ? Routes(official, mode) : OrderRoutes(official, mirror, mode);
        }

        static IReadOnlyList<string> OrderRoutes(string official, string mirror, string mode)
        {
            mode = NormalizeMode(mode);
            if (mirror == null || mode == "official") return new[] { official };
            return mode == "china" ? new[] { mirror, official } : new[] { official, mirror };
        }

        string RouteKey(string official) => proxy + "|" + new Uri(official).Host;

        IReadOnlyList<string> Candidates(string official, string requestedMode)
        {
            var routes = RoutesForDownload(official, requestedMode, mcimEnabled);
            if (routes.Count == 2 && NormalizeMode(requestedMode) == "auto" &&
                PreferredMirrors.TryGetValue(RouteKey(official), out var expiry) && expiry > DateTime.UtcNow)
                return new[] { routes[1], routes[0] };
            return routes;
        }

        void RecordRoute(string official, string used, string requestedMode, int candidates)
        {
            if (candidates != 2 || NormalizeMode(requestedMode) != "auto") return;
            if (used == official) PreferredMirrors.TryRemove(RouteKey(official), out _);
            else PreferredMirrors[RouteKey(official)] = DateTime.UtcNow.AddMinutes(4);
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

        public Task<string> GetStringWithFallbackAsync(string officialUrl, string networkMode, CancellationToken ct)
        {
            return GetMetadataWithFallbackAsync(officialUrl, networkMode, body =>
            {
                var json = new JavaScriptSerializer { MaxJsonLength = MaxMetadataBytes };
                var document = json.DeserializeObject(body);
                if (!(document is IDictionary<string, object>) && !(document is object[]))
                    throw new InvalidDataException("来源未返回 JSON 对象或列表");
                return body;
            }, ct);
        }

        internal async Task<T> GetMetadataWithFallbackAsync<T>(string officialUrl, string networkMode,
            Func<string, T> parse, CancellationToken ct, bool refresh = false)
        {
            ct.ThrowIfCancellationRequested();
            string cacheKey = PublicSearchCache.CacheKey(
                officialUrl, NormalizeMode(networkMode), proxy, mcimEnabled);
            if (!refresh && PublicSearchCache.TryGet(cacheKey, out var cached))
            {
                try { return parse(cached); }
                catch (Exception error) when (error is InvalidDataException ||
                    error is ArgumentException || error is InvalidOperationException ||
                    error is XmlException)
                {
                    // A parser change must not turn a stale cache value into a
                    // false success. Re-fetch validated provider metadata.
                    PublicSearchCache.Remove(cacheKey);
                }
            }

            var routes = Candidates(officialUrl, networkMode);
            var retryable = new HashSet<string>(routes, StringComparer.Ordinal);
            Exception last = null;
            var failures = new List<string>();
            for (int round = 0; round < Math.Min(2, attempts) && retryable.Count > 0; round++)
            {
                foreach (string url in routes.Where(retryable.Contains))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Bound only the connection/response headers here. Metadata
                        // may stream for longer on slow mainland connections:
                        // give the body a separate inactivity timeout, rather
                        // than cancelling healthy downloads at the header deadline.
                        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                        {
                            if (refresh) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                            using (var response = await SendWithHeaderTimeoutAsync(http, request,
                                FirstRouteHeaderTimeout(requestTimeout,
                                    routes.Count > 1 && string.Equals(url, routes[0],
                                        StringComparison.Ordinal)), ct).ConfigureAwait(false))
                            using (var memory = new MemoryStream())
                            {
                                CheckStatus(response);
                                await CopyBodyAsync(response, memory, MaxMetadataBytes, 0, null,
                                    null, idleTimeout, ct).ConfigureAwait(false);
                                string body = Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
                                var result = parse(body);
                                RecordRoute(officialUrl, url, networkMode, routes.Count);
                                PublicSearchCache.Store(cacheKey, body);
                                return result;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                        ex is HttpRequestException || ex is OperationCanceledException || ex is ArgumentException ||
                        ex is InvalidOperationException || ex is XmlException)
                    {
                        ct.ThrowIfCancellationRequested();
                        last = ex;
                        failures.Add(new Uri(url).Host + " " + RouteFailure(ex));
                        if (ex is InvalidDataException || ex is ArgumentException ||
                            ex is InvalidOperationException || ex is XmlException || ex is PermanentRouteException)
                            retryable.Remove(url);
                    }
                }
                if (round + 1 < Math.Min(2, attempts) && retryable.Count > 0)
                {
                    int pause = last is RetryableHttpException limited ? limited.RetryAfterMs : 0;
                    await Task.Delay(Math.Max(350, pause), ct).ConfigureAwait(false);
                }
            }
            throw new IOException("无法获取在线数据（" + string.Join("；", failures.Distinct()) + "）。", last);
        }

        internal static async Task<HttpResponseMessage> SendWithHeaderTimeoutAsync(HttpClient client,
            HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(timeout);
                try
                {
                    return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("连接等待超时", ex);
                }
            }
        }

        internal static async Task<long> CopyBodyAsync(HttpResponseMessage response, Stream output,
            long maxBytes, long offset, long? total, IProgress<DownloadBytes> progress,
            TimeSpan idleTimeout, CancellationToken ct)
        {
            if (total.HasValue && total > maxBytes)
                throw new InvalidDataException("文件大小超过限制");
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            // Some .NET Framework response streams ignore a cancellation token
            // once a socket read starts. Closing the response also ends that read.
            using (idle.Token.Register(response.Dispose))
            {
                try
                {
                    idle.CancelAfter(idleTimeout);
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        byte[] buffer = new byte[128 * 1024];
                        long written = offset;
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length,
                            idle.Token).ConfigureAwait(false)) > 0)
                        {
                            idle.CancelAfter(Timeout.InfiniteTimeSpan);
                            written += count;
                            if (written > maxBytes) throw new InvalidDataException("文件大小超过限制");
                            await output.WriteAsync(buffer, 0, count, ct).ConfigureAwait(false);
                            progress?.Report(new DownloadBytes(written, total ?? 0));
                            idle.CancelAfter(idleTimeout);
                        }
                        ct.ThrowIfCancellationRequested();
                        if (total.HasValue && total.Value != written)
                            throw new IOException("数据传输不完整");
                        return written;
                    }
                }
                catch (Exception ex) when (idle.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new IOException("数据传输长时间无响应", ex);
                }
            }
        }

        static bool TemporaryStatus(HttpStatusCode code) =>
            (int)code == 408 || (int)code == 429 || (int)code >= 500;

        static void CheckStatus(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            int status = (int)response.StatusCode;
            if (!TemporaryStatus(response.StatusCode)) throw new PermanentRouteException(status);
            var header = response.Headers.RetryAfter;
            double wait = (header?.Delta ?? (header?.Date - DateTimeOffset.UtcNow))
                ?.TotalMilliseconds ?? 0;
            throw new RetryableHttpException(status, (int)Math.Max(0, Math.Min(5000, wait)));
        }

        static string RouteFailure(Exception ex)
        {
            if (ex is RouteHttpException httpError) return "HTTP " + httpError.Status;
            if (ex is OperationCanceledException) return "超时";
            if (ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException ||
                ex is XmlException)
                return "响应格式或校验异常";
            return "连接或传输失败";
        }

        internal static bool ValidSha1(string sha) => sha != null && sha.Length == 40 && sha.All(Uri.IsHexDigit);

        // ReplaceFileW is atomic on supported disks, but FAT/exFAT USB media
        // may reject it. Keep the old file intact until the SHA-1-verified
        // replacement is committed, restoring it if the portable move fails.
        internal static void CommitVerifiedFile(string part, string destination,
            bool forcePortableFallback = false)
        {
            if (!File.Exists(destination))
            {
                File.Move(part, destination);
                return;
            }
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("拒绝替换符号链接或重解析点文件");
            if (!forcePortableFallback)
            {
                try
                {
                    File.Replace(part, destination, null);
                    return;
                }
                catch (IOException) { if (!File.Exists(part)) throw; }
                catch (PlatformNotSupportedException) { }
            }

            string backup = destination + ".blockcraft-previous-" +
                Guid.NewGuid().ToString("N") + ".bak";
            bool movedOld = false;
            try
            {
                File.Move(destination, backup);
                movedOld = true;
                File.Move(part, destination);
            }
            catch
            {
                if (movedOld && !File.Exists(destination) && File.Exists(backup))
                    File.Move(backup, destination);
                throw;
            }
            // If backup cleanup fails, the newly verified file remains intact.
            // Never roll back after the new file has been committed.
            try { File.Delete(backup); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

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
            int hashCode = StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath) & int.MaxValue;
            var gate = OutputLocks[hashCode % OutputLocks.Length];
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
            var failures = new List<string>();
            var routes = Candidates(url, mode);
            for (int route = 0; route < routes.Count; route++)
            {
                string candidate = routes[route];
                bool hasAlternative = route + 1 < routes.Count;
                for (int retry = 0; retry < attempts; retry++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        long offset = PreparePartialForRoute(part, candidate, sha1);
                        if (offset >= MaxFileBytes)
                        {
                            File.Delete(part);
                            offset = 0;
                        }
                        using (var request = new HttpRequestMessage(HttpMethod.Get, candidate))
                        {
                            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                            using (var response = await SendWithHeaderTimeoutAsync(http, request,
                                FirstRouteHeaderTimeout(requestTimeout, hasAlternative), ct).ConfigureAwait(false))
                            {
                                if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                                {
                                    File.Delete(part);
                                    if (retry + 1 < attempts) continue;
                                }
                                CheckStatus(response);
                                if (response.StatusCode == HttpStatusCode.PartialContent)
                                {
                                    if (offset == 0 || response.Content.Headers.ContentRange?.From != offset)
                                    {
                                        if (File.Exists(part)) File.Delete(part);
                                        throw new IOException("服务器返回了不匹配的断点范围");
                                    }
                                }
                                else offset = 0;
                                long? total = response.Content.Headers.ContentLength + offset;
                                using (var output = new FileStream(part, offset > 0 ? FileMode.Append :
                                    FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                                    await CopyBodyAsync(response, output, MaxFileBytes, offset, total,
                                        progress, idleTimeout, ct).ConfigureAwait(false);
                            }
                        }
                        ct.ThrowIfCancellationRequested();
                        string hash;
                        using (var file = File.OpenRead(part))
                        using (var sha = SHA1.Create())
                            hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                        if (!hash.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(part);
                            throw new InvalidDataException("SHA-1 校验失败");
                        }
                        CommitVerifiedFile(part, fullPath);
                        try { File.Delete(part + ".origin"); }
                        catch (IOException) { /* The verified file has already been committed. */ }
                        RecordRoute(url, candidate, mode, routes.Count);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                                               ex is HttpRequestException || ex is OperationCanceledException)
                    {
                        ct.ThrowIfCancellationRequested();
                        last = ex;
                        failures.Add(new Uri(candidate).Host + " " + RouteFailure(ex));
                        if (ex is InvalidDataException || ex is PermanentRouteException) break;
                        // Give an interrupted transfer one chance to resume on
                        // the same source. A failed connection can switch immediately.
                        if (hasAlternative && (retry > 0 || !File.Exists(part) ||
                            new FileInfo(part).Length == 0)) break;
                        if (retry + 1 < attempts)
                        {
                            int pause = ex is RetryableHttpException limited && limited.RetryAfterMs > 0
                                ? limited.RetryAfterMs : Math.Min(6000, 400 * (1 << retry));
                            await Task.Delay(pause, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            throw new IOException("下载失败（" + string.Join("；", failures.Distinct()) + "）。", last);
        }

        abstract class RouteHttpException : HttpRequestException
        {
            public int Status { get; }
            protected RouteHttpException(int status) : base("HTTP " + status) { Status = status; }
        }

        sealed class PermanentRouteException : RouteHttpException
        {
            public PermanentRouteException(int status) : base(status) { }
        }

        sealed class RetryableHttpException : RouteHttpException
        {
            public int RetryAfterMs { get; }
            public RetryableHttpException(int status, int retryAfterMs) : base(status)
            { RetryAfterMs = retryAfterMs; }
        }

        public void Dispose() => http.Dispose();
    }
}