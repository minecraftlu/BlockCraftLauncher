using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class LocalQwenImageInstallChecks
    {
        internal static async Task RunAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "BlockCraft-image-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Check(LocalQwenImageInstaller.DownloadBytes(root, false) == 32046180084L, "fresh generation size");
                Check(LocalQwenImageInstaller.DownloadBytes(root, true) == 32149263757L, "fresh edit size");
                Check(!LocalQwenImageInstaller.IsInstalled(root, true), "empty installation");
                var origin = new Uri("https://model.example/model");
                byte[] bytes = Encoding.UTF8.GetBytes("a locally verified model payload");
                string expected;
                using (var hash = SHA256.Create()) expected = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                foreach (bool acceptResume in new[] { true, false })
                {
                    string file = Path.Combine(root, acceptResume ? "resumed" : "restarted");
                    string part = VerifiedModelDownload.PartPath(file, expected);
                    File.WriteAllBytes(part, bytes.Take(6).ToArray());
                    using (var http = new HttpClient(new Handler(request =>
                    {
                        Check(request.Headers.Range.Ranges.Single().From == 6, "range offset");
                        var response = new HttpResponseMessage(acceptResume ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent(acceptResume ? bytes.Skip(6).ToArray() : bytes)
                        };
                        if (acceptResume) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(6, bytes.Length - 1, bytes.Length);
                        return response;
                    })))
                        await VerifiedModelDownload.FetchAsync(http, origin, file, bytes.Length, expected, null, CancellationToken.None);
                    Check(File.ReadAllBytes(file).SequenceEqual(bytes) && !File.Exists(part), "verified final file");
                    using (var http = new HttpClient(new Handler(request => throw new Exception("cached file redownloaded"))))
                        await VerifiedModelDownload.FetchAsync(http, origin, file, bytes.Length, expected, null, CancellationToken.None);
                }
                string failed = Path.Combine(root, "unpublished");
                using (var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new ByteArrayContent(new byte[bytes.Length]) })))
                {
                    bool rejected = false;
                    try { await VerifiedModelDownload.FetchAsync(http, origin, failed, bytes.Length, expected, null, CancellationToken.None); }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !File.Exists(failed) && !File.Exists(VerifiedModelDownload.PartPath(failed, expected)), "hash mismatch");
                }
                string remainder = VerifiedModelDownload.PartPath(failed, expected);
                File.WriteAllBytes(remainder, bytes.Take(3).ToArray());
                using (var http = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(bytes.Skip(8).ToArray()) })))
                {
                    bool rejected = false;
                    try { await VerifiedModelDownload.FetchAsync(http, origin, failed, bytes.Length, expected, null, CancellationToken.None); }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected && new FileInfo(remainder).Length == 3 && !File.Exists(failed), "invalid range must retain prefix");
                }
                using (var http = new HttpClient(new Handler(request =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                    response.Headers.Location = new Uri("http://unsafe.example/file");
                    return response;
                })))
                {
                    bool rejected = false;
                    try { await VerifiedModelDownload.FetchAsync(http, origin, failed, bytes.Length, expected, null, CancellationToken.None); }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected, "insecure redirect");
                    try { await VerifiedModelDownload.FetchAsync(http, origin, failed, bytes.Length, expected, null, new CancellationToken(true)); }
                    catch (OperationCanceledException) { Check(!File.Exists(failed), "canceled download published"); return; }
                    throw new InvalidOperationException("cancellation ignored");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        static void Check(bool success, string name)
        { if (!success) throw new InvalidOperationException("Local model installation check failed: " + name); }

        sealed class Handler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, HttpResponseMessage> callback;
            internal Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) { this.callback = callback; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(callback(request)); }
        }
    }
}
