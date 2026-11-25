using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        // The catalog, metadata and JAR responses all stay in process. Use the
        // ordinary CF download fixture for verified file bytes and dependencies.
        sealed class QwenDownloadCatalog : HttpMessageHandler
        {
            readonly CurseForgeInstallSanityChecks.Fixture files = new CurseForgeInstallSanityChecks.Fixture();
            public int SearchCount, CompletedSearches;
            public string LastSearch;
            public bool Ambiguous;
            public TaskCompletionSource<bool> SearchGate;
            public TaskCompletionSource<bool> FileGate;

            static HttpResponseMessage Page(object entries, int count) =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new JavaScriptSerializer().Serialize(new
                    {
                        data = entries, pagination = new { totalCount = count }
                    }), Encoding.UTF8, "application/json")
                };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Uri uri = request.RequestUri;
                if (uri.Host == "api.curseforge.com" && uri.AbsolutePath == "/v1/mods/search")
                {
                    LastSearch = Uri.UnescapeDataString(uri.Query);
                    Interlocked.Increment(ref SearchCount);
                    // Deliberately return late even when the view has been closed.
                    var gate = SearchGate;
                    if (gate != null) await gate.Task.ConfigureAwait(false);
                    object Project(int id) => new { id, name = "Sodium", slug = "sodium-" + id, gameId = 432,
                        classId = 6, allowModDistribution = true, summary = "适用于 Fabric 的渲染优化模组" };
                    var projects = Ambiguous ? new[] { Project(101), Project(102) } : new[] { Project(101) };
                    Interlocked.Increment(ref CompletedSearches);
                    return Page(projects, projects.Length);
                }
                if (uri.Host == "api.curseforge.com" && uri.AbsolutePath == "/v1/mods/101/files")
                {
                    return Page(new[] { new
                    {
                        id = 1002, modId = 101, gameId = 432, fileName = "chosen.jar", displayName = "Fabric 1.21.1",
                        releaseType = 1, isAvailable = true, fileDate = "2026-01-01T00:00:00Z",
                        gameVersions = new[] { "Fabric", "1.21.1" },
                        hashes = new[] { new { algo = 1, value = files.ExpectedHash("chosen.jar") } },
                        dependencies = new[] { new { modId = 202, relationType = 3 } }
                    } }, 1);
                }
                if (uri.AbsolutePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && FileGate != null)
                    await FileGate.Task.ConfigureAwait(false);
                using (var inner = new HttpMessageInvoker(files, disposeHandler: false))
                    return await inner.SendAsync(request, ct).ConfigureAwait(false);
            }
        }

        internal async Task<string> VerifyQwenDownloadsAsync()
        {
            const string qwenKey = "DASHSCOPE_API_KEY", cfKey = "BLOCKCRAFT_CURSEFORGE_API_KEY";
            string oldQwen = Environment.GetEnvironmentVariable(qwenKey), oldCf = Environment.GetEnvironmentVariable(cfKey);
            string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                "blockcraft-qwen-download-" + Guid.NewGuid().ToString("N"))).FullName;
            var catalog = new QwenDownloadCatalog();
            MainWindow window = null;
            try
            {
                Environment.SetEnvironmentVariable(qwenKey, null);
                Environment.SetEnvironmentVariable(cfKey, CurseForgeInstallSanityChecks.Key);
                window = new MainWindow(mirror => mirror
                    ? throw new InvalidOperationException("指令测试意外切换了来源。")
                    : new CurseForgeMirrorClient(new LauncherConfig { DownloadRetries = 2 }, false, catalog,
                        CurseForgeInstallSanityChecks.Key), (ct, refresh) => Task.FromResult(true),
                    new SettingsStore(Path.Combine(folder, "settings.test.json")))
                {
                    SuppressAutomaticUpdateCheck = true, ShowActivated = false, ShowInTaskbar = false,
                    qwenSettingsPath = Path.Combine(folder, "qwen.json"), qwenHistoryPath = Path.Combine(folder, "history.json"),
                    qwenWebPageFactory = OfflineQwenWebPage,
                    preferredModSource = ModSource.CurseForge
                };
                window.settings.Root = folder;
                window.settings.DownloadDirectory = Path.Combine(folder, "downloads");
                window.settings.Profiles.Clear();
                var profile = new GameProfile { Id = "qwen-fixture", Name = "生存", BaseVersion = "1.21.1",
                    LaunchVersion = "fabric-1.21.1", Loader = "Fabric", Directory = Path.Combine(folder, "survival") };
                Directory.CreateDirectory(profile.Directory);
                window.settings.Profiles.Add(profile);
                window.activeProfile = profile;
                window.gameVersions.Add(new MinecraftVersionEntry { Id = "1.20.4", Type = "release" });
                window.gameVersions.Add(new MinecraftVersionEntry { Id = "1.21.1", Type = "release", IsLatestRelease = true });
                window.gameVersions.Add(new MinecraftVersionEntry { Id = "24w01a", Type = "snapshot" });
                CheckQwen(MatchQwenGameVersion(window.gameVersions, "latest")?.Id == "1.21.1",
                    "最新正式版没有使用索引的正式版标识。");
                var installedVersions = new List<string>();
                window.qwenInstallGameFixture = async (version, loader, progress, bytes, token) =>
                {
                    installedVersions.Add(version + "/" + loader);
                    await Task.Delay(Timeout.Infinite, token);
                    throw new InvalidOperationException("游戏测试只允许取消模拟下载。");
                };
                int aiCalls = 0;
                string suggested = "{\"keyword\":\"Sodium\"}";
                window.qwenRunChat = (turns, key, endpoint, model, web, onDelta, ct) =>
                {
                    aiCalls++;
                    throw new InvalidOperationException("下载指令意外进入普通聊天。");
                };
                window.qwenRunSearch = (question, key, endpoint, model, ct) =>
                {
                    aiCalls++;
                    return Task.FromResult(new QwenSearchResult(suggested, Array.Empty<QwenSearchSource>()));
                };
                window.Show();
                await WaitForQwenAsync(() => window.IsLoaded, "加载指令自检窗口");
                async Task Layout()
                {
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.ArrangeLayoutProbe(960, 600);
                }
                async Task Submit(string text)
                {
                    if (window.currentPage != QwenSearchPage)
                    {
                        ClickFixtureAction(window.TopLaunchButton);
                        await Layout();
                        ClickFixtureAction(FixtureButton(window.PageHost, NavLabel(QwenSearchPage)));
                        await Layout();
                        window.SelectQwenFixtureChat();
                    }
                    window.qwenQuery.Text = text;
                    ClickFixtureAction(window.qwenSubmit);
                    await Layout();
                }

                await Layout();
                await Submit("找 Minecraft 1.20.4");
                CheckQwen(window.currentPage == "游戏版本" && window.versionQuery.Text == "1.20.4" &&
                    (window.versionList.SelectedItem as MinecraftVersionEntry)?.Id == "1.20.4" &&
                    window.downloads.Jobs.Count == 0, "搜索游戏版本未定位正确的候选项。");
                await Submit("下载 Minecraft 1.20.4");
                await WaitForQwenAsync(() => installedVersions.Count == 1, "创建游戏安装任务");
                var game = window.downloads.Jobs.Single();
                CheckQwen(game.IsRunning && game.Kind == "游戏" && installedVersions[0] == "1.20.4/原版",
                    "下载指令未使用游戏安装队列或选错版本。");
                var history = window.qwenConversations.Current.Turns;
                CheckQwen(history.Count == 2 && history[0].Question == "找 Minecraft 1.20.4" &&
                    history[1].Question == "下载 Minecraft 1.20.4" && history[1].Answer.Contains("已加入下载任务") &&
                    window.qwenAnswer.Text == history[1].Answer && !window.qwenRegenerate.IsEnabled,
                    "回到对话后缺少指令记录，或允许重新回答再次执行下载。");
                await Submit("下载 Minecraft 1.20.4");
                CheckQwen(window.downloads.Jobs.Count == 1 && installedVersions.Count == 1,
                    "重复的游戏指令创建了第二个任务。");
                game.Cancel();
                await WaitForQwenAsync(() => game.Status == "已取消", "取消游戏安装任务");
                window.qwenInstallGameFixture = (version, loader, progress, bytes, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    string metadata = Path.Combine(folder, "versions", version, version + ".json");
                    Directory.CreateDirectory(Path.GetDirectoryName(metadata));
                    File.WriteAllText(metadata, "{}");
                    return Task.FromResult(MinecraftInstanceLocator.ImportVersionMetadata(metadata));
                };
                await Submit("下载 Minecraft 1.20.4");
                await WaitForQwenAsync(() => window.downloads.Jobs.Count == 2 && !window.downloads.Jobs[0].IsRunning,
                    "完成游戏任务并创建实例");
                CheckQwen(window.downloads.Jobs[0].Status == "已完成" && window.activeProfile?.LaunchVersion == "1.20.4" &&
                    window.store.Load().Profiles.Any(item => item.LaunchVersion == "1.20.4"),
                    "游戏完成后未登记或未保存实例。");
                await Submit("下载 Minecraft 1.20.4");
                int initialJobs = window.downloads.Jobs.Count;
                CheckQwen(initialJobs == 2, "再次请求已安装的游戏生成了重复任务。");
                window.activeProfile = profile;
                await Submit("给生存实例安装钠到实例创造");
                CheckQwen(window.currentPage == QwenSearchPage && catalog.SearchCount == 0 &&
                    window.downloads.Jobs.Count == initialJobs && aiCalls == 0, "冲突目标错误触发了下载或模型调用。");

                catalog.Ambiguous = true;
                await Submit("给当前实例装钠");
                await WaitForQwenAsync(() => window.resourceResults.Count == 2, "显示重名模组候选");
                await Layout();
                CheckQwen(window.currentPage == "资源中心" && window.resourceList.SelectedItem == null &&
                    ReferenceEquals(window.modTarget.SelectedItem, profile) && window.downloads.Jobs.Count == initialJobs &&
                    catalog.LastSearch.IndexOf("sodium", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    catalog.LastSearch.Contains("gameVersion=1.21.1") && catalog.LastSearch.Contains("modLoaderType=4"),
                    "模组指令未使用实例筛选或自动安装了歧义项目。");
                window.PageScroll.ScrollToTop();
                window.ArrangeLayoutProbe(960, 600, "qwen-download-action");

                catalog.Ambiguous = false;
                await Submit("搜索模组 Sodium");
                await WaitForQwenAsync(() => window.resourceResults.Count == 1, "仅搜索模组");
                CheckQwen(window.downloads.Jobs.Count == initialJobs, "仅搜索指令触发了安装。");
                await Submit("给当前实例装钠");
                await WaitForQwenAsync(() => window.downloads.Jobs.Count == initialJobs + 1, "将兼容模组加入下载队列");
                var mod = window.downloads.Jobs.First();
                await WaitForQwenAsync(() => !mod.IsRunning, "安装模组与必需前置");
                CheckQwen(mod.Status == "已完成" && File.Exists(Path.Combine(profile.Directory, "mods", "chosen.jar")) &&
                    File.Exists(Path.Combine(profile.Directory, "mods", "dependency.jar")),
                    "模组或必需前置未安装到目标实例：" + mod.Error);
                CheckQwen(aiCalls == 0, "明确的下载安装指令调用了付费模型。");

                int before = catalog.SearchCount;
                catalog.SearchGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                await Submit("给当前实例装钠");
                await WaitForQwenAsync(() => catalog.SearchCount > before, "等待延迟的模组响应");
                ClickFixtureAction(window.TopLaunchButton);
                catalog.SearchGate.SetResult(true);
                catalog.SearchGate = null;
                await WaitForQwenAsync(() => catalog.CompletedSearches > before, "放行延迟响应");
                await Layout();
                CheckQwen(window.currentPage == "首页" && window.downloads.Jobs.Count == initialJobs + 1,
                    "离开页面后仍然执行了延迟返回的下载指令。");

                window.qwenProvider.SelectedIndex = 1;
                await Submit("让千问帮我找个提高帧率的模组");
                CheckQwen(aiCalls == 0 && window.currentPage == QwenSearchPage, "未设置密钥时调用了推荐模型。");
                window.qwenApiKey.Password = "offline-recommendation-fixture";
                await Submit("让千问帮我找个提高帧率的模组");
                await WaitForQwenAsync(() => window.currentPage == "资源中心" && window.resourceResults.Count == 1,
                    "推荐词进入启动器模组列表");
                CheckQwen(aiCalls == 1 && window.downloads.Jobs.Count == initialJobs + 1,
                    "模型推荐直接执行了安装或重复调用模型。");
                suggested = "{\"keyword\":\"https://example.invalid/unsafe.jar\"}";
                before = catalog.SearchCount;
                await Submit("让千问帮我找个提高帧率的模组");
                await WaitForQwenAsync(() => window.qwenSubmit.IsEnabled, "拒绝非项目推荐");
                CheckQwen(aiCalls == 2 && window.currentPage == QwenSearchPage && catalog.SearchCount == before &&
                    window.downloads.Jobs.Count == initialJobs + 1, "模型输出的网址触发了搜索或安装。");
                await Submit("帮我下载一个可以自动收菜的模组");
                await WaitForQwenAsync(() => window.qwenSubmit.IsEnabled, "拒绝直接安装模型返回的网址");
                CheckQwen(aiCalls == 3 && window.currentPage == QwenSearchPage && catalog.SearchCount == before &&
                    window.downloads.Jobs.Count == initialJobs + 1, "下载请求使用了目录以外的网址。");
                suggested = "{\"keyword\":\"Sodium\"}";
                var second = new GameProfile { Id = "qwen-second", Name = "空白实例", BaseVersion = profile.BaseVersion,
                    LaunchVersion = profile.LaunchVersion, Loader = profile.Loader, Directory = Path.Combine(folder, "second") };
                Directory.CreateDirectory(second.Directory);
                window.settings.Profiles.Add(second);
                window.activeProfile = second;
                catalog.FileGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                const string installRecommendation = "帮我下载一个提升帧率的模组";
                await Submit(installRecommendation);
                await WaitForQwenAsync(() => window.downloads.Jobs.Count == initialJobs + 2, "推荐项目进入新实例的下载队列");
                var recommended = window.downloads.Jobs[0];
                CheckQwen(recommended.IsRunning && recommended.Title.Contains(second.Name), "用途指令没有定位新实例。");
                await Submit(installRecommendation);
                await WaitForQwenAsync(() => window.modSourceStatus.Text.Contains("已有"), "相同推荐复用正在进行的任务");
                CheckQwen(window.downloads.Jobs.Count == initialJobs + 2 && ReferenceEquals(window.downloads.Jobs[0], recommended),
                    "重复用途指令创建了并行下载任务。");
                catalog.FileGate.SetResult(true);
                await WaitForQwenAsync(() => !recommended.IsRunning, "完成用途指令安装");
                CheckQwen(recommended.Status == "已完成" && File.Exists(Path.Combine(second.Directory, "mods", "chosen.jar")) &&
                    File.Exists(Path.Combine(second.Directory, "mods", "dependency.jar")),
                    "描述需求后未将选定的项目及其前置安装到新实例。" + recommended.Error);
                await VerifyQwenPackCommandsAsync();
                await VerifyQwenResourceTargetAsync();
                return "Passed native game search/queue/cancel/deduplication, ambiguous and compatible mod commands, " +
                    "CurseForge dependency installation, stale result suppression, search-only AI recommendations, " +
                    "explicit semantic installs with active task deduplication, and native modpack commands.";
            }
            finally
            {
                catalog.SearchGate?.TrySetResult(true);
                catalog.FileGate?.TrySetResult(true);
                if (window != null)
                {
                    foreach (var job in window.downloads.Jobs.Where(job => job.IsRunning)) job.Cancel();
                    window.Close();
                }
                Environment.SetEnvironmentVariable(qwenKey, oldQwen);
                Environment.SetEnvironmentVariable(cfKey, oldCf);
                catalog.Dispose();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
