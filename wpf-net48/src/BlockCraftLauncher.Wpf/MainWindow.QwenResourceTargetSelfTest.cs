using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        // In-process Modrinth endpoints return two resources with the same title.
        sealed class AmbiguousResourceCatalog : HttpMessageHandler
        {
            internal static readonly byte[] Zip = new byte[22] { 80,75,5,6,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0 };
            readonly Action transferred;
            internal AmbiguousResourceCatalog(Action transferred) { this.transferred = transferred; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                var url = request.RequestUri;
                object Rows(string id) => new { project_id = id, project_type = "resourcepack", title = "测试材质", slug = "fixture-" + id };
                object data;
                if (url.Host == "api.modrinth.com" && url.AbsolutePath == "/v2/search")
                {
                    if (!Uri.UnescapeDataString(url.Query).Contains("versions:1.21.1"))
                        throw new InvalidDataException("未按目标实例版本筛选资源。");
                    data = new { total_hits = 2, hits = new[] { Rows("One"), Rows("Two") } };
                }
                else if (url.Host == "api.modrinth.com" && url.AbsolutePath == "/v2/project/Two/version")
                {
                    string digest;
                    using (var sha = SHA1.Create())
                        digest = BitConverter.ToString(sha.ComputeHash(Zip)).Replace("-", "").ToLowerInvariant();
                    data = new[] { new { id = "ChosenVersion", game_versions = new[] { "1.21.1" },
                        files = new[] { new { primary = true, filename = "chosen.zip",
                            url = "https://cdn.modrinth.com/data/Two/chosen.zip", hashes = new { sha1 = digest } } } } };
                }
                else if (url.Host == "cdn.modrinth.com" && url.AbsolutePath == "/data/Two/chosen.zip")
                {
                    transferred();
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Zip) });
                }
                else throw new InvalidDataException("意外的资源请求：" + url.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    new JavaScriptSerializer().Serialize(data), Encoding.UTF8, "application/json") });
            }
        }

        internal async Task VerifyQwenResourceTargetAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bcl-resource-target-" + Guid.NewGuid().ToString("N"))).FullName;
            var config = new LauncherConfig { NetworkMode = "official", DownloadRetries = 1 };
            int transfers = 0;
            var window = new MainWindow(null, null, new SettingsStore(Path.Combine(root, "fixture.json")),
                () => new ModrinthClient(config, new NetworkDownloader(config,
                    new AmbiguousResourceCatalog(() => Interlocked.Increment(ref transfers)))))
            { SuppressAutomaticUpdateCheck = true, ShowActivated = false, ShowInTaskbar = false };
            GameProfile Profile(string name, string version) => new GameProfile { Id = name, Name = name,
                BaseVersion = version, LaunchVersion = version, Loader = "Fabric", Directory = Path.Combine(root, name) };
            var current = Profile("生存", "1.20.1");
            var selected = Profile("候选", "1.21.1");
            window.settings.Root = root;
            window.settings.Profiles.Clear();
            window.settings.Profiles.Add(current);
            window.settings.Profiles.Add(selected);
            window.activeProfile = current;
            try
            {
                window.Show();
                await WaitForQwenAsync(() => window.IsLoaded, "加载资源目标自检");
                CheckQwen(LauncherSearchCommand.TryParse("给候选实例安装资源包 测试材质", out var command) &&
                    command.ResourceType == "resourcepack" && command.InstanceName == selected.Name, "未解析具名实例。");
                var result = await window.RunQwenResourceActionAsync(command, CancellationToken.None);
                CheckQwen(result.Contains("未能唯一匹配") && window.downloads.Jobs.Count == 0 &&
                    window.resourceInstallTarget.SelectedItem == selected && window.activeProfile == current,
                    "同名资源应允许在原目标实例中手动选取。");
                window.resourceList.SelectedItem = window.resourceResults.Single(hit => hit.ProjectId == "Two");
                async Task Layout()
                {
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.ArrangeLayoutProbe(960, 600);
                }
                await Layout();
                ClickFixtureAction(FixtureButton(window.PageHost, "查看详情"));
                await Layout();
                ClickFixtureAction(FixtureButton(window.DetailHost, "安装到 候选"));
                await WaitForQwenAsync(() => window.downloads.Jobs.Count == 1 && !window.downloads.Jobs[0].IsRunning, "完成详情页安装");
                var job = window.downloads.Jobs[0];
                CheckQwen(job.Status == "已完成" && File.ReadAllBytes(Path.Combine(selected.Directory, "resourcepacks", "chosen.zip"))
                    .SequenceEqual(AmbiguousResourceCatalog.Zip) && !File.Exists(Path.Combine(current.Directory, "resourcepacks", "chosen.zip")),
                    "资源未安装到指定实例。" + job.Error);
                window.ClearDetails();
                await Layout();
                ClickFixtureAction(FixtureButton(window.PageHost, "安装到目标实例"));
                await WaitForQwenAsync(() => window.downloads.Jobs.Count == 2 && !window.downloads.Jobs[0].IsRunning, "重复列表安装");
                var registry = new ManagedModUpdateService(config);
                CheckQwen(window.downloads.Jobs[0].Status == "已完成" && transfers == 1 &&
                    registry.Load(selected).Single().ProjectId == "Two" && registry.Load(current).Count == 0,
                    "重复安装不应再次传输文件或更改其它实例的登记。");
            }
            finally
            {
                foreach (var job in window.downloads.Jobs.Where(job => job.IsRunning)) job.Cancel();
                window.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
