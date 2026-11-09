using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// In-memory HTTP transport only. Exercises the same CmlLib route handler
    /// used by the real launcher, without DNS, Minecraft downloads or accounts.
    /// </summary>
    internal static class MinecraftRouteSanityChecks
    {
        sealed class Snapshot
        {
            public string Uri;
            public string Range;
            public string UserAgent;
            public bool HasAuthorization;
            public bool HasCookie;
            public bool HasPrivateHeader;
            public bool HasIfRange;
        }

        sealed class FakeHttpHandler : HttpMessageHandler
        {
            readonly Func<int, HttpRequestMessage, Task<HttpResponseMessage>> respond;
            public readonly List<Snapshot> Requests = new List<Snapshot>();

            public FakeHttpHandler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> respond)
            {
                this.respond = respond;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var snapshot = new Snapshot
                {
                    Uri = request.RequestUri.AbsoluteUri,
                    Range = request.Headers.Range?.ToString(),
                    UserAgent = request.Headers.UserAgent.ToString(),
                    HasAuthorization = request.Headers.Authorization != null,
                    HasCookie = request.Headers.Contains("Cookie"),
                    HasPrivateHeader = request.Headers.Contains("X-Private-Token"),
                    HasIfRange = request.Headers.Contains("If-Range")
                };
                Requests.Add(snapshot);
                return respond(Requests.Count, request);
            }
        }

        static void Require(bool condition, string problem)
        {
            if (!condition)
                throw new InvalidOperationException("Minecraft 线路回归自检失败：" + problem);
        }

        static Task<HttpResponseMessage> Reply(HttpStatusCode status)
        {
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(new byte[] { 42 })
            });
        }

        static HttpRequestMessage OriginalRequest(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new RangeHeaderValue(4096, null);
            req.Headers.TryAddWithoutValidation("If-Range", "\"origin-etag\"");
            req.Headers.UserAgent.ParseAdd("BlockCraft-Download-Test/1.0");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "PRIVATE-ACCESS");
            req.Headers.TryAddWithoutValidation("Cookie", "PRIVATE_SESSION=1");
            req.Headers.TryAddWithoutValidation("X-Private-Token", "PRIVATE");
            return req;
        }

        public static async Task RunAsync()
        {
            Require(MinecraftDownloadFactory.GameDownloadTimeout >= TimeSpan.FromMinutes(10),
                "低速大文件不能沿用 45 秒短超时");

            const string original = "https://piston-data.mojang.com/v1/objects/public-game-asset";
            var mock = new FakeHttpHandler((n, req) =>
                Reply(n == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            using (var client = new HttpClient(new MinecraftDownloadFactory.MinecraftRouteHandler(
                new LauncherConfig { NetworkMode = "mirror" }) { InnerHandler = mock }))
            using (var request = OriginalRequest(original))
            using (var response = await client.SendAsync(request).ConfigureAwait(false))
            {
                Require(response.IsSuccessStatusCode, "镜像失败后应回退官方");
                Require(mock.Requests.Count == 2, "503 应当回退一次，且不能重复下载");
                var mirror = mock.Requests[0];
                var fallback = mock.Requests[1];
                Require(mirror.Uri.StartsWith("https://bmclapi2.bangbang93.com/",
                    StringComparison.OrdinalIgnoreCase), "第一条必须是官方认可的 BMCLAPI 映射");
                Require(mirror.Range == "bytes=4096-", "镜像必须保留可恢复的 HTTP Range");
                Require(mirror.UserAgent.Contains("BlockCraft-Download-Test"),
                    "镜像必须保留公共请求头");
                Require(!mirror.HasAuthorization && !mirror.HasCookie
                    && !mirror.HasPrivateHeader && !mirror.HasIfRange,
                    "第三方镜像不能接收官方站点的凭据或源站 ETag");
                Require(fallback.Uri == original && fallback.Range == "bytes=4096-",
                    "官方回退必须恢复原始 Range");
                Require(fallback.HasAuthorization && fallback.HasCookie
                    && fallback.HasPrivateHeader && fallback.HasIfRange,
                    "返回源站时应完整保留原始凭据和 If-Range");
                Require(request.RequestUri.AbsoluteUri == original,
                    "回退不得修改调用方的原始请求");
            }

            // A transient timeout caused by the first CDN is not the user's
            // cancellation. It must use the same safe official fallback.
            var timedOut = new FakeHttpHandler((n, req) =>
                n == 1
                    ? Task.FromException<HttpResponseMessage>(
                        new TaskCanceledException("synthetic CDN timeout"))
                    : Reply(HttpStatusCode.OK));
            using (var client = new HttpClient(new MinecraftDownloadFactory.MinecraftRouteHandler(
                new LauncherConfig { NetworkMode = "mirror" }) { InnerHandler = timedOut }))
            using (var request = OriginalRequest(original))
            using (var response = await client.SendAsync(request).ConfigureAwait(false))
            {
                Require(response.IsSuccessStatusCode && timedOut.Requests.Count == 2,
                    "CDN 超时不应导致官方下载完全失败");
            }

            // Downloading from a third-party API must never be moved to
            // BMCLAPI, including when the user explicitly prefers mirrors.
            var thirdParty = new FakeHttpHandler((n, req) => Reply(HttpStatusCode.OK));
            using (var client = new HttpClient(new MinecraftDownloadFactory.MinecraftRouteHandler(
                new LauncherConfig { NetworkMode = "mirror" }) { InnerHandler = thirdParty }))
            using (var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.modrinth.com/v2/search"))
            using (var response = await client.SendAsync(request).ConfigureAwait(false))
            {
                Require(response.IsSuccessStatusCode && thirdParty.Requests.Count == 1
                    && thirdParty.Requests.Single().Uri ==
                        "https://api.modrinth.com/v2/search",
                    "第三方资源 API 不得被官方游戏镜像重写");
            }
        }
    }
}
