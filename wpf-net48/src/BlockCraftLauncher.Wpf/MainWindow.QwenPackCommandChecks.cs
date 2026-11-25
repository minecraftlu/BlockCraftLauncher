using System;
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
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        sealed class PackCommandFixture : HttpMessageHandler
        {
            readonly byte[] archive;
            readonly string digest;
            internal int VersionRequests, ArchiveRequests;
            internal readonly TaskCompletionSource<bool> AllowArchive =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal PackCommandFixture()
            {
                using (var bytes = new MemoryStream())
                {
                    using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                    {
                        // The catalog advertises 1.20.1. The actual archive declares 1.19.4.
                        using (var writer = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                            writer.Write("{\"formatVersion\":1,\"game\":\"minecraft\",\"name\":\"Test Pack\",\"dependencies\":{\"minecraft\":\"1.19.4\",\"fabric-loader\":\"0.16.0\"},\"files\":[]}");
                        using (var writer = new StreamWriter(zip.CreateEntry("overrides/config/pack.txt").Open()))
                            writer.Write("This file must never be extracted.");
                    }
                    archive = bytes.ToArray();
                }
                using (var hash = SHA1.Create())
                    digest = BitConverter.ToString(hash.ComputeHash(archive)).Replace("-", "").ToLowerInvariant();
            }

            static HttpResponseMessage Json(object data) => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(new JavaScriptSerializer().Serialize(data), Encoding.UTF8, "application/json")
            };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
            {
                var uri = message.RequestUri;
                if (uri.Host == "api.modrinth.com" && uri.AbsolutePath == "/v2/search")
                {
                    string query = Uri.UnescapeDataString(uri.Query);
                    if (!query.Contains("project_type:modpack") || !query.Contains("versions:1.20.1") ||
                        !query.Contains("categories:fabric")) throw new InvalidDataException("整合包搜索条件丢失：" + query);
                    return Json(new { hits = new[] { new { project_id = "TestPack", slug = "test-pack", title = "Test Pack" } }, total_hits = 1 });
                }
                if (uri.Host == "api.modrinth.com" && uri.AbsolutePath == "/v2/project/TestPack/version")
                {
                    Interlocked.Increment(ref VersionRequests);
                    object Version(string game, string loader, string file) => new { game_versions = new[] { game },
                        loaders = new[] { loader }, files = new[] { new { filename = file, hashes = new { sha1 = digest },
                            url = "https://cdn.modrinth.com/data/TestPack/versions/fixture/" + file } } };
                    return Json(new[] { Version("1.21.1", "fabric", "wrong-game.mrpack"),
                        Version("1.20.1", "forge", "wrong-loader.mrpack"), Version("1.20.1", "fabric", "matched.mrpack") });
                }
                if (uri.Host == "cdn.modrinth.com" && uri.AbsolutePath == "/data/TestPack/versions/fixture/matched.mrpack")
                {
                    Interlocked.Increment(ref ArchiveRequests);
                    await AllowArchive.Task.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
                }
                throw new InvalidDataException("意外的整合包请求：" + uri);
            }
        }

        async Task VerifyQwenPackCommandsAsync()
        {
            using (var fixture = new PackCommandFixture())
            {
                MainWindow window = null;
                window = CreateQwenLiveDownloadWindow(() =>
                    new ModrinthClient(window.settings, new NetworkDownloader(window.settings, fixture)));
                try
                {
                    var original = window.activeProfile;
                    string marker = Path.Combine(original.Directory, "existing-save.txt");
                    File.WriteAllText(marker, "existing");
                    window.Show();
                    await WaitForQwenAsync(() => window.IsLoaded, "加载整合包指令自检窗口");
                    var originalEntries = Directory.GetFileSystemEntries(original.Directory).OrderBy(path => path).ToArray();
                    Task<string> Submit(string text) => window.TryRunLocalQwenExactCommandAsync(text, CancellationToken.None);
                    const string criteria = " 1.20.1 Fabric modpack Test Pack";
                    await Submit("find" + criteria);
                    CheckQwen(window.currentPage == "整合包" && window.packSearchResults.Items.Count == 1 &&
                        window.downloads.Jobs.Count == 0 && fixture.VersionRequests == 0 && fixture.ArchiveRequests == 0,
                        "仅搜索整合包时启动了下载或没有显示项目。");

                    await Submit("install" + criteria);
                    await WaitForQwenAsync(() => fixture.ArchiveRequests == 1, "下载目录中匹配版本的整合包");
                    var job = window.downloads.Jobs.Single();
                    CheckQwen(job.Kind == "整合包" && job.IsRunning && window.settings.Profiles.Single() == original,
                        "加入整合包队列时过早修改了游戏实例。");
                    await Submit("install" + criteria);
                    CheckQwen(window.downloads.Jobs.Count == 1 && fixture.VersionRequests == 1 && fixture.ArchiveRequests == 1,
                        "重复的整合包请求产生了并行安装。");
                    fixture.AllowArchive.TrySetResult(true);
                    await WaitForQwenAsync(() => !job.IsRunning, "检查整合包清单与目录元数据");
                    string instances = Path.Combine(window.settings.Root, "instances");
                    string cache = DownloadPathService.ModpackCache(window.settings.DownloadDirectory);
                    CheckQwen(job.Status == "失败" && (job.Error ?? "").Contains("清单") &&
                        window.settings.Profiles.Single() == original && window.activeProfile == original &&
                        File.ReadAllText(marker) == "existing" &&
                        originalEntries.SequenceEqual(Directory.GetFileSystemEntries(original.Directory).OrderBy(path => path)) &&
                        (!Directory.Exists(instances) || Directory.GetFileSystemEntries(instances).Length == 0) &&
                        (!Directory.Exists(cache) || !Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).Any()),
                        "目录声称兼容、实际清单不匹配的整合包留下了实例、缓存文件或修改了存档。" + job.Error);
                }
                finally
                {
                    fixture.AllowArchive.TrySetResult(true);
                    foreach (var job in window.downloads.Jobs.Where(j => j.IsRunning)) job.Cancel();
                    await WaitForQwenAsync(() => window.downloads.Jobs.All(j => !j.IsRunning), "结束整合包自检任务");
                    window.Close();
                    Directory.Delete(window.qwenLiveFolder, true);
                }
            }
        }
    }
}
