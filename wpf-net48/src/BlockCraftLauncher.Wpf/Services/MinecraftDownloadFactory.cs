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
using CmlLib.Core.Files;
using CmlLib.Core.VersionLoader;
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

        public static Session Create(MinecraftPath path, LauncherConfig settings,
            CancellationToken token = default)
        {
            var handler = new MinecraftRouteHandler(settings, token)
            {
                InnerHandler = NetworkDownloader.CreateHttpHandler(settings)
            };
            var http = new HttpClient(handler) { Timeout = GameDownloadTimeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.34");
            var files = new NetworkDownloader(settings);
            var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
            int parallel = Math.Max(2, Math.Min(32, settings.DownloadThreads));
            parameters.GameInstaller = new VerifiedGameInstaller(parallel, http, files);
            return new Session(new MinecraftLauncher(parameters), http, files);
        }

        public static MinecraftLauncher CreateLocal(MinecraftPath path)
        {
            var parameters = MinecraftLauncherParameters.CreateDefault(path);
            parameters.VersionLoader = new LocalJsonVersionLoader(path);
            return new MinecraftLauncher(parameters);
        }

        internal sealed class Session : IDisposable
        {
            public MinecraftLauncher Launcher { get; }
            public HttpClient Http { get; }
            readonly NetworkDownloader files;

            public Session(MinecraftLauncher launcher, HttpClient http, NetworkDownloader files)
            {
                Launcher = launcher;
                Http = http;
                this.files = files;
            }

            public void Dispose()
            {
                files.Dispose();
                Http.Dispose();
            }
        }

        sealed class VerifiedGameInstaller : ParallelGameInstaller
        {
            readonly NetworkDownloader files;

            public VerifiedGameInstaller(int parallel, HttpClient http, NetworkDownloader files)
                // CmlLib retains the file set for deduplication. Let its producer
                // finish enqueueing even when a worker faults mid-install.
                : base(maxChecker: 4, maxDownloader: parallel, boundedCapacity: int.MaxValue, http)
            {
                this.files = files;
            }

            protected override Task Download(GameFile file, IProgress<ByteProgress> progress,
                CancellationToken cancellationToken)
            {
                if (!NetworkDownloader.ValidSha1(file.Hash) ||
                    !Uri.TryCreate(file.Url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
                    return base.Download(file, progress, cancellationToken);

                IProgress<DownloadBytes> transfer = progress == null ? null :
                    new GameProgress(progress, file.Size);
                return files.DownloadVerifiedDetailedAsync(file.Url, file.Path, file.Hash,
                    cancellationToken, transfer);
            }
        }

        sealed class GameProgress : IProgress<DownloadBytes>
        {
            readonly IProgress<ByteProgress> progress;
            readonly long expected;
            public GameProgress(IProgress<ByteProgress> progress, long expected)
            {
                this.progress = progress;
                this.expected = expected;
            }

            public void Report(DownloadBytes value) =>
                progress.Report(new ByteProgress(value.Total > 0 ? value.Total : expected, value.Completed));
        }

        internal sealed class MinecraftRouteHandler : DelegatingHandler
        {
            readonly string mode, proxy;
            readonly CancellationToken operationToken;
            readonly TimeSpan headerTimeout;
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

            public MinecraftRouteHandler(LauncherConfig settings, CancellationToken operationToken = default,
                TimeSpan? headerTimeout = null)
            {
                mode = NetworkDownloader.NormalizeMode(settings.NetworkMode);
                proxy = settings.Proxy ?? "";
                this.operationToken = operationToken;
                this.headerTimeout = headerTimeout ?? NetworkDownloader.HeaderTimeout;
            }

            string CacheKey(string url) => proxy + "|" + new Uri(url).Host;

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
                string host = CacheKey(official);
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
                History[CacheKey(official)] = new Preference
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
                if (code == 408 || code == 429 || code >= 500) return true;
                return firstIsMirror &&
                    (status == HttpStatusCode.Unauthorized ||
                     status == HttpStatusCode.Forbidden ||
                     status == HttpStatusCode.NotFound ||
                     status == HttpStatusCode.Gone ||
                     status == HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            async Task<HttpResponseMessage> SendAttemptAsync(HttpRequestMessage request, CancellationToken ct)
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    deadline.CancelAfter(headerTimeout);
                    try { return await base.SendAsync(request, deadline.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        throw new HttpRequestException("等待 " + request.RequestUri.Host + " 响应超时", ex);
                    }
                }
            }

            static bool IsGatewayPage(HttpResponseMessage response) =>
                string.Equals(response.Content?.Headers.ContentType?.MediaType,
                    "text/html", StringComparison.OrdinalIgnoreCase);

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken callerToken)
            {
                using (var operation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, operationToken))
                {
                    var ct = operation.Token;
                    ct.ThrowIfCancellationRequested();
                    return await SendRoutedAsync(request, ct).ConfigureAwait(false);
                }
            }

            async Task<HttpResponseMessage> SendRoutedAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var original = request.RequestUri;
                string mirror = original != null && request.Method == HttpMethod.Get && request.Content == null
                    ? NetworkDownloader.MirrorMojangUri(original.AbsoluteUri) : null;
                if (mirror == null || mode == "official")
                    return await SendAttemptAsync(request, ct).ConfigureAwait(false);

                bool mirrorFirst = mode == "china";
                if (mode == "auto")
                    mirrorFirst = await PreferMirrorAsync(original.AbsoluteUri, mirror, ct).ConfigureAwait(false);

                string first = mirrorFirst ? mirror : original.AbsoluteUri;
                string second = mirrorFirst ? original.AbsoluteUri : mirror;
                try
                {
                    using (var attempt = CloneDownloadRequest(request, first))
                    {
                        var response = await SendAttemptAsync(attempt, ct).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode && !IsGatewayPage(response) ||
                            !response.IsSuccessStatusCode && !NeedsFallback(response.StatusCode, mirrorFirst))
                            return response;
                        if (mirrorFirst && ((int)response.StatusCode == 429 ||
                            (int)response.StatusCode >= 500 || IsGatewayPage(response)))
                            MarkMirrorTemporarilySlow(original.AbsoluteUri);
                        response.Dispose();
                    }
                }
                catch (HttpRequestException) when (!ct.IsCancellationRequested)
                {
                    if (mirrorFirst) MarkMirrorTemporarilySlow(original.AbsoluteUri);
                }
                ct.ThrowIfCancellationRequested();
                using (var fallback = CloneDownloadRequest(request, second))
                {
                    var response = await SendAttemptAsync(fallback, ct).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && IsGatewayPage(response))
                    {
                        response.Dispose();
                        throw new HttpRequestException("游戏文件来源返回了网页，请检查下载线路。");
                    }
                    return response;
                }
            }
        }
    }
}
