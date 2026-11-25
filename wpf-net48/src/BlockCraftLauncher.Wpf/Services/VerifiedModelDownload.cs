using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    // Model files routinely exceed the launcher's game-file size limit. Each call
    // supplies the exact expected byte length and the publisher's pinned SHA-256.
    internal static class VerifiedModelDownload
    {
        internal static async Task FetchAsync(Uri source, string path, long size, string sha256,
            LauncherConfig settings, IProgress<string> progress, CancellationToken ct)
        {
            using (var handler = NetworkDownloader.CreateHttpHandler(settings, false))
            {
                handler.AutomaticDecompression = DecompressionMethods.None;
                using (var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher/LocalModels");
                    await FetchAsync(http, source, path, size, sha256, progress, ct).ConfigureAwait(false);
                }
            }
        }

        // Injectable transport for the offline regression tests.
        internal static async Task FetchAsync(HttpClient http, Uri source, string path, long size,
            string sha256, IProgress<string> progress, CancellationToken ct)
        {
            if (size <= 0 || sha256 == null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("模型文件缺少有效的长度或 SHA-256。");
            if (!IsSecure(source)) throw new ArgumentException("模型下载需要 HTTPS。");
            ct.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            RejectReparsePoints(directory);
            Directory.CreateDirectory(directory);
            string name = Path.GetFileName(path);
            string part = PartPath(path, sha256);
            foreach (string candidate in new[] { path, part, path + ".download.lock" })
                RejectReparsePoints(candidate);
            using (var lease = new FileStream(path + ".download.lock", FileMode.OpenOrCreate,
                FileAccess.Write, FileShare.None))
            {
                if (await MatchesAsync(path, size, sha256, progress, ct).ConfigureAwait(false)) return;
                long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (offset > size) { File.Delete(part); offset = 0; }
                if (offset < size)
                {
                    using (var response = await OpenAsync(http, source, offset, ct).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.OK) offset = 0;
                        else if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var range = response.Content.Headers.ContentRange;
                            if (range == null || range.Unit != "bytes" || range.From != offset ||
                                range.To != size - 1 || range.Length != size)
                                throw new InvalidDataException("服务器返回的续传范围与已下载部分不一致。");
                        }
                        else throw new HttpRequestException("下载失败，HTTP " + (int)response.StatusCode + "。");
                        long? length = response.Content.Headers.ContentLength;
                        if (length.HasValue && length.Value != size - offset)
                            throw new InvalidDataException("下载长度与模型清单不一致。");
                        using (var file = new FileStream(part, offset == 0 ? FileMode.Create : FileMode.Open,
                            FileAccess.Write, FileShare.Read, 128 * 1024, true))
                        {
                            file.Position = offset;
                            await NetworkDownloader.CopyBodyAsync(response, file, size, offset, size,
                                new TransferProgress(name, progress), TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                        }
                    }
                }
                if (!await MatchesAsync(part, size, sha256, progress, ct).ConfigureAwait(false))
                {
                    File.Delete(part);
                    throw new InvalidDataException(name + " 的 SHA-256 校验失败，请重试下载。");
                }
                ct.ThrowIfCancellationRequested();
                NetworkDownloader.CommitVerifiedFile(part, path);
            }
        }

        internal static string PartPath(string path, string digest) => path + "." + digest.ToLowerInvariant() + ".part";

        internal static void RejectReparsePoints(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(current); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("请选择不含符号链接或目录联接的本机模型目录。");
            }
        }

        static bool IsSecure(Uri uri) => uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
            && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

        static async Task<HttpResponseMessage> OpenAsync(HttpClient http, Uri source, long offset,
            CancellationToken ct)
        {
            var next = source;
            for (int redirect = 0; redirect <= 8; redirect++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, next))
                {
                    if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
                    var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(http, request,
                        TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
                    int code = (int)response.StatusCode;
                    if (code != 301 && code != 302 && code != 303 && code != 307 && code != 308)
                        return response;
                    using (response)
                    {
                        Uri target;
                        if (response.Headers.Location == null ||
                            !Uri.TryCreate(next, response.Headers.Location, out target) || !IsSecure(target))
                            throw new InvalidDataException("模型下载服务器返回了无效的跳转地址。");
                        next = target;
                    }
                }
            }
            throw new InvalidDataException("模型下载重定向次数过多。");
        }

        static Task<bool> MatchesAsync(string path, long size, string digest, IProgress<string> progress,
            CancellationToken ct)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != size) return Task.FromResult(false);
            return Task.Run(() =>
            {
                progress?.Report("正在校验 SHA-256 · " + Path.GetFileName(path).Split(new[] { "." + digest }, StringSplitOptions.None)[0]);
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                using (var hash = SHA256.Create())
                {
                    byte[] buffer = new byte[1024 * 1024];
                    int count;
                    while ((count = file.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        hash.TransformBlock(buffer, 0, count, null, 0);
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return string.Equals(BitConverter.ToString(hash.Hash).Replace("-", ""), digest, StringComparison.OrdinalIgnoreCase);
                }
            }, ct);
        }

        sealed class TransferProgress : IProgress<DownloadBytes>
        {
            readonly string name;
            readonly IProgress<string> progress;
            readonly Stopwatch watch = Stopwatch.StartNew();
            long nextReport;
            internal TransferProgress(string name, IProgress<string> progress) { this.name = name; this.progress = progress; }
            public void Report(DownloadBytes value)
            {
                if (progress == null || (watch.ElapsedMilliseconds < nextReport && value.Completed != value.Total)) return;
                nextReport = watch.ElapsedMilliseconds + 500;
                progress.Report(string.Format("正在下载 {0} · {1:F2} / {2:F2} GB", name,
                    value.Completed / 1e9, value.Total / 1e9));
            }
        }
    }
}
