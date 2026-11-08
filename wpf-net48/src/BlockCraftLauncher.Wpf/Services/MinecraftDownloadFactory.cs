using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.27");
            var p = MinecraftLauncherParameters.CreateDefault(path, http);
            int parallel = Math.Max(2, Math.Min(32, settings.DownloadThreads));
            p.GameInstaller = new ParallelGameInstaller(
                maxChecker: 4, maxDownloader: parallel, boundedCapacity: 2048, http);
            return new MinecraftLauncher(p);
        }

        sealed class MinecraftRouteHandler : DelegatingHandler
        {
            readonly LauncherConfig settings;
            static readonly ConcurrentDictionary<string, Preference> History =
                new ConcurrentDictionary<string, Preference>(StringComparer.OrdinalIgnoreCase);

            sealed class Preference
            {
                public DateTime Expires { get; set; }
                public bool PreferMirror { get; set; }
            }

            public MinecraftRouteHandler(LauncherConfig settings) { this.settings = settings; }

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

                // HEAD probes do not download the entire game asset. They run
                // concurrently and have a 1400ms deadline each.
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

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var original = request.RequestUri;
                string mirror = original != null && request.Method == HttpMethod.Get
                    ? NetworkDownloader.MirrorMojangUri(original.AbsoluteUri) : null;
                if (mirror == null || settings.NetworkMode == "official")
                    return await base.SendAsync(request, ct);

                bool mirrorFirst = settings.NetworkMode == "mirror";
                if (settings.NetworkMode == "auto")
                    mirrorFirst = await PreferMirrorAsync(original.AbsoluteUri, mirror, ct);

                string first = mirrorFirst ? mirror : original.AbsoluteUri;
                string second = mirrorFirst ? original.AbsoluteUri : mirror;
                try
                {
                    using (var attempt = new HttpRequestMessage(HttpMethod.Get, first))
                    {
                        var response = await base.SendAsync(attempt, ct);
                        if (response.IsSuccessStatusCode || (int)response.StatusCode < 500 &&
                            response.StatusCode != (HttpStatusCode)429)
                            return response;
                        response.Dispose();
                    }
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested) { }
                using (var fallback = new HttpRequestMessage(HttpMethod.Get, second))
                    return await base.SendAsync(fallback, ct);
            }
        }
    }
}