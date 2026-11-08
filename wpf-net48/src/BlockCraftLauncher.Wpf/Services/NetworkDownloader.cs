using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>Timeout, rate-limit, backup route and verified atomic writes.</summary>
    public sealed class NetworkDownloader : IDisposable
    {
        readonly HttpClient http;
        readonly int attempts;
        public NetworkDownloader(LauncherConfig settings)
        {
            var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            if (!string.IsNullOrWhiteSpace(settings.Proxy)) { handler.UseProxy = true; handler.Proxy = new WebProxy(settings.Proxy); }
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(32) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.27");
            attempts = Math.Max(1, Math.Min(6, settings.DownloadRetries));
        }
        public static string MirrorMojangUri(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
            var path = uri.AbsolutePath;
            string prefix;
            switch (uri.Host.ToLowerInvariant())
            {
                case "piston-meta.mojang.com": case "launchermeta.mojang.com": case "launcher.mojang.com":
                case "piston-data.mojang.com": prefix = ""; break;
                case "libraries.minecraft.net": prefix = "/maven"; break;
                case "resources.download.minecraft.net": prefix = "/assets"; break;
                case "maven.fabricmc.net": prefix = "/maven"; break;
                case "meta.fabricmc.net": prefix = "/fabric-meta"; break;
                default: return null;
            }
            return "https://bmclapi2.bangbang93.com" + prefix + path + uri.Query;
        }
        public async Task<string> GetStringWithFallbackAsync(string officialUrl, string networkMode, CancellationToken ct)
        {
            var mirror = MirrorMojangUri(officialUrl);
            string[] candidates = networkMode == "official" || mirror == null ? new[] { officialUrl }
                : networkMode == "mirror" ? new[] { mirror, officialUrl } : new[] { mirror, officialUrl };
            Exception last = null;
            foreach (var url in candidates)
            {
                for (int i = 0; i < Math.Min(2, attempts); ++i)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using (var response = await http.GetAsync(url, ct))
                        {
                            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                                throw new HttpRequestException("远端暂时不可用：" + response.StatusCode);
                            response.EnsureSuccessStatusCode();
                            return await response.Content.ReadAsStringAsync();
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        last = ex;
                        if (i == 0) await Task.Delay(350, ct);
                    }
                }
            }
            throw new IOException("全部下载线路失败。可更换下载模式或配置代理。", last);
        }
        public async Task DownloadVerifiedAsync(string url, string destination, string sha1, CancellationToken ct, IProgress<long> progress = null)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("仅接受 HTTPS 下载链接");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
            string temp = destination + ".part-" + Guid.NewGuid().ToString("N");
            try
            {
                Exception last = null;
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
                        {
                            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                            {
                                int pause = response.Headers.RetryAfter?.Delta.HasValue == true ?
                                    (int)response.Headers.RetryAfter.Delta.Value.TotalMilliseconds : 700 * (attempt + 1);
                                throw new HttpRequestException("限流或服务故障：" + response.StatusCode + ", RetryAfterMs=" + Math.Max(0, Math.Min(6000, pause)));
                            }
                            response.EnsureSuccessStatusCode();
                            using (var src = await response.Content.ReadAsStreamAsync())
                            using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true))
                            {
                                byte[] buffer = new byte[128 * 1024]; int count; long total = 0;
                                while ((count = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                                {
                                    await dest.WriteAsync(buffer, 0, count, ct);
                                    total += count; progress?.Report(total);
                                }
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(sha1))
                        {
                            using (var stream = File.OpenRead(temp))
                            using (var hasher = SHA1.Create())
                            {
                                var hash = BitConverter.ToString(hasher.ComputeHash(stream)).Replace("-", "");
                                if (!hash.Equals(sha1, StringComparison.OrdinalIgnoreCase)) throw new IOException("SHA-1 校验失败，已拒绝安装");
                            }
                        }
                        // Replacing only after validation avoids losing a good
                        // existing JAR when the new transfer is corrupted.
                        if (File.Exists(destination)) File.Replace(temp, destination, null, true);
                        else File.Move(temp, destination);
                        return;
                    }
                    catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is TaskCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        last = ex;
                        if (attempt + 1 < attempts)
                        {
                            int backoff = 500 * (attempt + 1);
                            var match = System.Text.RegularExpressions.Regex.Match(ex.Message, @"RetryAfterMs=(\d+)");
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int retryMs)) backoff = Math.Max(backoff, retryMs);
                            await Task.Delay(Math.Min(6000, backoff), ct);
                        }
                    }
                }
                throw new IOException("资源下载失败", last);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void Dispose() => http.Dispose();
    }
}
