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
        internal const string Key = "offline-fixture-key";
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
                    useMirror: false, officialHandler: handler, testKey: Key, serviceOrigin: Fixture.OperatorOrigin))
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
                    var choice = (CurseForgeDownloadFile)olderPage.Files[0];
                    var names = await client.GetDependencyNamesAsync(choice.RequiredProjects,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(names.Count == 1 && names[202] == "Dependency",
                        "批量查询所选文件的必需前置名称");

                    var installed = await client.InstallAsync(101, profile, null, CancellationToken.None,
                        1002).ConfigureAwait(false);
                    Require(installed.Select(Path.GetFileName).SequenceEqual(new[] { "dependency.jar", "chosen.jar" }) &&
                        installed.All(File.Exists) && handler.EdgeRequests == 3,
                        "认证下载所选旧版，自动安装必需前置，并重试临时 CDN 故障");
                    int requests = handler.EdgeRequests;
                    var again = await client.InstallAsync(101, profile, null, CancellationToken.None,
                        1002).ConfigureAwait(false);
                    Require(again.Count == 0 && handler.EdgeRequests == requests, "复用校验一致的已有模组和前置");

                    var browse = await client.ListCompatibleFilesAsync(101, null, 1,
                        CancellationToken.None).ConfigureAwait(false);
                    Require(browse.Files.Single().VersionId == "1002", "无实例时仍可浏览文件");
                    string savedJar = Path.Combine(folder, "saved.jar");
                    await client.SaveFileAsync(101, 1002, savedJar, null, CancellationToken.None).ConfigureAwait(false);
                    Require(File.ReadAllBytes(savedJar).SequenceEqual(File.ReadAllBytes(installed.Last())) &&
                        Directory.GetFiles(Path.Combine(folder, "mods")).Length == 2,
                        "单独保存校验通过的 JAR，无需向实例写入文件");

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
                await CheckOperatorAsync(Path.Combine(folder, "operator")).ConfigureAwait(false);
                await CurseForgeServiceDiscoverySanityChecks.RunAsync().ConfigureAwait(false);
            }
            finally { Directory.Delete(folder, true); }
        }

        static async Task CheckOperatorAsync(string root)
        {
            var fixture = new Fixture { OperatorOnly = true };
            var profile = new GameProfile { BaseVersion = "1.21.1", Loader = "Fabric", Directory = root };
            Directory.CreateDirectory(root);
            var token = CancellationToken.None;
            using (var client = new CurseForgeMirrorClient(new LauncherConfig { DownloadRetries = 1 },
                false, fixture, null, Fixture.OperatorOrigin))
            {
                var search = await client.SearchPageAsync("sodium", profile.BaseVersion, profile.Loader,
                    0, 0, 409, token).ConfigureAwait(false);
                var project = search.Hits.Single();
                Require(project.DistributionAllowed && project.Id == 101, "公开服务返回获准分发的 Minecraft 模组");
                var files = await client.ListCompatibleFilesAsync(project.Id, profile, 1, token).ConfigureAwait(false);
                var chosen = (CurseForgeDownloadFile)files.Files.Single();
                var names = await client.GetDependencyNamesAsync(chosen.RequiredProjects, token).ConfigureAwait(false);
                Require(names.Count == 1 && names[202] == "Dependency", "公开服务可读取必需前置名称");
                var installed = await client.InstallAsync(project.Id, profile, null, token,
                    long.Parse(chosen.VersionId)).ConfigureAwait(false);
                Require(installed.Select(Path.GetFileName).SequenceEqual(new[] { "dependency.jar", "chosen.jar" }) &&
                    fixture.BinaryRequests.SequenceEqual(new[] { 2001, 1002 }), "仅从精确文件服务地址下载必需前置和所选模组");
                foreach (var path in installed)
                    using (var sha = SHA1.Create())
                    using (var input = File.OpenRead(path))
                        Require(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "") ==
                            fixture.ExpectedHash(Path.GetFileName(path)), "公开服务安装结果与官方摘要匹配");

                fixture.Restricted = true;
                try
                {
                    await client.InstallAsync(101, profile, null, token, 1003).ConfigureAwait(false);
                    throw new Exception("公开服务放行了限制分发的项目。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("作者限制")) { }
                Require(fixture.BinaryRequests.Count == 2, "限制分发的项目没有发起文件传输");
                fixture.Restricted = false;
                fixture.OmitPermission = true;
                try
                {
                    await client.InstallAsync(101, profile, null, token, 1003).ConfigureAwait(false);
                    throw new Exception("公开服务放行了未明确允许分发的项目。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("作者限制")) { }
                Require(fixture.BinaryRequests.Count == 2, "未明确允许分发的项目没有发起文件传输");
                fixture.OmitPermission = false;
                fixture.WrongProject = true;
                try
                {
                    await client.ListCompatibleFilesAsync(101, profile, 1, token).ConfigureAwait(false);
                    throw new Exception("公开服务显示了其他项目的文件。");
                }
                catch (InvalidDataException error) when (error.Message.Contains("项目")) { }
                fixture.WrongProject = false;
                try
                {
                    await client.InstallAsync(101, profile, null, token, 1003).ConfigureAwait(false);
                    throw new Exception("公开服务放行了错误的文件摘要。");
                }
                catch (IOException error) when (error.Message.Contains("SHA-1")) { }
                fixture.RedirectBinary = true;
                try
                {
                    await client.InstallAsync(101, profile, null, token, 1003).ConfigureAwait(false);
                    throw new Exception("公开服务文件请求接受了外部重定向。");
                }
                catch (IOException error) when (error.Message.Contains("重定向")) { }
                catch (InvalidDataException error) when (error.Message.Contains("重定向")) { }
                fixture.MetadataStatus = 401;
                try
                {
                    await client.GetCategoriesAsync(token).ConfigureAwait(false);
                    throw new Exception("公开服务授权失败后仍返回成功。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("401") || error.Message.Contains("授权")) { }
                fixture.MetadataStatus = 429;
                try
                {
                    await client.GetCategoriesAsync(token).ConfigureAwait(false);
                    throw new Exception("公开服务的限流未反馈给用户。");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("429") && error.Message.Contains("19")) { }
                Require(Directory.GetFiles(Path.Combine(root, "mods")).Length == 2 && installed.All(File.Exists) &&
                    Directory.GetDirectories(root, ".blockcraft-cf-stage-*").Length == 0, "服务失败保留已有文件并清理暂存目录");
            }
        }

        internal sealed class Fixture : HttpMessageHandler
        {
            internal static readonly Uri OperatorOrigin = new Uri("https://operator.example");
            internal bool OperatorOnly, RedirectBinary, WrongProject, OmitPermission;
            internal int MetadataStatus;
            internal readonly List<int> BinaryRequests = new List<int>();
            readonly byte[] jar = MakeJar("fixture");
            readonly byte[] dependency = MakeJar("dependency");
            readonly JavaScriptSerializer json = new JavaScriptSerializer();
            bool retryChosenFile = true;
            public int EdgeRequests { get; private set; }
            public bool Restricted { get; set; }

            static byte[] MakeJar(string id)
            {
                using (var memory = new MemoryStream())
                {
                    using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
                    using (var entry = new StreamWriter(zip.CreateEntry("fabric.mod.json").Open()))
                        entry.Write("{\"id\":\"" + id + "\"}");
                    return memory.ToArray();
                }
            }

            internal string ExpectedHash(string name)
            {
                using (var sha1 = SHA1.Create())
                    return BitConverter.ToString(sha1.ComputeHash(name == "dependency.jar" ? dependency : jar)).Replace("-", "");
            }

            object ModFile(int project, int id, string name, string url, bool dependencies = false,
                bool corrupt = false) => new
                {
                    id, gameId = 432, modId = WrongProject ? 909 : project, fileName = name, displayName = name, releaseType = 1,
                    gameVersions = new[] { "1.21.1", "Fabric" }, isAvailable = true,
                    hashes = new[] { new { algo = 1, value = corrupt ? new string('0', 40) : ExpectedHash(name) } },
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
                if (OperatorOnly)
                {
                    Require(uri.Scheme == "https" && uri.Host == OperatorOrigin.Host && !hasKey &&
                        request.Headers.Authorization == null && !request.Headers.Contains("Cookie"),
                        "公开服务请求不携带私有凭据，失败不回退至其他来源");
                    if (MetadataStatus != 0)
                    {
                        var reply = new HttpResponseMessage((HttpStatusCode)MetadataStatus);
                        if (MetadataStatus == 429) reply.Headers.TryAddWithoutValidation("Retry-After", "19");
                        return reply;
                    }
                    if (uri.AbsolutePath.EndsWith("/download", StringComparison.Ordinal))
                    {
                        Require(request.Method == HttpMethod.Get && uri.Query == "", "下载服务使用限定的 GET 文件路由");
                        string path = uri.AbsolutePath;
                        int id = path == "/v1/mods/101/files/1002/download" ? 1002 :
                            path == "/v1/mods/202/files/2001/download" ? 2001 :
                            path == "/v1/mods/101/files/1003/download" ? 1003 : 0;
                        Require(id != 0, "文件路由同时匹配项目和文件 ID");
                        BinaryRequests.Add(id);
                        if (RedirectBinary)
                        {
                            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                            redirect.Headers.Location = new Uri("https://other.example/redirect.jar");
                            return redirect;
                        }
                        return new HttpResponseMessage(HttpStatusCode.OK)
                            { Content = new ByteArrayContent(id == 2001 ? dependency : jar) };
                    }
                }
                if (uri.Host == "mediafilez.forgecdn.net")
                {
                    Require(!hasKey, "重定向后的非 Edge CDN 请求不携带密钥");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(
                        uri.AbsolutePath.EndsWith("/dependency.jar", StringComparison.Ordinal) ? dependency : jar) };
                }
                if (!OperatorOnly) Require(hasKey && keys.Single() == Key, "官方元数据和 Edge 请求携带本机密钥");
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

                if (!OperatorOnly) Require(uri.Host == "api.curseforge.com", "官方流程仅请求官方 API 和 CDN");
                switch (uri.AbsolutePath)
                {
                    case "/v1/categories":
                        return Json(new[] { new { id = 409, name = "Library", classId = 6 },
                            new { id = 410, name = "Pack", classId = 4471 } });
                    case "/v1/mods/search":
                        Require(uri.Query.Contains("categoryId=409") && uri.Query.Contains("gameVersion=1.21.1") &&
                            uri.Query.Contains("modLoaderType=4"), "筛选参数发送给官方 API");
                        return Json(new[] { new { id = 101, name = "Fixture", gameId = 432,
                            classId = 6, allowModDistribution = true } }, 21);
                    case "/v1/mods":
                        Require(request.Method == HttpMethod.Post &&
                            request.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"modIds\":[202]"),
                            "批量元数据请求只携带必需前置的项目 ID");
                        return Json(new[] { new { id = 202, name = "Dependency", gameId = 432,
                            classId = 6, allowModDistribution = true } });
                    case "/v1/mods/101":
                        if (OmitPermission) return Json(new { id = 101, gameId = 432, classId = 6, name = "Fixture" });
                        return Json(new { id = 101, gameId = 432, classId = 6, name = "Fixture", allowModDistribution = !Restricted });
                    case "/v1/mods/202":
                        return Json(new { id = 202, gameId = 432, classId = 6, name = "Dependency", allowModDistribution = true });
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

