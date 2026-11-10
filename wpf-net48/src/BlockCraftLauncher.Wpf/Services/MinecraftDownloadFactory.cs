using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Installers;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// CmlLib 4.x Minecraft downloader configuration.
    /// The official source remains the fallback for supported BMCLAPI mirrors;
    /// unrelated API and mod URLs are never rewritten.
    /// </summary>
    internal static class MinecraftDownloadFactory
    {
        internal static readonly TimeSpan GameDownloadTimeout = TimeSpan.FromMinutes(15);

        public static MinecraftLauncher Create(MinecraftPath path, LauncherConfig settings)
        {
            var handler = new MinecraftRouteHandler(settings)
            {
                InnerHandler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    MaxConnectionsPerServer = Math.Max(2, Math.Min(32, settings.DownloadThreads)),
                    Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : new WebProxy(settings.Proxy),
                    UseProxy = !string.IsNullOrWhiteSpace(settings.Proxy)
                }
            };
            // Large game jars/assets may legitimately take several minutes on a slow
            // link. Cancellation stays controlled by the install operation.
            var http = new HttpClient(handler) { Timeout = GameDownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.32");
            var p = MinecraftLauncherParameters.CreateDefault(path, http);
            int parallel = Math.Max(2, Math.Min(32, settings.DownloadThreads));
            p.GameInstaller = new ParallelGameInstaller(
                maxChecker: 4, maxDownloader: parallel, boundedCapacity: 2048, http);
            return new MinecraftLauncher(p);
        }

        internal sealed class MinecraftRouteHandler : DelegatingHandler
        {
            readonly LauncherConfig settings;
            static readonly ConcurrentDictionary<string, Preference> History =
                new ConcurrentDictionary<string, Preference>(StringComparer.OrdinalIgnoreCase);
            // A large asset batch must not launch 12–32 duplicate latency
            // probes on the first request for the same official host.
            static readonly ConcurrentDictionary<string, SemaphoreSlim> ProbeLocks =
                new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

            sealed class Preference
            {
                public DateTime Expires { get; set; }
                public bool PreferMirror { get; set; }
            }

            public MinecraftRouteHandler(LauncherConfig settings) { this.settings = settings; }

            // Requests may contain Range/If-Range or an Authorization header.
            // On a different host we forward ONLY public download headers:
            // entity validators and credentials belong to the original origin.
            static readonly HashSet<string> PublicMirrorHeaders =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Range", "Accept", "Accept-Encoding", "Accept-Language",
                    "User-Agent", "Cache-Control", "Pragma"
                };

            internal static HttpRequestMessage CloneDownloadRequest(
                HttpRequestMessage original, string destination)
            {
                if (original == null) throw new ArgumentNullException(nameof(original));
                var destinationUri = new Uri(destination, UriKind.Absolute);
                bool sameOrigin = original.RequestUri != null &&
                    Uri.Compare(original.RequestUri, destinationUri,
                        UriComponents.SchemeAndServer, UriFormat.Unescaped,
                        StringComparison.OrdinalIgnoreCase) == 0;
                var copy = new HttpRequestMessage(original.Method, destinationUri)
                {
                    Version = original.Version
                };
                foreach (var header in original.Headers)
                {
                    if (sameOrigin || PublicMirrorHeaders.Contains(header.Key))
                        copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                // Deliberately don't forward request.Properties, body, cookies,
                // authorization or host-specific ETags to a third-party mirror.
                return copy;
            }

            async Task<long> ProbeAsync(string url, CancellationToken ct)
            {
                try
                {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        linked.CancelAfter(TimeSpan.FromMilliseconds(1400));
                        var timer = Stopwatch.StartNew();
                        using (var head = new HttpRequestMessage(HttpMethod.Head, url))
                        using (var answer = await base.SendAsync(head, linked.Token))
                            return answer.IsSuccessStatusCode ? timer.ElapsedMilliseconds : long.MaxValue;
                    }
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) &&
                                          !ct.IsCancellationRequested)
                {
                    return long.MaxValue;
                }
            }

            async Task<bool> PreferMirrorAsync(string official, string mirror, CancellationToken ct)
            {
                string host = new Uri(official).Host;
                if (History.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow)
                    return cached.PreferMirror;
                var gate = ProbeLocks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    // Another download may have measured the route while this
                    // request waited. Reuse the answer instead of probing again.
                    if (History.TryGetValue(host, out cached) && cached.Expires > DateTime.UtcNow)
                        return cached.PreferMirror;
                    var officialProbe = ProbeAsync(official, ct);
                    var mirrorProbe = ProbeAsync(mirror, ct);
                    await Task.WhenAll(officialProbe, mirrorProbe);
                    long officialMs = officialProbe.Result;
                    long mirrorMs = mirrorProbe.Result;
                    bool prefer = mirrorMs != long.MaxValue &&
                        (officialMs == long.MaxValue || mirrorMs + 60 < officialMs);
                    History[host] = new Preference
                    {
                        PreferMirror = prefer,
                        Expires = DateTime.UtcNow.AddMinutes(10)
                    };
                    return prefer;
                }
                finally { gate.Release(); }
            }

            void MarkMirrorTemporarilySlow(string official)
            {
                // Only server failures and connection timeouts affect host
                // health; missing snapshots (404) are resource-specific.
                History[new Uri(official).Host] = new Preference
                {
                    PreferMirror = false,
                    Expires = DateTime.UtcNow.AddMinutes(3)
                };
            }

            // Some mirrors do not contain every snapshot, installer or
            // library. A missing mirror resource must still try Mojang.
            // A 404 from Mojang itself is authoritative and should be
            // surfaced instead of pretending the file must exist on a mirror.
            internal static bool NeedsFallback(HttpStatusCode status, bool firstIsMirror)
            {
                int code = (int)status;
                if (code == 429 || code >= 500) return true;
                return firstIsMirror &&
                    (status == HttpStatusCode.Unauthorized ||
                     status == HttpStatusCode.Forbidden ||
                     status == HttpStatusCode.NotFound ||
                     status == HttpStatusCode.Gone ||
                     status == HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var original = request.RequestUri;
                // Only copy body-free GET requests. Never transform a request
                // containing content into an incomplete GET.
                string mirror = original != null && request.Method == HttpMethod.Get &&
                    request.Content == null
                    ? NetworkDownloader.MirrorMojangUri(original.AbsoluteUri) : null;
                if (mirror == null || settings.NetworkMode == "official")
                    return await base.SendAsync(request, ct);

                bool mirrorFirst = settings.NetworkMode == "mirror" || settings.NetworkMode == "china";
                if (settings.NetworkMode == "auto")
                    mirrorFirst = await PreferMirrorAsync(original.AbsoluteUri, mirror, ct);

                string first = mirrorFirst ? mirror : original.AbsoluteUri;
                string second = mirrorFirst ? original.AbsoluteUri : mirror;
                try
                {
                    using (var attempt = CloneDownloadRequest(request, first))
                    {
                        var response = await base.SendAsync(attempt, ct);
                        if (response.IsSuccessStatusCode ||
                            !NeedsFallback(response.StatusCode, mirrorFirst))
                            return response;
                        if (mirrorFirst && ((int)response.StatusCode == 429 ||
                            (int)response.StatusCode >= 500))
                            MarkMirrorTemporarilySlow(original.AbsoluteUri);
                        response.Dispose();
                    }
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested)
                {
                    if (mirrorFirst) MarkMirrorTemporarilySlow(original.AbsoluteUri);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (mirrorFirst) MarkMirrorTemporarilySlow(original.AbsoluteUri);
                }
                using (var fallback = CloneDownloadRequest(request, second))
                    return await base.SendAsync(fallback, ct);
            }
        }
    }
}