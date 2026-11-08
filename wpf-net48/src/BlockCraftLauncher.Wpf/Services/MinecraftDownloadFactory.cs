using System;
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
            public MinecraftRouteHandler(LauncherConfig settings) { this.settings = settings; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct)
            {
                var original = request.RequestUri;
                string mirror = original != null && request.Method == HttpMethod.Get
                    ? NetworkDownloader.MirrorMojangUri(original.AbsoluteUri) : null;
                if (mirror == null || settings.NetworkMode == "official")
                    return await base.SendAsync(request, ct);

                // Explicit mirror preference routes known Mojang hosts to BMCLAPI.
                // On unavailable mirror, retry the original official URI.
                string first = settings.NetworkMode == "mirror" ? mirror : original.AbsoluteUri;
                string second = settings.NetworkMode == "mirror" ? original.AbsoluteUri : mirror;
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