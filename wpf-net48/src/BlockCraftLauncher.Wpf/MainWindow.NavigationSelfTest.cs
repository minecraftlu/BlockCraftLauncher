using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        async Task VerifyLocalPackQueueAsync()
        {
            var archive = Path.Combine(Path.GetTempPath(), "blockcraft-queue-" + Guid.NewGuid().ToString("N") + ".mrpack");
            const string invalidArchive = "offline fixture: invalid Modrinth archive";
            File.WriteAllText(archive, invalidArchive);
            var releaseSlots = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var jobs = new List<DownloadJob>();
            var before = activeProfile;
            int profiles = settings.Profiles.Count;
            var view = PageHost.Content;
            async Task WaitForIdle(IEnumerable<DownloadJob> pending)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (pending.Any(job => job.IsRunning))
                {
                    if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("整合包队列检查超时。");
                    await Task.Delay(20);
                }
            }
            try
            {
                // Hold the queue's maximum six slots so cancellation always precedes archive I/O.
                for (int i = 0; i < 6; i++)
                    jobs.Add(downloads.Enqueue("自检", "占用下载槽", (token, stage, bytes) => releaseSlots.Task));
                var pack = QueueMrpackInstall("本地整合包自检", archive);
                jobs.Add(pack);
                if (pack.Kind != "整合包" || pack.Status != "排队中") throw new InvalidOperationException("本地整合包未进入任务队列。");
                pack.Cancel();
                await WaitForIdle(new[] { pack });
                if (pack.Status != "已取消" || !pack.CanRetry)
                    throw new InvalidOperationException("排队中的整合包无法取消和重试。");
                releaseSlots.SetResult(true);
                await WaitForIdle(jobs);
                downloads.Retry(pack);
                await WaitForIdle(new[] { pack });
                if (pack.Status != "失败" || string.IsNullOrWhiteSpace(pack.Error) || !pack.CanRetry ||
                    File.ReadAllText(archive) != invalidArchive || settings.Profiles.Count != profiles ||
                    !ReferenceEquals(activeProfile, before) || !ReferenceEquals(PageHost.Content, view))
                    throw new InvalidOperationException("整合包重试未报告错误，或改动了用户文件、实例及页面。");
            }
            finally
            {
                releaseSlots.TrySetResult(true);
                foreach (var job in jobs) downloads.Jobs.Remove(job);
                File.Delete(archive);
            }
        }

        // The fixture supplies offline data only. Every transition after the
        // initial Home reset is dispatched through an ordinary user action.
        internal void VerifyRedesignedNavigation()
        {
            string originalTheme = settings.Theme;
            int originalZoom = settings.Zoom;
            var originalProfile = activeProfile;
            var profile = new GameProfile {
                Id = "navigation-fixture", Name = "Minecraft 1.21.1", Loader = "vanilla",
                BaseVersion = "1.21.1", LaunchVersion = "1.21.1",
                Directory = Path.Combine(Path.GetTempPath(), "blockcraft-ui-" + Guid.NewGuid().ToString("N"))
            };
            bool wasSuppressed = SuppressAutomaticUpdateCheck;
            SuppressAutomaticUpdateCheck = true;
            qwenSettingsPath = Path.Combine(profile.Directory, "qwen-settings.json");
            qwenHistoryPath = Path.Combine(profile.Directory, "qwen-history.json");
            qwenWebPageFactory = OfflineQwenWebPage;
            bool dialogs = Environment.GetEnvironmentVariable("BLOCKCRAFT_UI_DIALOG_PROBE") == "1";
            settings.Profiles.Add(profile);
            activeProfile = profile;
            try
            {
                settings.Zoom = 100;
                ApplyZoom();
                foreach (bool dark in new[] { false, true })
                {
                    string theme = dark ? "dark-" : "light-";
                    settings.Theme = dark ? "深色" : "浅色";
                    ApplyTheme();
                    int width = dark ? 850 : 960, height = dark ? 560 : 600;
                    void Arrange() => ArrangeLayoutProbe(width, height);
                    void Visit(string page, string id, Button action = null)
                    {
                        if (action != null) ClickFixtureAction(action);
                        if (currentPage != page || !(PageHost.Content is FrameworkElement content))
                            throw new InvalidOperationException("导航未打开页面：" + page);
                        ArrangeLayoutProbe(width, height, theme + id);
                        if (content.ActualWidth < 80 || content.ActualHeight <= 0 ||
                            PageScroll.ViewportWidth <= 0 ||
                            PageScroll.ExtentWidth > PageScroll.ViewportWidth + 2)
                            throw new InvalidOperationException("页面没有布局或存在横向溢出：" + page);
                    }
                    void Side(string page, string id)
                    {
                        if (!sidebarButtons.TryGetValue(page, out var action))
                            throw new InvalidOperationException("缺少到页面的导航：" + page);
                        Visit(page, id, action);
                    }
                    void Mode(string page, string id, string parent, string label)
                    {
                        SelectFixtureMode(PageHost, label);
                        Visit(page, id);
                        VerifyFixtureMode(PageHost, label);
                        VerifyFixtureSidebar(parent);
                    }

                    Navigate("首页");
                    Visit("首页", "home");
                    ClickFixtureAction(FixtureButton(PageHost, NavLabel(QwenSearchPage)));
                    Arrange();
                    CheckQwen(currentPage == QwenSearchPage && ReferenceEquals(PageHost.Content, qwenPage) &&
                        qwenSelectedTab == QwenAssistantTab.Chat && qwenChatBody.Visibility == Visibility.Visible &&
                        qwenChatBody.IsAncestorOf(qwenQuery) && qwenQuery.ActualHeight > 0, "首页入口没有打开统一对话。");
                    qwenTools.IsExpanded = true;
                    Arrange();
                    ClickFixtureAction(qwenWebTab);
                    Arrange();
                    var webOrigin = qwenWebBody.TranslatePoint(new Point(), PageScroll);
                    if (PageScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled ||
                        qwenWebBody.ActualHeight < PageScroll.ViewportHeight - 90 ||
                        qwenWebBody.ActualWidth < PageScroll.ViewportWidth - 4 ||
                        webOrigin.Y + qwenWebBody.ActualHeight > PageScroll.ActualHeight + 1)
                        throw new InvalidOperationException("千问官网未填满窗口内容区或底部被裁切。");
                    SelectQwenFixtureChat();
                    Visit("千问 AI 搜索", "qwen-search");
                    VerifyFixtureSidebar("千问 AI 搜索");
                    Visit("首页", "home", TopLaunchButton);
                    ClickFixtureAction(FixtureButton(PageHost, "CurseForge 官方"));
                    Arrange();
                    if (currentPage != "资源中心" || SelectedModSource != ModSource.CurseForge)
                        throw new InvalidOperationException("首页快捷入口未打开模组页的 CurseForge 官方来源。");
                    VerifyFixtureSidebar("资源中心");
                    Visit("首页", "home", TopLaunchButton);
                    if (!dark && dialogs)
                    {
                        var offline = FixtureVisuals((FrameworkElement)Content).OfType<RadioButton>()
                            .First(radio => Equals(radio.Content, "离线"));
                        offline.IsChecked = true;
                        Arrange();
                        CaptureFixtureDialog(
                            () => ClickFixtureAction(FixtureButton((FrameworkElement)Content, "更改玩家名", "离线登录")),
                            "dialog-account", dialog =>
                            {
                                var field = FixtureVisuals(dialog).OfType<TextBox>()
                                    .Single(item => AutomationProperties.GetName(item) == "玩家名");
                                field.Text = "!";
                                ClickFixtureAction(FixtureButton(dialog, "使用此玩家名"));
                                if (!dialog.IsVisible || !FixtureVisuals(dialog).OfType<TextBlock>()
                                        .Any(t => t.Text.Contains("请检查")))
                                    throw new InvalidOperationException("离线账号未显示无效玩家名提示。");
                            });
                    }
                    Visit("版本选择", "versions", FixtureButton((FrameworkElement)Content, "版本选择"));
                    if (!ReferenceEquals(profileList.SelectedItem, profile))
                        throw new InvalidOperationException("版本选择未关联当前实例。");
                    Visit("版本设置", "version-settings", FixtureButton(PageHost, "版本设置"));
                    Side("模组更新", "installed-mods");
                    Side("存档管理", "saves");
                    ClickFixtureAction(BackButton);
                    if (currentPage != "版本选择") throw new InvalidOperationException("实例设置返回目标错误。");
                    Arrange();
                    ClickFixtureAction(BackButton);
                    if (currentPage != "首页") throw new InvalidOperationException("版本选择返回目标错误。");
                    Arrange();

                    Visit("游戏版本", "game-download", TopDownloadButton);
                    VerifyFixtureMode(PageHost, "自动安装");
                    versionQuery.Text = "1.21";
                    versionLoader.SelectedItem = "Fabric";
                    Mode("手动安装", "manual-install", "游戏版本", "本地导入");
                    Mode("游戏版本", "game-download", "游戏版本", "自动安装");
                    if (versionQuery.Text != "1.21" || !Equals(versionLoader.SelectedItem, "Fabric"))
                        throw new InvalidOperationException("切换安装方式后丢失版本筛选或加载器。");
                    versionQuery.Clear();
                    versionLoader.SelectedIndex = 0;
                    Side("资源中心", "mods");
                    resourceSource.SelectedIndex = (int)ModSource.Modrinth;
                    if (!FixtureVisuals(PageHost).OfType<TextBox>().Contains(resourceQuery) ||
                        AutomationProperties.GetName(resourceQuery) != "模组名称或关键词")
                        throw new InvalidOperationException("模组搜索框无法通过当前页面访问。");
                    var demoMod = new ResourceHit { Name = "林地地图", Kind = "mod", ProjectId = "ui-demo-map",
                        Author = "界面示例", CategoryLabel = "地图", LoaderLabel = "Fabric", VersionsLabel = "1.21.1",
                        Description = "在地图上标记村庄、林地和探索路线。" };
                    resourceResults.Add(demoMod);
                    resourceResults.Add(new ResourceHit { Name = "行囊整理", Kind = "mod", ProjectId = "ui-demo-inventory",
                        Author = "界面示例", CategoryLabel = "实用工具", LoaderLabel = "Fabric", VersionsLabel = "1.21.1",
                        Description = "按类型整理背包中的物品。" });
                    resourceCounter.Text = "界面预览 · 2 项示例";
                    resourceList.SelectedItem = demoMod;
                    Arrange();
                    ClickFixtureAction(FixtureButton(PageHost, "查看文件"));
                    if (!FixtureVisuals(modDetailsHost).OfType<TextBlock>().Any(t => t.Text == demoMod.Name))
                        throw new InvalidOperationException("选择模组后未显示对应的项目详情。");
                    PageScroll.ScrollToTop();
                    ArrangeLayoutProbe(width, height, theme + "mods");
                    var modWorkspace = PageHost.Content;
                    resourceSource.SelectedIndex = (int)ModSource.CurseForge;
                    Visit("资源中心", "curseforge");
                    VerifyFixtureSidebar("资源中心");
                    if (!ReferenceEquals(modWorkspace, PageHost.Content))
                        throw new InvalidOperationException("更换模组下载源重建了页面。");
                    if (SelectedModSource != ModSource.CurseForge)
                        throw new InvalidOperationException("CurseForge 入口未选择官方来源。");
                    if (resourceSearchButton.IsEnabled != CurseForgeMirrorClient.HasOfficialAccess)
                        throw new InvalidOperationException("CurseForge 搜索按钮与官方服务可用状态不一致。");
                    if (!resourceSearchButton.IsEnabled &&
                        (modSourceStatus.Visibility != Visibility.Visible || !modSourceStatus.Text.Contains("CurseForge") ||
                         resourceCounter.Visibility != Visibility.Visible || string.IsNullOrWhiteSpace(resourceCounter.Text) ||
                         checkOfficialService.Visibility != Visibility.Visible || !checkOfficialService.IsEnabled ||
                         browseOfficialMods.Visibility != Visibility.Visible || !browseOfficialMods.IsEnabled ||
                         !FixtureButton(officialSourceActions, "下载 CurseForge 客户端").IsEnabled))
                        throw new InvalidOperationException("官方服务不可用时缺少提示、重试、官网入口或客户端下载操作。");
                    Side("资源包", "resource-packs");
                    var sample = new ResourceHit { Name = "山间村落", Kind = "resourcepack",
                        ProjectId = "offline-fixture", Author = "BlockCraft", Description = "适用于 1.21.1 的资源包。" };
                    resourceResults.Add(sample);
                    resourceList.SelectedItem = sample;
                    Arrange();
                    ClickFixtureAction(FixtureButton(PageHost, "查看详情"));
                    if (DetailHost.Content == null || DetailHost.Visibility != Visibility.Visible)
                        throw new InvalidOperationException("选中资源后未打开项目详情。");
                    ArrangeLayoutProbe(width, height, theme + "resource-detail");
                    ClickFixtureAction(BackButton);
                    if (DetailHost.Content != null || currentPage != "资源包" || resourceList.SelectedItem != sample)
                        throw new InvalidOperationException("项目详情返回后丢失列表或选中项目。");
                    Arrange();
                    Side("光影引擎", "shaders");
                    Side("整合包", "modpacks");
                    Side("皮肤下载", "skins");
                    VerifyFixtureMode(PageHost, "下载皮肤");
                    if (!dark && dialogs)
                        skinStudio.VerifyOnlinePreviewLayout(() =>
                        {
                            Arrange();
                            CaptureFixtureDialog(() => ClickFixtureAction(FixtureButton(PageHost, "放大预览")),
                                "dialog-skin", dialog =>
                                {
                                    if (!FixtureVisuals(dialog).OfType<Skin3DPreview>().Any())
                                        throw new InvalidOperationException("放大皮肤预览未显示 3D 模型。");
                                });
                        });
                    Mode("皮肤库", "skin-library", "皮肤下载", "我的皮肤");
                    Visit("下载管理", "tasks", TaskShortcut);
                    Visit("多人联机", "servers", TopMultiplayerButton);
                    VerifyFixtureMode(PageHost, "收藏");
                    var server = new ServerEntry { Name = "村庄", Address = "village.example.invalid:25565" };
                    serverList.ItemsSource = new[] { server };
                    serverList.SelectedItem = server;
                    var save = FixtureButton(PageHost, "保存收藏");
                    var remove = FixtureButton(PageHost, "删除收藏");
                    var join = FixtureButton(PageHost, "加入服务器");
                    if (!save.IsEnabled || !remove.IsEnabled || !join.IsEnabled || serverAddress.Text != server.Address)
                        throw new InvalidOperationException("收藏选择未启用操作或填入对应地址。");
                    ClickFixtureAction(FixtureButton(PageHost, "新增服务器"));
                    if (save.IsEnabled || remove.IsEnabled || join.IsEnabled || serverList.SelectedItem != null)
                        throw new InvalidOperationException("清除服务器选择后仍可操作空记录。");
                    serverAddress.Text = server.Address;
                    if (!join.IsEnabled || save.IsEnabled || remove.IsEnabled)
                        throw new InvalidOperationException("手动输入地址与收藏名称的状态混淆。");
                    Mode("查找服务器", "server-search", "多人联机", "发现");
                    serverFinderQuery.Text = "村庄";
                    Mode("局域网", "lan", "多人联机", "局域网");
                    var joinWorld = FixtureButton(PageHost, "加入世界");
                    lanWorldList.UnselectAll();
                    if (joinWorld.IsEnabled) throw new InvalidOperationException("未选中世界时可加入局域网。");
                    lanWorldList.ItemsSource = new[] { server };
                    lanWorldList.SelectedItem = server;
                    if (!joinWorld.IsEnabled) throw new InvalidOperationException("选中世界后无法加入局域网。");
                    Mode("查找服务器", "server-search", "多人联机", "发现");
                    if (serverFinderQuery.Text != "村庄")
                        throw new InvalidOperationException("切换联机分类后丢失服务器搜索词。");
                    serverFinderQuery.Clear();
                    Visit("设置", "start-settings", TopSettingsButton);
                    Side("下载设置", "download-settings");
                    Side("个性化", "appearance");
                    Side("更新设置", "updates");
                    VerifyFixtureSidebar("更新设置");
                    if (!FixtureVisuals(PageHost).Contains(settingsUpdateChannel) ||
                        !FixtureButton(PageHost, "检查更新").IsEnabled)
                        throw new InvalidOperationException("更新页缺少更新通道或检查更新操作。");
                    bool? oldAutoDownload = settingsAutoDownload.IsChecked;
                    settingsAutoDownload.IsChecked = oldAutoDownload != true;
                    Side("下载设置", "download-settings");
                    Side("更新设置", "updates");
                    if (settingsAutoDownload.IsChecked == oldAutoDownload || !FixtureVisuals(PageHost).Contains(updateStatusText))
                        throw new InvalidOperationException("切换设置分类后丢失更新偏好或更新状态。");
                    settingsAutoDownload.IsChecked = oldAutoDownload;
                    Visit("关于", "about", TopMoreButton);
                    ClickFixtureAction(sidebarButtons[QwenSearchPage]);
                    SelectQwenFixtureChat();
                    Visit("千问 AI 搜索", "qwen-search");
                    VerifyFixtureSidebar("千问 AI 搜索");
                    Side("运行日志", "logs");
                    Side("皮肤 DIY", "skin-editor");
                    Visit("首页", "home", TopLaunchButton);
                }
            }
            finally
            {
                settings.Profiles.Remove(profile);
                activeProfile = originalProfile;
                settings.Theme = originalTheme;
                settings.Zoom = originalZoom;
                SuppressAutomaticUpdateCheck = wasSuppressed;
                ApplyTheme();
                ApplyZoom();
                Navigate("首页");
            }
        }

        // Drive the ordinary official search, compatible-file pagination and
        // install button. Every HTTP client in this window uses the fixture.
        async Task VerifyCurseForgeQueueAsync()
        {
            const string variable = "BLOCKCRAFT_CURSEFORGE_API_KEY";
            string previous = Environment.GetEnvironmentVariable(variable);
            string root = Path.Combine(Path.GetTempPath(), "blockcraft-cf-ui-" + Guid.NewGuid().ToString("N"));
            var profile = new GameProfile { Id = "cf-ui-fixture", Name = "Fabric 1.21.1",
                BaseVersion = "1.21.1", LaunchVersion = "1.21.1", Loader = "fabric", Directory = root };
            var fixture = new CurseForgeInstallSanityChecks.Fixture();
            var lookup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken lookupToken = default;
            MainWindow window = null;
            async Task Until(Func<bool> ready, string failure)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (!ready())
                {
                    if (timer.Elapsed.TotalSeconds > 12) throw new InvalidOperationException("CurseForge 自检：" + failure);
                    await Task.Delay(20);
                }
            }
            try
            {
                window = new MainWindow(mirror => mirror
                    ? throw new InvalidOperationException("官方安装意外选择了镜像。")
                    : new CurseForgeMirrorClient(new LauncherConfig { DownloadRetries = 2 }, false,
                        fixture, CurseForgeInstallSanityChecks.Key), (token, refresh) =>
                    {
                        lookupToken = token;
                        return lookup.Task;
                    })
                { SuppressAutomaticUpdateCheck = true, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                window.settings.Profiles.Add(profile);
                window.activeProfile = profile;
                window.Show();
                await Until(() => window.IsLoaded, "测试窗口未加载");
                void Layout() => window.ArrangeLayoutProbe(960, 600);
                Layout();
                if (!CurseForgeMirrorClient.HasOfficialAccess)
                {
                    ClickFixtureAction(window.TopDownloadButton);
                    Layout();
                    ClickFixtureAction(window.sidebarButtons["资源中心"]);
                    window.resourceSource.SelectedIndex = (int)ModSource.CurseForge;
                    Layout();
                    await Until(() => lookupToken.CanBeCanceled, "未开始检测公开服务地址");
                    if (window.resourceSearchButton.IsEnabled ||
                        !FixtureButton(window.officialSourceActions, "下载 CurseForge 客户端").IsEnabled)
                        throw new InvalidOperationException("检测公开服务期间的客户端和模组操作状态错误。");
                    var oldStatus = window.modSourceStatus;
                    ClickFixtureAction(window.TopSettingsButton);
                    string statusAfterLeaving = oldStatus.Text;
                    if (!lookupToken.IsCancellationRequested)
                        throw new InvalidOperationException("离开官方页面后未取消服务检测。");
                    lookup.SetResult(true);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    if (window.currentPage != "设置" || oldStatus.Text != statusAfterLeaving)
                        throw new InvalidOperationException("较晚完成的服务检测更改了已离开的页面。");
                    Layout();
                }
                Environment.SetEnvironmentVariable(variable, CurseForgeInstallSanityChecks.Key);
                ClickFixtureAction(window.TopDownloadButton);
                Layout();
                ClickFixtureAction(window.sidebarButtons["资源中心"]);
                window.resourceSource.SelectedIndex = (int)ModSource.CurseForge;
                Layout();
                await Until(() => window.resourceCategory.Items.OfType<CurseForgeCategory>()
                    .Any(category => category.Id == 409), "官方分类未载入");
                window.modTarget.SelectedItem = profile;
                var filters = FixtureVisuals(window.PageHost).OfType<Expander>()
                    .First(expander => Equals(expander.Header, "筛选条件"));
                filters.IsExpanded = true;
                window.resourceMatchVersion.IsChecked = true;
                window.resourceCategory.SelectedItem = window.resourceCategory.Items.OfType<CurseForgeCategory>()
                    .Single(category => category.Id == 409);
                window.resourceQuery.Text = "sodium";
                Layout();
                ClickFixtureAction(window.resourceSearchButton);
                await Until(() => window.resourceResults.OfType<CurseForgeHit>().Any(hit => hit.Id == 101),
                    "官方搜索未返回所选分类的项目");
                window.resourceList.SelectedItem = window.resourceResults.OfType<CurseForgeHit>().Single();
                Layout();
                var picker = FixtureVisuals(window.modDetailsHost).OfType<ListBox>()
                    .Single(list => AutomationProperties.GetName(list) == "兼容的模组文件");
                await Until(() => picker.Items.OfType<ModDownloadFile>().Any(file => file.VersionId == "1001"),
                    "兼容文件第一页未载入");
                Layout();
                ClickFixtureAction(FixtureButton(window.modDetailsHost, "更多版本"));
                await Until(() => picker.Items.OfType<ModDownloadFile>().Any(file => file.VersionId == "1002"),
                    "未载入后续页的兼容文件");
                picker.SelectedItem = picker.Items.OfType<ModDownloadFile>().Single(file => file.VersionId == "1002");
                Layout();
                await Until(() => FixtureVisuals(window.modDetailsHost).OfType<TextBlock>()
                    .Any(text => text.Text.Contains("Dependency")), "文件选择未显示必需前置名称");
                filters.IsExpanded = false;
                window.PageScroll.ScrollToTop();
                window.ArrangeLayoutProbe(960, 600, "curseforge-selected-file");
                ClickFixtureAction(FixtureButton(window.modDetailsHost, "安装所选版本"));
                var job = window.downloads.Jobs.Single();
                await Until(() => !job.IsRunning, "等待安装队列超时");
                if (job.Status != "已完成" || job.Percent != 100)
                    throw new InvalidOperationException("CurseForge 安装队列失败：" + job.Error);
                string mods = Path.Combine(root, "mods");
                if (!Directory.GetFiles(mods).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "chosen.jar", "dependency.jar" }))
                    throw new InvalidOperationException("安装未精确使用选定文件和必需前置。");
                foreach (string path in Directory.GetFiles(mods))
                    using (var sha = SHA1.Create())
                    using (var input = File.OpenRead(path))
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                        if (actual != fixture.ExpectedHash(Path.GetFileName(path)))
                            throw new InvalidOperationException("实例中的 JAR 哈希与官方元数据不一致。");
                    }
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
                window?.Close();
                fixture.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static IEnumerable<FrameworkElement> FixtureVisuals(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element)
                {
                    if (element.Visibility != Visibility.Visible) continue;
                    yield return element;
                }
                foreach (var descendant in FixtureVisuals(child)) yield return descendant;
            }
        }

        static Button FixtureButton(FrameworkElement root, params string[] names)
        {
            return FixtureVisuals(root).OfType<Button>()
                .Where(button => names.Contains(AutomationProperties.GetName(button)) || names.Contains(button.Content as string))
                .OrderByDescending(button => button.IsEnabled).FirstOrDefault() ??
                throw new InvalidOperationException("找不到可用操作：" + string.Join("/", names));
        }

        static RadioButton FixtureMode(FrameworkElement root, string label)
        {
            var radio = FixtureVisuals(root).OfType<RadioButton>()
                .SingleOrDefault(item => AutomationProperties.GetName(item) == label);
            if (radio == null || !radio.IsEnabled || !radio.Focusable || radio.ActualWidth <= 0)
                throw new InvalidOperationException("页面缺少可聚焦的模式选项：" + label);
            return radio;
        }

        static void SelectFixtureMode(FrameworkElement root, string label)
        {
            var radio = FixtureMode(root, label);
            var peer = UIElementAutomationPeer.CreatePeerForElement(radio);
            var selection = peer?.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider;
            if (selection == null) throw new InvalidOperationException("模式未提供可访问的选择操作：" + label);
            selection.Select();
        }

        static void VerifyFixtureMode(FrameworkElement root, string label)
        {
            if (FixtureMode(root, label).IsChecked != true)
                throw new InvalidOperationException("当前模式未选中：" + label);
        }

        void VerifyFixtureSidebar(string parent)
        {
            var active = sidebarButtons.Where(item => AutomationProperties.GetItemStatus(item.Value) == "当前页面")
                .Select(item => item.Key).ToArray();
            if (active.Length != 1 || active[0] != parent)
                throw new InvalidOperationException("当前页面未高亮统一入口：" + parent);
        }

        static void ClickFixtureAction(Button button)
        {
            button.BringIntoView();
            button.UpdateLayout();
            if (!button.IsEnabled || !button.IsHitTestVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0)
                throw new InvalidOperationException("操作不可用：" + AutomationProperties.GetName(button));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        void CaptureFixtureDialog(Action open, string name, Action<Window> verify)
        {
            // ShowDialog runs a nested dispatcher. Inspect the actual modal
            // after layout, then close without saving accounts or skin files.
            var windows = Application.Current.Windows.Cast<Window>().ToList();
            Exception failure = null;
            bool inspected = false;
            var inspect = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.Cast<Window>()
                    .FirstOrDefault(item => item.IsVisible && !windows.Contains(item));
                if (dialog == null) return;
                try
                {
                    verify(dialog);
                    dialog.UpdateLayout();
                    var surface = (FrameworkElement)dialog.Content;
                    CaptureLayout(surface, (int)Math.Ceiling(surface.ActualWidth),
                        (int)Math.Ceiling(surface.ActualHeight), name);
                    inspected = true;
                }
                catch (Exception error) { failure = error; }
                finally { dialog.Close(); }
            }));
            try { open(); }
            finally { inspect.Abort(); }
            if (failure != null || !inspected)
                throw new InvalidOperationException("对话框自检未完成：" + name, failure);
        }
    }
}
