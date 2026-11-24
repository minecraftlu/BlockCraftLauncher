using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class CurseForgeServiceDiscovery
    {
        const string Manifest = "https://raw.githubusercontent.com/minecraftlu/BlockCraftLauncher/curseforge-service-config/service.json";
        const int MaxBytes = 4096;
        internal static readonly CurseForgeServiceDiscovery Project = new CurseForgeServiceDiscovery();
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly Func<LauncherConfig, HttpMessageHandler> createHandler;
        readonly Func<DateTime> utcNow;
        CachedResult result;

        sealed class CachedResult
        {
            internal Uri Origin;
            internal DateTime Expires;
        }

        internal CurseForgeServiceDiscovery(Func<LauncherConfig, HttpMessageHandler> createHandler = null,
            Func<DateTime> utcNow = null)
        {
            this.createHandler = createHandler ?? (settings => NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        bool IsFresh(CachedResult value) => value != null && value.Expires > utcNow();
        internal Uri FreshOrigin
        {
            get
            {
                var value = Volatile.Read(ref result);
                return IsFresh(value) ? value.Origin : null;
            }
        }

        internal async Task<Uri> ResolveAsync(LauncherConfig settings, CancellationToken ct, bool refresh = false)
        {
            ct.ThrowIfCancellationRequested();
            var before = Volatile.Read(ref result);
            if (IsFresh(before) && (!refresh || before.Origin != null)) return before.Origin;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = Volatile.Read(ref result);
                if (IsFresh(current) && (!refresh || current.Origin != null || current != before))
                    return current.Origin;
                var origin = await ReadManifestAsync(settings, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Volatile.Write(ref result, new CachedResult {
                    Origin = origin, Expires = utcNow().AddSeconds(origin == null ? 30 : 300)
                });
                return origin;
            }
            finally { gate.Release(); }
        }

        async Task<Uri> ReadManifestAsync(LauncherConfig settings, CancellationToken ct)
        {
            try
            {
                using (var http = new HttpClient(createHandler(settings)))
                using (var request = new HttpRequestMessage(HttpMethod.Get, Manifest))
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(TimeSpan.FromSeconds(7));
                    request.Headers.UserAgent.ParseAdd("BlockCraftLauncher-CurseForge/1.0");
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        limit.Token).ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        if (response.StatusCode != HttpStatusCode.OK || response.Content == null ||
                            response.Content.Headers.ContentLength > MaxBytes) return null;
                        await NetworkDownloader.CopyBodyAsync(response, buffer, MaxBytes, 0,
                            response.Content.Headers.ContentLength, null, TimeSpan.FromSeconds(3), limit.Token)
                            .ConfigureAwait(false);
                        var json = new JavaScriptSerializer { MaxJsonLength = MaxBytes, RecursionLimit = 4 };
                        var data = json.DeserializeObject(Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF'))
                            as Dictionary<string, object>;
                        if (data == null || !data.TryGetValue("serviceUrl", out object address) ||
                            !(address is string url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var origin) ||
                            !CurseForgeMirrorClient.IsServiceOrigin(origin) || origin.HostNameType != UriHostNameType.Dns ||
                            origin.IsLoopback || !origin.Host.Contains(".") ||
                            origin.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                            origin.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                            return null;
                        return origin;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                ex is HttpRequestException || ex is ArgumentException || ex is InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }
        }
    }
}
