using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class CurseForgeInstallSanityChecks
    {
        const string Key = "offline-fixture-key";
        const string Edge = "https://edge.forgecdn.net/files/1/2/";
        const string Media = "https://mediafilez.forgecdn.net/files/1/2/";

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("CurseForge 安装自检：" + message);
        }

        internal static async Task RunAsync()
        {
            string folder = Path.Combine(Path.GetTempPath(), "blockcraft-cf-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var handler = new Fixture();
                using (var client = new CurseForgeMirrorClient(new LauncherConfig { DownloadRetries = 2 },
                    useMirror: false, officialHandler: handler, testKey: Key))
                {
                    var profile = new GameProfile { BaseVersion = "1.21.1", Loader = "Fabric", Directory = folder };
                    var categories = await client.GetCategoriesAsync(CancellationToken.None).ConfigureAwait(false);
                    Require(categories.Count == 1 && categories[0].Id == 409, "只列出模组类别");
                    var search = await client.SearchPageAsync("sodium", profile.BaseVersion, profile.Loader,
                        0, 0, categories[0].Id, CancellationToken.None).ConfigureAwait(false);
                    Require(search.Hits.Count == 1 && search.HasMore && search.Hits[0].Source == "CurseForge",
                        "使用官方分类、版本和服务器分页");
                    var firstPage = await client.ListCompatibleFilesAsync(101, profile, 0,
                        CancellationToken.None).ConfigureAwait(false);
                    var olderPage = await client.ListCompatibleFilesAsync(101, profile, 1,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(firstPage.HasMore && olderPage.Files.Single().VersionId == "1002" &&
                        olderPage.Files[0].RequiredDependencies == 1 && !olderPage.HasMore,
                        "能从后续页选择精确文件，并区分必需和可选前置");

                    var installed = await client.InstallAsync(101, profile, null, CancellationToken.None,
                        1002).ConfigureAwait(false);
                    Require(installed.Select(Path.GetFileName).SequenceEqual(new[] { "dependency.jar", "chosen.jar" }) &&
                        installed.All(File.Exists) && handler.EdgeRequests == 3,
                        "认证下载所选旧版，自动安装必需前置，并重试临时 CDN 故障");
                    int requests = handler.EdgeRequests;
                    var again = await client.InstallAsync(101, profile, null, CancellationToken.None,
                        1002).ConfigureAwait(false);
                    Require(again.Count == 0 && handler.EdgeRequests == requests, "复用校验一致的已有模组和前置");

                    bool corruptRejected = false;
                    try { await client.InstallAsync(101, profile, null, CancellationToken.None, 1003).ConfigureAwait(false); }
                    catch (IOException ex) { corruptRejected = ex.Message.Contains("SHA-1"); }
                    Require(corruptRejected && installed.All(File.Exists) &&
                        Directory.GetFiles(Path.Combine(folder, "mods")).Length == 2 &&
                        Directory.GetDirectories(folder, ".blockcraft-cf-stage-*").Length == 0,
                        "拒绝损坏文件，保留已有模组并清理暂存文件");

                    handler.Restricted = true;
                    requests = handler.EdgeRequests;
                    bool blocked = false;
                    try { await client.InstallAsync(101, profile, null, CancellationToken.None, 1003).ConfigureAwait(false); }
                    catch (InvalidOperationException ex) { blocked = ex.Message.Contains("作者限制"); }
                    Require(blocked && handler.EdgeRequests == requests, "遵守作者的第三方分发设置");
                }
            }
            finally { Directory.Delete(folder, true); }
        }

        sealed class Fixture : HttpMessageHandler
        {
            readonly byte[] jar;
            readonly string hash;
            readonly JavaScriptSerializer json = new JavaScriptSerializer();
            bool retryChosenFile = true;
            public int EdgeRequests { get; private set; }
            public bool Restricted { get; set; }

            public Fixture()
            {
                using (var memory = new MemoryStream())
                {
                    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                    using (var entry = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                        entry.Write("{}");
                    jar = memory.ToArray();
                }
                using (var sha1 = SHA1.Create())
                    hash = BitConverter.ToString(sha1.ComputeHash(jar)).Replace("-", "");
            }

            object ModFile(int project, int id, string name, string url, bool dependencies = false,
                bool corrupt = false) => new
                {
                    id, modId = project, fileName = name, displayName = name, releaseType = 1,
                    gameVersions = new[] { "1.21.1", "Fabric" }, isAvailable = true,
                    hashes = new[] { new { algo = 1, value = corrupt ? new string('0', 40) : hash } },
                    downloadUrl = url,
                    dependencies = dependencies ? new[]
                    {
                        new { modId = 202, relationType = 3 }, new { modId = 303, relationType = 2 }
                    } : null
                };

            HttpResponseMessage Json(object value, int total = 0) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json.Serialize(new { data = value, pagination = new { totalCount = total } }),
                    Encoding.UTF8, "application/json")
            };

            HttpResponseMessage Handle(HttpRequestMessage request)
            {
                Uri uri = request.RequestUri;
                bool hasKey = request.Headers.TryGetValues("x-api-key", out IEnumerable<string> keys);
                if (uri.Host == "mediafilez.forgecdn.net")
                {
                    Require(!hasKey, "重定向后的非 Edge CDN 请求不携带密钥");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jar) };
                }
                Require(hasKey && keys.Single() == Key, "官方元数据和 Edge 请求携带本机密钥");
                if (uri.Host == "edge.forgecdn.net")
                {
                    EdgeRequests++;
                    if (uri.AbsolutePath.EndsWith("/chosen.jar", StringComparison.Ordinal) && retryChosenFile)
                    {
                        retryChosenFile = false;
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri(Media + Path.GetFileName(uri.AbsolutePath));
                    return redirect;
                }

                Require(uri.Host == "api.curseforge.com", "官方流程仅请求官方 API 和 CDN");
                switch (uri.AbsolutePath)
                {
                    case "/v1/categories":
                        return Json(new[] { new { id = 409, name = "Library", classId = 6 },
                            new { id = 410, name = "Pack", classId = 4471 } });
                    case "/v1/mods/search":
                        Require(uri.Query.Contains("categoryId=409") && uri.Query.Contains("gameVersion=1.21.1") &&
                            uri.Query.Contains("modLoaderType=4"), "筛选参数发送给官方 API");
                        return Json(new[] { new { id = 101, name = "Fixture" } }, 21);
                    case "/v1/mods/101":
                        return Json(new { id = 101, name = "Fixture", allowModDistribution = !Restricted });
                    case "/v1/mods/202":
                        return Json(new { id = 202, name = "Dependency", allowModDistribution = true });
                    case "/v1/mods/101/files":
                        if (uri.Query.Contains("index=50"))
                            return Json(new[] { ModFile(101, 1002, "chosen.jar", null, dependencies: true) }, 51);
                        return Json(new[] { ModFile(101, 1001, "latest.jar", Edge + "latest.jar") }, 51);
                    case "/v1/mods/101/files/1002":
                        return Json(ModFile(101, 1002, "chosen.jar", null, dependencies: true));
                    case "/v1/mods/101/files/1002/download-url":
                        return Json(Edge + "chosen.jar");
                    case "/v1/mods/101/files/1003":
                        return Json(ModFile(101, 1003, "corrupt.jar", Edge + "corrupt.jar", corrupt: true));
                    case "/v1/mods/202/files":
                        return Json(new[] { ModFile(202, 2001, "dependency.jar", Edge + "dependency.jar") }, 1);
                    default:
                        throw new InvalidOperationException("模拟 CurseForge API 收到意外请求：" + uri.AbsolutePath);
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(Handle(request));
            }
        }
    }
}
