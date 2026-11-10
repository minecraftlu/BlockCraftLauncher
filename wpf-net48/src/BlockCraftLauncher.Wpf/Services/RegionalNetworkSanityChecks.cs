using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class RegionalNetworkSanityChecks
    {
        const string Official = MinecraftVersionCatalog.Official;
        static readonly string Mirror = NetworkDownloader.MirrorMojangUri(Official);
        static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(150);
        const string FixtureManifest =
            "{\"versions\":[{\"id\":\"1.21.1\",\"type\":\"release\",\"releaseTime\":\"2024-08-08T00:00:00Z\"}]}";

        sealed class ScriptedHandler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply;
            public List<string> Requests { get; } = new List<string>();
            public List<long?> RangeStarts { get; } = new List<long?>();

            public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
            {
                this.reply = reply;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests.Add(request.RequestUri.AbsoluteUri);
                RangeStarts.Add(request.Headers.Range?.Ranges.SingleOrDefault()?.From);
                return reply(request, token);
            }
        }

        sealed class StallingStream : MemoryStream
        {
            public override bool CanSeek => false;

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return 0;
            }
        }

        // Header arrival is fast, but JSON payload starts after the header
        // deadline. This must succeed while each body read meets its own idle
        // deadline; slow cross-border responses are not automatically broken.
        sealed class DelayedFirstReadStream : MemoryStream
        {
            bool firstRead = true;
            public override bool CanSeek => false;

            public DelayedFirstReadStream(string body)
                : base(System.Text.Encoding.UTF8.GetBytes(body)) { }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken token)
            {
                if (firstRead)
                {
                    firstRead = false;
                    await Task.Delay(350, token).ConfigureAwait(false);
                }
                return base.Read(buffer, offset, count);
            }
        }

        static void Require(bool ok, string description)
        {
            if (!ok) throw new InvalidOperationException("网络线路回归测试失败：" + description);
        }

        static Task<HttpResponseMessage> Json(string value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value) });

        static Task<HttpResponseMessage> Binary(byte[] bytes) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        internal static async Task RunAsync()
        {
            Require(NetworkDownloader.Routes(Official, "china").SequenceEqual(new[] { Mirror, Official }) &&
                    NetworkDownloader.Routes(Official, "international").SequenceEqual(new[] { Official, Mirror }),
                "大陆与海外设置保留相反的优先顺序及备用来源");
            Require(NetworkDownloader.Routes(Official, "official").SequenceEqual(new[] { Official }),
                "仅官方源不会访问镜像");
            Require(NetworkDownloader.FirstRouteHeaderTimeout(
                    NetworkDownloader.HeaderTimeout, true) ==
                    NetworkDownloader.AlternateRouteHeaderTimeout &&
                NetworkDownloader.FirstRouteHeaderTimeout(
                    NetworkDownloader.HeaderTimeout, false) ==
                    NetworkDownloader.HeaderTimeout &&
                NetworkDownloader.FirstRouteHeaderTimeout(ShortDeadline, true) == ShortDeadline,
                "双线路首次连接 6 秒内自动回退，单线路和更短超时保持不变");
            Require(NetworkDownloader.TryNormalizeProxy("127.0.0.1:7890", out var proxy) &&
                proxy == "http://127.0.0.1:7890" &&
                !NetworkDownloader.TryNormalizeProxy("http://user:password@localhost:7890/path", out _),
                "下载代理地址保留合法旧配置并拦截无效格式");
            using (var system = NetworkDownloader.CreateHttpHandler(new LauncherConfig()))
            using (var custom = NetworkDownloader.CreateHttpHandler(new LauncherConfig { Proxy = proxy }))
                Require(system.UseProxy && system.Proxy == null && custom.Proxy is WebProxy,
                    "保留 Windows 系统代理，显式代理优先");

            const string fabric = "https://meta.fabricmc.net/v2/versions/loader/1.21.1";
            Require(NetworkDownloader.MirrorMojangUri(fabric) ==
                "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/1.21.1" &&
                NetworkDownloader.MirrorMojangUri("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml") ==
                "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/maven-metadata.xml" &&
                NetworkDownloader.MirrorMojangUri("https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar")
                    ?.StartsWith("https://bmclapi2.bangbang93.com/maven/", StringComparison.Ordinal) == true &&
                NetworkDownloader.MirrorMojangUri("https://maven.quiltmc.org/repository/release/org/quiltmc/loader.jar") ==
                "https://bmclapi2.bangbang93.com/maven/org/quiltmc/loader.jar",
                "不同加载器使用匹配的 Maven 或元数据路径");
            Require(NetworkDownloader.MirrorMojangUri(fabric + "?private=ignored") == null &&
                NetworkDownloader.MirrorCommunityUri("https://cdn.modrinth.com/data/sample?private=ignored") == null,
                "拒绝把非公开查询串传给镜像");

            var badMirror = new ScriptedHandler((request, token) =>
                Json(request.RequestUri.AbsoluteUri == Mirror ? "<html>gateway</html>" : FixtureManifest));
            using (var client = new NetworkDownloader(new LauncherConfig { NetworkMode = "china" }, badMirror))
            {
                var list = await client.GetMetadataWithFallbackAsync(Official, "china",
                    MinecraftVersionCatalog.Parse, CancellationToken.None).ConfigureAwait(false);
                Require(list.Single().Id == "1.21.1" && badMirror.Requests.SequenceEqual(new[] { Mirror, Official }),
                    "镜像返回成功状态的错误网页也会改用官方清单");
            }

            const string slowBodyEndpoint =
                "https://api.modrinth.com/v2/search?query=metadata-body-idle-regression";
            var slowBody = new ScriptedHandler((request, token) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StreamContent(new DelayedFirstReadStream(
                        "{\\"hits\\":[],\\"total_hits\\":0}"))
                }));
            using (var client = new NetworkDownloader(new LauncherConfig {
                NetworkMode = "official", DownloadRetries = 1
            }, slowBody, requestTimeout: ShortDeadline, idleTimeout: TimeSpan.FromSeconds(2)))
            {
                var page = await client.GetMetadataWithFallbackAsync(slowBodyEndpoint,
                    "official", body => ModrinthClient.ParseSearchResponse(body, "mod"),
                    CancellationToken.None).ConfigureAwait(false);
                Require(page.TotalHits == 0 && slowBody.Requests.SequenceEqual(
                    new[] { slowBodyEndpoint }),
                    "元数据响应头及时返回但正文稍慢时，不应被响应头截止时间取消");
            }

            var slowOfficial = new ScriptedHandler(async (request, token) =>
            {
                if (request.RequestUri.AbsoluteUri == Official)
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return await Json(FixtureManifest).ConfigureAwait(false);
            });
            using (var client = new NetworkDownloader(new LauncherConfig(), slowOfficial, ShortDeadline))
            {
                var list = await client.GetMetadataWithFallbackAsync(Official, "auto",
                    MinecraftVersionCatalog.Parse, CancellationToken.None).ConfigureAwait(false);
                Require(list.Count == 1 && slowOfficial.Requests.SequenceEqual(new[] { Official, Mirror }),
                    "官方端点无响应时在首轮切换线路");
                await client.GetMetadataWithFallbackAsync(Official, "auto",
                    MinecraftVersionCatalog.Parse, CancellationToken.None).ConfigureAwait(false);
                Require(slowOfficial.Requests.Count == 3 && slowOfficial.Requests[2] == Mirror,
                    "自动模式短期记住成功的备用线路");
            }

            var gameHandler = new ScriptedHandler(async (request, token) =>
            {
                if (request.RequestUri.Host == "meta.fabricmc.net")
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return await Json("[]").ConfigureAwait(false);
            });
            using (var client = new HttpClient(new MinecraftDownloadFactory.MinecraftRouteHandler(
                new LauncherConfig { NetworkMode = "international" }, headerTimeout: ShortDeadline)
                { InnerHandler = gameHandler }))
            using (var reply = await client.GetAsync(fabric).ConfigureAwait(false))
                Require(reply.IsSuccessStatusCode && gameHandler.Requests.Count == 2 &&
                    gameHandler.Requests[1].Contains("/fabric-meta/"),
                    "CmlLib 加载器客户端在首选线路超时后回退");

            using (var stop = new CancellationTokenSource())
            {
                var cancelled = new ScriptedHandler((request, token) =>
                {
                    stop.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Json(FixtureManifest);
                });
                using (var client = new NetworkDownloader(new LauncherConfig(), cancelled))
                {
                    bool stopped = false;
                    try { await client.GetStringWithFallbackAsync(Official, "international", stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { stopped = true; }
                    Require(stopped && cancelled.Requests.Count == 1, "用户取消后不会再请求备用线路");
                }
            }

            await VerifyPublicSearchCacheAsync().ConfigureAwait(false);
            await VerifyFilesAsync().ConfigureAwait(false);
            VerifyLoaderCatalogs();
        }

        static async Task VerifyPublicSearchCacheAsync()
        {
            const string endpoint = "https://api.modrinth.com/v2/search?limit=20&query=regional-cache-selftest";
            string standard = PublicSearchCache.CacheKey(endpoint, "official", "", false);
            string regional = PublicSearchCache.CacheKey(endpoint, "china", "", true);
            Require(standard != null && regional != null && standard != regional &&
                PublicSearchCache.CacheKey(endpoint, "official", "http://127.0.0.1:7890", false) != standard,
                "资源缓存必须按线路、代理与 MCIM 选项隔离");
            Require(PublicSearchCache.CacheKey(
                    "https://login.live.com/oauth20_authorize.srf", "auto", "", true) == null &&
                PublicSearchCache.CacheKey(
                    "https://api.minecraftservices.com/minecraft/profile", "auto", "", true) == null &&
                PublicSearchCache.CacheKey(
                    "https://api.modrinth.com.evil.invalid/v2/search", "auto", "", true) == null &&
                PublicSearchCache.CacheKey(
                    "http://api.modrinth.com/v2/search", "auto", "", true) == null,
                "登录令牌、伪造域名和不安全 HTTP 绝不能进入搜索缓存");
            Require(PublicSearchCache.CacheKey(
                    "https://mosaicary.com/api/skins?source=published", "skin", "", false) != null &&
                PublicSearchCache.CacheKey(
                    "https://dpplugins.com/api/skins?search=test", "skin", "", false) != null &&
                PublicSearchCache.CacheKey(
                    "https://sessionserver.mojang.com/session/minecraft/profile/test", "skin", "", false) == null,
                "只缓存公开皮肤目录，不缓存玩家账号资料");

            const string payload = "{\"hits\":[],\"total_hits\":0}";
            var handler = new ScriptedHandler((request, token) => Json(payload));
            using (var downloader = new NetworkDownloader(
                new LauncherConfig { NetworkMode = "official" }, handler))
            {
                var first = await downloader.GetMetadataWithFallbackAsync(endpoint, "official",
                    body => ModrinthClient.ParseSearchResponse(body, "resourcepack"),
                    CancellationToken.None).ConfigureAwait(false);
                var second = await downloader.GetMetadataWithFallbackAsync(endpoint, "official",
                    body => ModrinthClient.ParseSearchResponse(body, "resourcepack"),
                    CancellationToken.None).ConfigureAwait(false);
                Require(first.Hits.Count == 0 && second.TotalHits == 0 && handler.Requests.Count == 1,
                    "重复查询资源包只请求网络一次，缓存回放时仍重新解析");
                Require(!ReferenceEquals(first, second),
                    "不共享可变搜索结果，避免跨页修改互相影响");
            }
            PublicSearchCache.Remove(standard);
            PublicSearchCache.Store(standard, "temporary", TimeSpan.Zero);
            Require(!PublicSearchCache.TryGet(standard, out _),
                "缓存到期后必须重新联网");
            PublicSearchCache.Store(standard, new string('x', 600 * 1024));
            Require(!PublicSearchCache.TryGet(standard, out _),
                "拒绝缓存超大响应占用过多内存");
        }

        static async Task VerifyFilesAsync()
        {
            const string url = "https://libraries.minecraft.net/example/test.jar";
            var mirror = NetworkDownloader.MirrorMojangUri(url);
            var bytes = new byte[] { 18, 42, 70, 100 };
            string sha;
            using (var hash = SHA1.Create())
                sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
            string temp = Path.Combine(Path.GetTempPath(), "blockcraft-regional-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string file = Path.Combine(temp, "test.jar");
                var corrupt = new ScriptedHandler((request, token) =>
                    Binary(request.RequestUri.AbsoluteUri == mirror ? new byte[] { 90, 90, 90, 90 } : bytes));
                using (var client = new NetworkDownloader(new LauncherConfig { NetworkMode = "china" }, corrupt))
                    await client.DownloadVerifiedAsync(url, file, sha, CancellationToken.None).ConfigureAwait(false);
                Require(File.ReadAllBytes(file).SequenceEqual(bytes) && corrupt.Requests.SequenceEqual(new[] { mirror, url }),
                    "镜像文件损坏时用官方版本重新校验，损坏字节不进入目标文件");

                File.Delete(file);
                var stall = new ScriptedHandler((request, token) => request.RequestUri.AbsoluteUri == url
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StreamContent(new StallingStream()) })
                    : Binary(bytes));
                using (var client = new NetworkDownloader(new LauncherConfig { NetworkMode = "international" },
                    stall, idleTimeout: ShortDeadline))
                    await client.DownloadVerifiedAsync(url, file, sha, CancellationToken.None).ConfigureAwait(false);
                Require(File.ReadAllBytes(file).SequenceEqual(bytes) && stall.Requests.SequenceEqual(new[] { url, mirror }),
                    "文件下载收到响应后长时间无数据也能换源");

                File.Delete(file);
                string part = file + ".blockcraft.part";
                NetworkDownloader.PreparePartialForRoute(part, url, sha);
                File.WriteAllBytes(part, bytes.Take(2).ToArray());
                var resume = new ScriptedHandler((request, token) =>
                {
                    var content = new ByteArrayContent(bytes.Skip(2).ToArray());
                    content.Headers.ContentRange = new ContentRangeHeaderValue(2, 3, 4);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
                });
                using (var client = new NetworkDownloader(new LauncherConfig { NetworkMode = "official" }, resume))
                    await client.DownloadVerifiedAsync(url, file, sha, CancellationToken.None).ConfigureAwait(false);
                Require(File.ReadAllBytes(file).SequenceEqual(bytes) && resume.RangeStarts.Single() == 2,
                    "断点续传只接入正确偏移，并校验整个文件");

                var path = new MinecraftPath(temp);
                string installedVersion = "offline-probe";
                string metadata = path.GetVersionJsonPath(installedVersion);
                Directory.CreateDirectory(Path.GetDirectoryName(metadata));
                File.WriteAllText(metadata, "{\"id\":\"offline-probe\",\"type\":\"release\"}");
                var launcher = MinecraftDownloadFactory.CreateLocal(path);
                var installed = await launcher.GetAllVersionsAsync().ConfigureAwait(false);
                Require(installed.Any(x => x.Name == installedVersion), "本地启动可以枚举版本，无需远程清单");
            }
            finally { Directory.Delete(temp, true); }
        }

        static void VerifyLoaderCatalogs()
        {
            const string forge = "[{\"mcversion\":\"1.21.1\",\"version\":\"52.1.0\",\"modified\":\"2025-01-01\"," +
                "\"files\":[{\"category\":\"installer\",\"format\":\"jar\",\"hash\":\"0123456789012345678901234567890123456789\"}]}," +
                "{\"mcversion\":\"1.21.1\",\"version\":\"../invalid\",\"files\":[{\"category\":\"installer\",\"format\":\"jar\"}]}]";
            var build = LoaderNetworkCatalog.ParseForgeMirror(forge, "1.21.1").Single();
            Require(build.GetInstallerFile()?.DirectUrl ==
                "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.1.0/forge-1.21.1-52.1.0-installer.jar",
                "Forge 镜像列表映射至受支持的安装文件");
            const string neo = "<metadata><versioning><versions><version>21.1.90</version>" +
                "<version>21.1.102</version><version>21.3.10</version><version>bad</version>" +
                "</versions></versioning></metadata>";
            Require(LoaderNetworkCatalog.ParseNeoForgeMetadata(neo, "1.21.1").NeoForgeVersionName == "21.1.102",
                "NeoForge Maven 清单按 Minecraft 版本选择正确构建");
        }
    }
}
