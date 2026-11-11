using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow : Window
    {
        // In a Windows CI relaunch probe we render the main window but must
        // not send update HTTP requests or touch user authentication.
        internal bool SuppressAutomaticUpdateCheck { get; set; }

        SkinStudioView skinStudio;

        readonly SettingsStore store = new SettingsStore();
        readonly LauncherConfig settings;
        readonly LauncherAutoUpdateService autoUpdater;
        readonly CancellationTokenSource updateCancellation = new CancellationTokenSource();
        LauncherUpdateOffer offeredUpdate;
        PreparedLauncherUpdate preparedUpdate;
        bool updateChecking;
        bool updatePreparing;
        bool updateCheckFailed;
        // Normal window close can install a fully verified prepared update,
        // but never when manual installation/direct-preview already handled it.
        bool suppressAutomaticInstallOnClose;
        TextBlock updateStatusText, updateNotesText, homeUpdateText;
        Border homeUpdateCard;
        ProgressBar updateProgressBar;
        Button updateInstallButton, updateCheckButton, updateDirectOpenButton;
        CheckBox settingsAutoCheck, settingsAutoDownload, settingsAutoInstallOnExit;
        ComboBox settingsUpdateChannel;
        readonly MinecraftBackend minecraft;
        MicrosoftAccountService microsoft;
        MicrosoftAccountService GetMicrosoftService() => microsoft ?? (microsoft = new MicrosoftAccountService());
        readonly ModLoaderInstaller modLoaders;
        readonly ObservableCollection<MinecraftVersionEntry> gameVersions = new ObservableCollection<MinecraftVersionEntry>();
        readonly DownloadTaskCenter downloads = new DownloadTaskCenter(3);
        DownloadJob versionInstallJob;
        string versionLoadError;
        bool usingCachedVersions;
        ComboBox versionCategory, settingsNetworkMode;
        TextBox settingsDownloadThreads, settingsDownloadRetries;
        ListBox downloadList;
        TextBox downloadSearchQuery;
        ComboBox downloadStatusFilter;
        TextBlock downloadCounter;
        ProgressBar downloadProgressBar;
        readonly ObservableCollection<ResourceHit> resourceResults = new ObservableCollection<ResourceHit>();
        readonly Dictionary<string, Button> sidebarButtons = new Dictionary<string, Button>();
        readonly List<string> logs = new List<string>();
        readonly MultiplayerService multiplayer = new MultiplayerService();
        readonly GameProcessTracker gameProcesses = new GameProcessTracker();
        readonly HashSet<GameProfile> launchingProfiles = new HashSet<GameProfile>();
        readonly DispatcherTimer gameProcessTimer = new DispatcherTimer(DispatcherPriority.Background);
        TextBlock gameProcessText;
        Button homeLaunchButton;
        TextBlock homeLaunchLabel, homeVersionCaption, homeProfileName, homeProfileInfo;
        ListBox profileList;
        string versionSettingsReturnPage = "首页";
        string detailTitle;
        double detailReturnOffset;
        IInputElement detailTrigger;
        Dictionary<string, UIElement> settingsPages;
        ComboBox localResourceKind;
        ListBox localResourceList, serverList, lanWorldList;
        CancellationTokenSource localModIconLoad;
        TextBox serverName, serverAddress;
        TextBox serverFinderQuery, serverFinderVersion;
        ComboBox serverFinderProvider, serverFinderSort;
        CheckBox serverFinderOnlineOnly;
        ListBox serverFinderResults;
        TextBlock serverFinderStatus, serverFinderDetails;
        Button serverFinderSearchButton, serverFinderPrevButton, serverFinderNextButton;
        Button serverFinderFavoriteButton, serverFinderJoinButton, serverFinderCopyButton;
        Button serverFinderSourceButton;
        CancellationTokenSource serverFinderCancellation;
        int serverFinderPage = 1, serverFinderPages = 1;
        bool serverFinderSearching;
        TextBox packSearchQuery;
        ComboBox packVersionFilter, packSortMode;
        ListBox packSearchResults;
        TextBlock packSearchStatus;
        Button packSearchButton, packPrevButton, packNextButton;
        int packPage;
        CancellationTokenSource packSearchCancellation;
        ListBox modUpgradeResults;
        TextBox cfSearchQuery, cfVersionFilter;
        ListBox cfSearchResults;
        ComboBox cfCategoryFilter, cfSortMode;
        readonly List<CurseForgeHit> cfLoadedHits = new List<CurseForgeHit>();
        TextBlock cfSearchStatus;
        CancellationTokenSource cfSearchCancellation;
        CheckBox settingsMcim;
        GameProfile activeProfile;
        string currentPage = "首页";
        ComboBox versionLoader, settingsTheme;
        Button versionInstallButton, versionCancelButton, versionReloadButton;
        TextBox versionQuery, resourceQuery, settingsRoot, settingsProxy, settingsJava, settingsPlayer, settingsMemory;
        TextBlock versionCounter;
        ListBox versionList, resourceList;
        TextBlock zoomValue;
        Slider settingsZoom;
        string resourceType = "mod";
        int resourcePage;
        CheckBox resourceMatchVersion;
        ComboBox resourceSort, resourceSource, resourceVersion, resourceCategory, resourceLoader;
        Button resourceSearchButton, resourcePrevButton, resourceNextButton;
        TextBlock resourceCounter;
        CancellationTokenSource resourceSearchCancellation;
        CancellationTokenSource currentOperation;
        bool loadingVersions;

        public MainWindow()
        {
            InitializeComponent();
            settings = store.Load();
            autoUpdater = new LauncherAutoUpdateService(settings);
            minecraft = new MinecraftBackend(settings);
            modLoaders = new ModLoaderInstaller(settings);
            activeProfile = settings.Profiles.FirstOrDefault();
            TopLaunchButton.Click += (sender, args) => Navigate("首页");
            TopDownloadButton.Click += (sender, args) => Navigate("游戏版本");
            TopMultiplayerButton.Click += (sender, args) => Navigate("多人联机");
            TopSettingsButton.Click += (sender, args) => Navigate("设置");
            TopMoreButton.Click += (sender, args) => Navigate("关于");
            TaskShortcut.Click += (sender, args) => Navigate("下载管理");
            BuildVersionText.Text = LauncherAutoUpdateService.CurrentTag.Split('-')[0] +
                "  ·  " + LauncherAutoUpdateService.BuildChannelLabel;
            ApplyTheme();
            ApplyZoom();
            Navigate("首页");
            Log("启动器设置已加载。");
            gameProcesses.ProcessExited += (profile, state) =>
                Log("Minecraft 已退出：" + profile.Name + "（PID " + state.ProcessId
                    + "，退出码 " + (state.ExitCode?.ToString() ?? "未知") + "）");
            gameProcessTimer.Interval = TimeSpan.FromSeconds(2);
            gameProcessTimer.Tick += (sender, args) =>
            {
                gameProcesses.Refresh();
                RefreshGameProcessStatus();
            };
            // A smoke-test MainWindow is never shown; avoid starting a timer
            // before WPF has actually opened the user-facing window.
            Loaded += async (sender, args) =>
            {
                gameProcessTimer.Start();
                if (!SuppressAutomaticUpdateCheck)
                    await CheckLauncherUpdatesAsync(false);
            };
            if (Application.Current != null)
                Application.Current.SessionEnding += (sender, args) =>
                    suppressAutomaticInstallOnClose = true;
            Closing += (sender, args) =>
            {
                // Do not reopen the launcher in the middle of a Minecraft
                // session, a game download, a mod installation or logoff.
                bool activeDownload = downloads.Jobs.Any(job => job.IsRunning);
                bool activeGame = settings.Profiles.Any(profile =>
                    gameProcesses.GetState(profile).Status == GameProcessStatus.Running);
                bool workInProgress = currentOperation != null ||
                    activeDownload || activeGame;
                if (!LauncherAutoUpdateService.ShouldAutoInstallOnExit(settings,
                    preparedUpdate, offeredUpdate, updateChecking || updatePreparing ||
                    workInProgress || updateCancellation.IsCancellationRequested,
                    suppressAutomaticInstallOnClose || SuppressAutomaticUpdateCheck))
                    return;
                // A verified update is installed only after the user closes
                // the launcher. The separate helper waits for this process
                // to exit, replaces its original EXE, tests, then relaunches.
                try
                {
                    autoUpdater.StartExternalInstaller(preparedUpdate);
                    suppressAutomaticInstallOnClose = true;
                    Log("自动安装已准备的更新：" + preparedUpdate.Offer.Tag +
                        "；正在关闭旧版并原位替换 EXE。");
                }
                catch (Exception ex)
                {
                    // Do not trap the user in a window that refuses to close.
                    // The original EXE stays intact and the prepared package
                    // remains available to retry on the next launch.
                    Log("退出时自动安装失败，保留旧版：" + ex.GetType().Name);
                    ShowError("自动替换旧版失败（原启动器仍保留）", ex);
                }
            };
            Closed += (sender, args) =>
            {
                gameProcessTimer.Stop();
                updateCancellation.Cancel();
                localModIconLoad?.Cancel();
                gameProcesses.Dispose(); // Does not terminate the Minecraft game.
            };
        }

        static TextBlock Txt(string text, double size = 14, bool bold = false, bool muted = false)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size + 5,
                Margin = new Thickness(0, 0, 0, 7)
            };
            if (muted) label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return label;
        }

        static UIElement PageHeading(string title, string summary)
        {
            var heading = Txt(title, 21, true);
            var description = Txt(summary, 12, false, true);
            description.Margin = new Thickness(0, 0, 0, 12);
            return Col(heading, description);
        }

        static Border SearchCard(UIElement content)
        {
            var panel = Card(content);
            panel.SetResourceReference(FrameworkElement.StyleProperty, "SearchPanel");
            return panel;
        }

        static Border ResultsCard(UIElement content)
        {
            var panel = Card(content);
            panel.SetResourceReference(FrameworkElement.StyleProperty, "ResultsPanel");
            return panel;
        }

        static void ApplySearchInputStyle(Control control, string hint)
        {
            if (control is TextBox)
                control.SetResourceReference(FrameworkElement.StyleProperty, "SearchTextBox");
            else if (control is ComboBox)
                control.SetResourceReference(FrameworkElement.StyleProperty, "SearchComboBox");
            control.ToolTip = hint;
        }

        static Button FilterChipButton(string caption, Action action)
        {
            var b = Btn(caption, action);
            b.SetResourceReference(FrameworkElement.StyleProperty, "FilterChip");
            return b;
        }

        static string ActionGlyph(string caption)
        {
            if (caption.StartsWith("启动")) return "\uE768";
            if (caption.StartsWith("下载") || caption.StartsWith("安装")) return "\uE896";
            if (caption.StartsWith("搜索") || caption.StartsWith("检查") || caption.StartsWith("扫描")) return "\uE721";
            if (caption.StartsWith("刷新") || caption.StartsWith("重试") || caption.StartsWith("更新")) return "\uE72C";
            if (caption.StartsWith("取消") || caption.StartsWith("删除") || caption.StartsWith("移除")) return "\uE711";
            if (caption.StartsWith("打开") || caption.StartsWith("导入") || caption.StartsWith("选择")) return "\uE8B7";
            if (caption.StartsWith("保存") || caption.StartsWith("备份")) return "\uE74E";
            if (caption.StartsWith("登录") || caption.StartsWith("连接")) return "\uE77B";
            if (caption.StartsWith("设置") || caption.StartsWith("恢复")) return "\uE713";
            if (caption.StartsWith("查看") || caption.StartsWith("管理")) return "\uE8A7";
            if (caption.StartsWith("返回") || caption.StartsWith("前往")) return "\uE72B";
            return "";
        }
        static Button Btn(string caption, Action action, bool accent = false)
        {
            var b = new Button { MaxWidth = 338 };
            AutomationProperties.SetName(b, caption);
            var content = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            string glyph = ActionGlyph(caption);
            if (glyph.Length > 0)
            {
                var symbol = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                symbol.SetBinding(TextBlock.ForegroundProperty,
                    new Binding("Foreground") { Source = b, Mode = BindingMode.OneWay });
                DockPanel.SetDock(symbol, Dock.Left);
                content.Children.Add(symbol);
            }
            var label = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap };
            label.SetBinding(TextBlock.ForegroundProperty,
                new Binding("Foreground") { Source = b, Mode = BindingMode.OneWay });
            content.Children.Add(label);
            b.Content = content;
            if (accent) b.SetResourceReference(StyleProperty, "AccentButton");
            b.Click += (sender, args) => action();
            return b;
        }
        static Border Card(UIElement content)
        {
            var b = new Border { Child = content };
            b.SetResourceReference(StyleProperty, "Panel");
            return b;
        }
        static StackPanel Col(params UIElement[] children)
        {
            var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p;
        }
        static WrapPanel Row(params UIElement[] children)
        {
            var p = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in children) p.Children.Add(c); return p;
        }
        void AddNavSection(string heading, params string[] names)
        {
            var title = Txt(heading, 11, true, true);
            title.Margin = new Thickness(10, 6, 0, 5);
            SideMenu.Children.Add(title);
            foreach (var name in names)
            {
                var btn = new Button();
                AutomationProperties.SetName(btn, NavLabel(name));
                var content = new StackPanel { Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center };
                var icon = new TextBlock { Text = NavGlyph(name),
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15, Width = 21, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0) };
                icon.SetBinding(TextBlock.ForegroundProperty,
                    new Binding("Foreground") { Source = btn, Mode = BindingMode.OneWay });
                var label = new TextBlock { Text = NavLabel(name), FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                label.SetBinding(TextBlock.ForegroundProperty,
                    new Binding("Foreground") { Source = btn, Mode = BindingMode.OneWay });
                content.Children.Add(icon);
                content.Children.Add(label);
                btn.Content = content;
                btn.Click += (sender, args) => Navigate(name);
                btn.SetResourceReference(FrameworkElement.StyleProperty, "SidebarItem");
                SideMenu.Children.Add(btn);
                sidebarButtons[name] = btn;
            }
            SideMenu.Children.Add(new Border { Height = 7 });
        }
        static string NavLabel(string page)
        {
            switch (page)
            {
                case "版本设置": return "概览";
                case "模组更新": return "模组与资源";
                case "存档管理": return "存档";
                case "游戏版本": return "原版游戏";
                case "下载管理": return "下载任务";
                case "资源中心": return "模组";
                case "光影引擎": return "光影包";
                case "皮肤 DIY": return "皮肤 DIY";
                case "皮肤下载": return "皮肤";
                case "设置": return "启动设置";
                case "多人联机": return "服务器收藏";
                case "查找服务器": return "查找服务器";
                default: return page;
            }
        }

        static string NavGlyph(string page)
        {
            switch (page)
            {
                case "版本选择":
                case "游戏版本": return "\uE7B8";
                case "版本设置": return "\uE8A5";
                case "存档管理": return "\uE74E";
                case "下载管理": return "\uE896";
                case "资源中心":
                case "模组更新": return "\uE8B7";
                case "光影引擎":
                case "个性化": return "\uE790";
                case "资源包": return "\uE8A7";
                case "整合包": return "\uE8F1";
                case "CurseForge": return "\uE753";
                case "皮肤 DIY": return "\uE70F";
                case "皮肤下载": return "\uE8B7";
                case "多人联机":
                case "查找服务器":
                case "局域网": return "\uE902";
                case "启动器更新":
                case "更新设置": return "\uE777";
                case "下载设置": return "\uE774";
                case "设置": return "\uE713";
                case "运行日志": return "\uE9F9";
                default: return "\uE946";
            }
        }
        enum NavigationSection { Launch, Versions, VersionSettings, Download, Multiplayer, Settings, More }

        static NavigationSection SectionFor(string page)
        {
            switch (page)
            {
                case "首页": return NavigationSection.Launch;
                case "版本选择": return NavigationSection.Versions;
                case "版本设置":
                case "模组更新":
                case "存档管理":
                    return NavigationSection.VersionSettings;
                case "游戏版本":
                case "下载管理":
                case "资源中心":
                case "皮肤下载":
                case "光影引擎":
                case "资源包":
                case "整合包":
                case "CurseForge":
                    return NavigationSection.Download;
                case "多人联机":
                case "查找服务器":
                case "局域网":
                    return NavigationSection.Multiplayer;
                case "设置":
                case "下载设置":
                case "个性化":
                case "更新设置":
                    return NavigationSection.Settings;
                default: return NavigationSection.More;
            }
        }

        void BuildSidebar(NavigationSection section, string page)
        {
            SideMenu.Children.Clear();
            sidebarButtons.Clear();
            LaunchPane.Visibility = section == NavigationSection.Launch ? Visibility.Visible : Visibility.Collapsed;
            SideMenu.Visibility = section == NavigationSection.Launch ? Visibility.Collapsed : Visibility.Visible;
            SideColumn.Width = new GridLength(section == NavigationSection.Launch ? 264 : 164);
            switch (section)
            {
                case NavigationSection.Versions:
                    AddNavSection("本地游戏", "版本选择");
                    break;
                case NavigationSection.VersionSettings:
                    AddNavSection("版本设置", "版本设置", "模组更新", "存档管理");
                    break;
                case NavigationSection.Download:
                    AddNavSection("游戏", "游戏版本");
                    AddNavSection("社区资源", "资源中心", "皮肤下载", "整合包", "资源包", "光影引擎", "CurseForge");
                    AddNavSection("任务", "下载管理");
                    break;
                case NavigationSection.Multiplayer:
                    AddNavSection("Minecraft Java", "多人联机", "查找服务器", "局域网");
                    break;
                case NavigationSection.Settings:
                    AddNavSection("启动器设置", "设置", "下载设置", "个性化", "更新设置");
                    break;
                case NavigationSection.More:
                    AddNavSection("工具", "皮肤 DIY", "启动器更新", "运行日志");
                    AddNavSection("信息", "关于");
                    break;
            }
            if (sidebarButtons.TryGetValue(page, out var active))
                active.SetResourceReference(FrameworkElement.StyleProperty, "SidebarItemActive");
        }

        static void SetHeaderTab(Button button, bool active) =>
            button.SetResourceReference(FrameworkElement.StyleProperty, active ? "HeaderTabActive" : "HeaderTab");

        void UpdateHeader(NavigationSection section)
        {
            bool detailOpen = DetailHost.Content != null;
            bool inner = detailOpen || section == NavigationSection.Versions ||
                section == NavigationSection.VersionSettings;
            TopNavigation.Visibility = inner ? Visibility.Collapsed : Visibility.Visible;
            ContextNavigation.Visibility = inner ? Visibility.Visible : Visibility.Collapsed;
            ContextTitle.Text = detailOpen ? detailTitle :
                section == NavigationSection.VersionSettings
                    ? "版本设置" + (activeProfile == null ? "" : "  /  " + activeProfile.Name)
                    : "版本选择";
            SetHeaderTab(TopLaunchButton, section == NavigationSection.Launch);
            SetHeaderTab(TopDownloadButton, section == NavigationSection.Download);
            SetHeaderTab(TopMultiplayerButton, section == NavigationSection.Multiplayer);
            SetHeaderTab(TopSettingsButton, section == NavigationSection.Settings);
            SetHeaderTab(TopMoreButton, section == NavigationSection.More);
        }

        void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (DetailHost.Content != null)
            {
                ClearDetails();
                UpdateHeader(SectionFor(currentPage));
                PageScroll.UpdateLayout();
                PageScroll.ScrollToVerticalOffset(detailReturnOffset);
                if (detailTrigger != null) System.Windows.Input.Keyboard.Focus(detailTrigger);
                detailTrigger = null;
                return;
            }
            Navigate(SectionFor(currentPage) == NavigationSection.VersionSettings
                ? versionSettingsReturnPage : "首页");
        }

        void ShowDetails(string title, UIElement details)
        {
            detailReturnOffset = PageScroll.VerticalOffset;
            detailTrigger = System.Windows.Input.Keyboard.FocusedElement;
            detailTitle = title;
            DetailHost.Content = details;
            DetailHost.Visibility = Visibility.Visible;
            PageHost.Visibility = Visibility.Collapsed;
            UpdateHeader(SectionFor(currentPage));
            PageScroll.ScrollToTop();
            BackButton.Focus();
        }

        void ClearDetails()
        {
            DetailHost.Content = null;
            DetailHost.Visibility = Visibility.Collapsed;
            PageHost.Visibility = Visibility.Visible;
        }

        void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
        void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);
            if (MaximizeGlyph != null)
                MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        SkinStudioView GetSkinStudio()
        {
            if (skinStudio == null)
                skinStudio = new SkinStudioView(UploadSkinToMicrosoftAsync,
                    () => settings.AccountMode == "microsoft" ? settings.AccountName : null,
                    () => Navigate("皮肤 DIY"), QueueSkinDownload,
                    () => Navigate("皮肤下载"));
            return skinStudio;
        }

        void QueueSkinDownload(OnlineSkinEntry item)
        {
            if (item == null || !item.CanDownload)
                return;
            downloads.Enqueue("皮肤", item.Name, async (token, stage, bytes) =>
            {
                stage.Report("正在下载皮肤：" + item.Name + " · " + item.Source);
                string saved = await OnlineSkinSearchService.DownloadToLibraryAsync(
                    item, token, bytes);
                stage.Report("已保存到皮肤库：" + saved + "（附带作者及许可证信息）");
            });
            Status("皮肤下载已加入任务队列：" + item.Name);
        }

        void Navigate(string page)
        {
            localModIconLoad?.Cancel();
            resourceSearchCancellation?.Cancel();
            resourceSearchCancellation = null;
            cfSearchCancellation?.Cancel();
            cfSearchCancellation = null;
            packSearchCancellation?.Cancel();
            packSearchCancellation = null;
            serverFinderCancellation?.Cancel();
            serverFinderCancellation = null;
            serverFinderSearching = false;
            ClearDetails();
            detailTrigger = null;
            currentPage = page;
            var section = SectionFor(page);
            if (section != NavigationSection.Settings) settingsPages = null;
            gameProcessText = null;
            homeLaunchButton = null;
            profileList = null;
            updateStatusText = null;
            updateNotesText = null;
            updateInstallButton = null;
            updateCheckButton = null;
            updateDirectOpenButton = null;
            updateProgressBar = null;
            homeUpdateText = null;
            homeUpdateCard = null;
            BuildSidebar(section, page);
            UpdateHeader(section);
            LaunchPane.Content = section == NavigationSection.Launch ? BuildLaunchPane() : null;
            switch (page)
            {
                case "首页": PageHost.Content = BuildHome(); break;
                case "版本选择": PageHost.Content = BuildProfileSelection(); break;
                case "版本设置": PageHost.Content = BuildInstanceOverview(); break;
                case "存档管理": PageHost.Content = BuildInstanceSaves(); break;
                case "游戏版本": PageHost.Content = BuildVersions(); break;
                case "下载管理": PageHost.Content = BuildDownloadCenter(); break;
                case "资源中心": PageHost.Content = BuildResources("mod"); break;
                case "光影引擎": PageHost.Content = BuildResources("shader"); break;
                case "资源包": PageHost.Content = BuildResources("resourcepack"); break;
                case "整合包": PageHost.Content = BuildModpacks(); break;
                case "CurseForge": PageHost.Content = BuildCurseForge(); break;
                case "皮肤下载":
                    GetSkinStudio().ShowDownloadPage(true);
                    PageHost.Content = skinStudio;
                    break;
                case "皮肤 DIY":
                    GetSkinStudio().ShowDownloadPage(false);
                    PageHost.Content = skinStudio;
                    break;
                case "模组更新": PageHost.Content = BuildModUpdates(); break;
                case "多人联机": PageHost.Content = BuildMultiplayer(); break;
                case "查找服务器": PageHost.Content = BuildServerFinder(); break;
                case "局域网": PageHost.Content = BuildLanWorlds(); break;
                case "启动器更新": PageHost.Content = BuildLauncherUpdates(); break;
                case "设置":
                case "下载设置":
                case "个性化":
                case "更新设置":
                    PageHost.Content = BuildSettings(page); break;
                case "运行日志": PageHost.Content = BuildLogs(); break;
                case "关于": PageHost.Content = BuildAbout(); break;
                default: throw new ArgumentOutOfRangeException(nameof(page));
            }
            PageScroll.ScrollToTop();
            RefreshLauncherUpdatePanel();
            if (page == "游戏版本" && IsLoaded && !SuppressAutomaticUpdateCheck && gameVersions.Count == 0)
                _ = LoadVersionsAsync();
        }
        async Task UploadSkinToMicrosoftAsync(byte[] png, bool slim)
        {
            if (settings.AccountMode != "microsoft")
                throw new InvalidOperationException(
                    "当前为离线玩家模式；请先登录拥有 Minecraft Java 的微软正版账号。");
            var session = await GetMicrosoftService().GetSessionAsync();
            if (session == null || string.IsNullOrWhiteSpace(session.AccessToken))
                throw new InvalidOperationException(
                    "没有有效的 Minecraft Java 正版授权。请退出账号后重新登录。");
            // Deliberately send tokens only to the first-party HTTPS API;
            // never expose the token in UI, release logs or user skin files.
            await MinecraftSkinUploadService.UploadAsync(
                session.AccessToken, png, slim, updateCancellation.Token);
            Status("已上传当前皮肤至 Minecraft 官方账户：" + session.Username +
                "（" + (slim ? "Alex" : "Steve") + "）");
        }

        void Status(string message) { StatusText.Text = message; Log(message); }
        internal void ShowPostUpdateSuccess()
        {
            Title = "BlockCraft Launcher · " + LauncherAutoUpdateService.CurrentTag + " · 更新完成";
            Status("更新完成！已自动打开新版 BlockCraft Launcher " +
                LauncherAutoUpdateService.CurrentTag + "。");
        }

        internal void ShowDirectOpenNotice()
        {
            Title = "BlockCraft Launcher · " + LauncherAutoUpdateService.CurrentTag + " · 新版试用";
            Status("已直接打开经过校验的新版启动器 " +
                LauncherAutoUpdateService.CurrentTag +
                "。此方式不覆盖桌面上的旧版 EXE；需要永久更新请使用“下载并安装”。");
        }
        void Log(string text) { logs.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text); }
        void ApplyTheme()
        {
            bool light = settings.Theme == "浅色";
            void Set(string key, string value) => Application.Current.Resources[key] =
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            Set("WindowBrush", light ? "#F5F8FC" : "#182435");
            Set("PageBrush", light ? "#EAF2FC" : "#142031");
            Set("SidebarBrush", light ? "#F8FBFF" : "#1A2A40");
            Set("PanelBrush", light ? "#FFFFFF" : "#203147");
            Set("PanelAltBrush", light ? "#F3F7FC" : "#283C55");
            Set("TextBrush", light ? "#203249" : "#EFF5FF");
            Set("MutedBrush", light ? "#526983" : "#B2C5DD");
            Set("BorderBrushTheme", light ? "#D3E0F0" : "#38516C");
            Set("HoverBrush", light ? "#EAF3FF" : "#2E4663");
            Set("SelectedBrush", light ? "#DFEDFD" : "#315173");
            Set("BadgeBrush", light ? "#E5F0FD" : "#2A4667");
            Set("SecondaryAccentBrush", light ? "#4E9BDD" : "#6FB5F8");
            Set("SoftDividerBrush", light ? "#E5EDF7" : "#354E69");
            Set("SearchSurfaceBrush", light ? "#FFFFFF" : "#203147");
            Set("SuccessBrush", light ? "#1D765A" : "#70DBB1");
            Set("AccentInkBrush", light ? "#125CA4" : "#94C9FF");
            Set("AccentBrush", IsHexColor(settings.Accent) ? settings.Accent : "#1875CE");
            var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
            double luminance = 0.2126 * accent.ScR + 0.7152 * accent.ScG + 0.0722 * accent.ScB;
            Set("AccentTextBrush", luminance > 0.179 ? "#000000" : "#FFFFFF");
        }
        static bool IsHexColor(string s) => s != null && s.Length == 7 && s[0] == '#' && s.Skip(1).All(c => Uri.IsHexDigit(c));
        void ApplyZoom()
        {
            var value = Math.Max(80, Math.Min(140, settings.Zoom)) / 100.0;
            UiScale.ScaleX = value; UiScale.ScaleY = value;
            UpdateShellSize();
        }
        void UpdateShellSize()
        {
            if (WindowViewport == null || Shell == null) return;
            var scale = Math.Max(0.8, UiScale.ScaleX);
            if (WindowViewport.ActualWidth > 0)
                Shell.Width = Math.Max(Shell.MinWidth, (WindowViewport.ActualWidth - 2) / scale);
            if (WindowViewport.ActualHeight > 0)
                Shell.Height = Math.Max(Shell.MinHeight, (WindowViewport.ActualHeight - 2) / scale);
        }
        void WindowViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateShellSize();

        static void Place(Grid grid, UIElement element, int row, int col)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, col);
            grid.Children.Add(element);
        }

        UIElement BuildLaunchPane()
        {
            var account = Col(Txt("玩家账号", 16, true));
            string identity = settings.AccountMode == "offline" ? settings.OfflineName :
                settings.AccountMode == "microsoft" ? settings.AccountName : "未登录";
            account.Children.Add(Txt(string.IsNullOrWhiteSpace(identity) ? "未登录" : identity, 18, true));
            account.Children.Add(Txt(settings.AccountMode == "microsoft" ? "微软正版账号" :
                settings.AccountMode == "offline" ? "离线玩家" : "选择一种登录方式", 12, false, true));
            account.Children.Add(Row(
                Btn("正版登录", async () => await LoginMicrosoftAsync(), settings.AccountMode == "microsoft"),
                Btn("离线登录", EditOfflineAccount, settings.AccountMode == "offline")));
            if (settings.AccountMode != "signed_out")
                account.Children.Add(Btn("退出账号", async () => await SignOutAsync()));

            homeLaunchLabel = Txt("启动游戏", 19, true);
            homeVersionCaption = Txt("", 11);
            foreach (var label in new[] { homeLaunchLabel, homeVersionCaption })
            {
                label.Margin = new Thickness(0);
                label.TextWrapping = TextWrapping.NoWrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.TextAlignment = TextAlignment.Center;
                label.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
            }
            homeLaunchButton = Btn("启动游戏", async () =>
            {
                if (activeProfile == null) Navigate("游戏版本");
                else await LaunchAsync();
            }, true);
            homeLaunchButton.Content = Col(homeLaunchLabel, homeVersionCaption);
            homeLaunchButton.MinHeight = 63;
            homeLaunchButton.Margin = new Thickness(0, 16, 0, 7);
            gameProcessText = Txt("", 11, false, true);
            gameProcessText.Margin = new Thickness(1, 0, 0, 9);
            var select = Btn("版本选择", () => Navigate("版本选择"));
            var manage = Btn("版本设置", () => OpenVersionSettings("首页"));
            select.Margin = new Thickness(0, 0, 6, 0);
            manage.Margin = new Thickness(0);
            var versionButtons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            versionButtons.Children.Add(select);
            versionButtons.Children.Add(manage);

            var pane = Col(account, homeLaunchButton, gameProcessText, versionButtons);
            pane.Margin = new Thickness(15, 18, 15, 14);
            return pane;
        }

        UIElement BuildHome()
        {
            homeProfileName = Txt("", 20, true);
            homeProfileInfo = Txt("", 12, false, true);
            var overview = Card(Col(Txt("当前版本", 12, true, true),
                homeProfileName, homeProfileInfo,
                Row(Btn("打开游戏目录", OpenGameFolder),
                    Btn("查看运行日志", () => Navigate("运行日志")))));

            homeUpdateText = Txt("", 12, false, true);
            homeUpdateCard = Card(Col(Txt("启动器有新版本", 16, true), homeUpdateText,
                Row(Btn("查看更新", () => Navigate("启动器更新"), true),
                    Btn("更新设置", () => Navigate("更新设置")))));

            var shortcuts = Card(Col(Txt("常用功能", 16, true),
                Row(Btn("安装游戏", () => Navigate("游戏版本")),
                    Btn("搜索模组", () => Navigate("资源中心")),
                    Btn("皮肤下载", () => Navigate("皮肤下载")),
                    Btn("皮肤 DIY", () => Navigate("皮肤 DIY")),
                    Btn("多人游戏", () => Navigate("多人联机")))));
            RefreshGameProcessStatus();
            return Col(homeUpdateCard, overview, shortcuts,
                Card(Col(Txt("版本动态", 16, true),
                    Txt("新版启动器调整了页面布局。游戏、下载、联机和设置均可从顶部进入。", 12, false, true),
                    Row(Btn("查看更新说明", () => Navigate("关于"))))));
        }

        void OpenVersionSettings(string returnPage)
        {
            versionSettingsReturnPage = returnPage;
            Navigate("版本设置");
        }

        UIElement BuildProfileSelection()
        {
            profileList = new ListBox
            {
                ItemsSource = settings.Profiles, SelectedItem = activeProfile,
                ItemTemplate = (DataTemplate)FindResource("ProfileItemTemplate"),
                MinHeight = 145, MaxHeight = 366
            };
            void SelectProfile(bool openSettings)
            {
                if (!(profileList.SelectedItem is GameProfile profile)) return;
                activeProfile = profile;
                if (openSettings) OpenVersionSettings("版本选择");
                else Navigate("首页");
            }
            profileList.MouseDoubleClick += (sender, args) =>
            {
                if (args.OriginalSource is DependencyObject source &&
                    ItemsControl.ContainerFromElement(profileList, source) is ListBoxItem)
                    SelectProfile(false);
            };
            var use = Btn("选择此版本", () => SelectProfile(false), true);
            var manage = Btn("版本设置", () => SelectProfile(true));
            void RefreshSelection() => use.IsEnabled = manage.IsEnabled = profileList.SelectedItem != null;
            profileList.SelectionChanged += (sender, args) => RefreshSelection();
            RefreshSelection();
            return Col(
                PageHeading("版本选择", "选择本次启动使用的 Minecraft 版本。"),
                Card(Col(Txt("已安装版本", 16, true),
                    Txt(settings.Profiles.Count == 0 ? "暂无游戏版本。可安装新版本或导入本地游戏。"
                        : "双击列表中的版本可直接选用。", 12, false, true),
                    profileList, Row(use, manage))),
                Card(Col(Txt("添加版本", 16, true),
                    Row(Btn("下载游戏", () => Navigate("游戏版本")),
                        Btn("导入本地版本", ImportLocalGame),
                        Btn("打开游戏目录", OpenGameFolder)))));
        }

        void UseInstalledProfile(GameProfile profile)
        {
            // Keep open version settings on the instance they were opened for.
            if (activeProfile == null || SectionFor(currentPage) != NavigationSection.VersionSettings)
                activeProfile = profile;
            if (profileList != null)
            {
                var selection = profileList.SelectedItem;
                CollectionViewSource.GetDefaultView(settings.Profiles).Refresh();
                profileList.SelectedItem = selection ?? activeProfile;
            }
            RefreshGameProcessStatus();
        }

        static Grid SearchFields(params (string Label, Control Input)[] fields)
        {
            var form = new Grid { Margin = new Thickness(0, 2, 0, 1) };
            for (int column = 0; column < 4; column++)
                form.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = column % 2 == 0 ? new GridLength(56) : new GridLength(1, GridUnitType.Star)
                });
            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (i % 2 == 0) form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new Label
                {
                    Content = field.Label, Target = field.Input, FontSize = 12,
                    Padding = new Thickness(i % 2 == 0 ? 0 : 6, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 7), VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                AutomationProperties.SetLabeledBy(field.Input, label);
                field.Input.Margin = new Thickness(0, 0, 0, 7);
                field.Input.HorizontalAlignment = HorizontalAlignment.Stretch;
                Place(form, label, i / 2, 2 * (i % 2));
                Place(form, field.Input, i / 2, 2 * (i % 2) + 1);
            }
            return form;
        }

        UIElement BuildVersions()
        {
            versionQuery = new TextBox();
            ApplySearchInputStyle(versionQuery, "输入完整或部分版本号，例如 1.21.1");
            versionCategory = new ComboBox
            {
                ItemsSource = new[] { "全部版本", "正式版", "快照版", "远古 Beta", "远古 Alpha" },
                SelectedIndex = 0
            };
            ApplySearchInputStyle(versionCategory, "筛选正式版、快照版或历史版本");
            versionReloadButton = Btn("刷新列表", async () => await LoadVersionsAsync());
            versionReloadButton.IsEnabled = !loadingVersions;
            var filters = SearchCard(Col(Txt("原版游戏", 16, true),
                SearchFields(("版本号", versionQuery), ("类型", versionCategory)),
                Row(FilterChipButton("全部", () => versionCategory.SelectedIndex = 0),
                    FilterChipButton("正式版", () => versionCategory.SelectedIndex = 1),
                    FilterChipButton("快照版", () => versionCategory.SelectedIndex = 2),
                    versionReloadButton)));

            versionCounter = Txt("", 12, false, true);
            versionList = new ListBox
            {
                ItemsSource = gameVersions, ItemTemplate = (DataTemplate)FindResource("VersionItemTemplate"),
                MinHeight = 155, MaxHeight = 300, Margin = new Thickness(0, 0, 0, 8)
            };
            versionLoader = new ComboBox
            {
                ItemsSource = new[] { "原版", "Fabric", "Forge", "NeoForge", "Quilt" },
                SelectedIndex = 0, Width = 160
            };
            AutomationProperties.SetName(versionLoader, "加载器");
            versionInstallButton = Btn("安装", async () => await InstallSelectedVersionAsync(), true);
            versionCancelButton = Btn("取消安装", () => versionInstallJob?.Cancel());
            var loaderCaption = Txt("加载器", 12, false, true);
            loaderCaption.Margin = new Thickness(0, 5, 9, 8);
            versionQuery.TextChanged += (sender, args) => FilterVersions();
            versionCategory.SelectionChanged += (sender, args) => FilterVersions();
            versionList.SelectionChanged += (sender, args) => RefreshVersionActions();
            FilterVersions();
            RefreshVersionActions();
            return Col(filters,
                ResultsCard(Col(Txt("可安装版本", 16, true), versionCounter,
                    Txt("版本来源  ·  Mojang / BMCLAPI", 11, false, true), versionList,
                    Row(loaderCaption, versionLoader, versionInstallButton, versionCancelButton))),
                Card(Col(Txt("本地游戏", 16, true),
                    Row(Btn("导入本地版本", ImportLocalGame),
                        Btn("打开游戏目录", OpenGameFolder)))));
        }

        void FilterVersions()
        {
            if (versionList == null || versionQuery == null) return;
            string category = Convert.ToString(versionCategory?.SelectedItem) ?? "全部版本";
            string search = versionQuery.Text.Trim();
            versionList.Items.Filter = raw =>
            {
                var item = raw as MinecraftVersionEntry;
                return item != null &&
                    (category == "全部版本" || item.Category == category) &&
                    (search.Length == 0 || item.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            };
            versionCounter.Text = loadingVersions ? "正在获取 Minecraft 版本列表…" :
                gameVersions.Count == 0 ? versionLoadError ?? "点击“刷新列表”获取可用版本。" :
                versionList.Items.Count == 0 ? "没有符合条件的版本。" :
                (usingCachedVersions ? "本地缓存 · " : "") +
                "共 " + versionList.Items.Count + " 个版本。选中版本后可选择加载器。";
        }

        async Task LoadVersionsAsync()
        {
            if (loadingVersions) return;
            loadingVersions = true;
            versionLoadError = null;
            versionReloadButton.IsEnabled = false;
            FilterVersions();
            try
            {
                var catalog = new MinecraftVersionCatalog(settings);
                var versions = await catalog.LoadAsync(true, updateCancellation.Token);
                usingCachedVersions = catalog.UsingCachedManifest;
                gameVersions.Clear();
                foreach (var version in versions) gameVersions.Add(version);
                Status((usingCachedVersions ? "在线列表暂不可用，已读取缓存。" : "已刷新在线游戏列表。") +
                    " 共 " + gameVersions.Count + " 个版本。");
            }
            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                versionLoadError = "无法获取版本列表，请检查网络后刷新。";
                Status(versionLoadError);
                Log(ex.ToString());
            }
            finally
            {
                loadingVersions = false;
                versionReloadButton.IsEnabled = true;
                FilterVersions();
            }
        }

        void RefreshVersionActions()
        {
            bool running = versionInstallJob?.IsRunning == true;
            if (versionInstallButton != null)
                versionInstallButton.IsEnabled = !running && versionList?.SelectedItem is MinecraftVersionEntry;
            if (versionCancelButton != null)
                versionCancelButton.IsEnabled = running;
        }

        async Task InstallSelectedVersionAsync()
        {
            if (!(versionList?.SelectedItem is MinecraftVersionEntry selected) ||
                versionInstallJob?.IsRunning == true)
                return;
            string version = selected.Id;
            string loader = Convert.ToString(versionLoader?.SelectedItem) ?? "原版";

            versionInstallJob = downloads.Enqueue("游戏", version + " · " + loader, async (token, progress, bytes) =>
            {
                progress.Report("正在安装游戏及依赖：" + version);
                var profile = await modLoaders.InstallAsync(version, loader, null, progress, bytes, token);
                token.ThrowIfCancellationRequested();
                var registered = settings.Profiles.FirstOrDefault(p => p.LaunchVersion == profile.LaunchVersion);
                if (registered == null)
                {
                    settings.Profiles.Add(profile);
                    try { store.Save(settings); }
                    catch { settings.Profiles.Remove(profile); throw; }
                }
                UseInstalledProfile(registered ?? profile);
                progress.Report("安装完成：" + (registered ?? profile).Name);
                Status("安装完成：" + (registered ?? profile).Name);
            });
            versionInstallJob.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(DownloadJob.Status)) RefreshVersionActions();
            };
            RefreshVersionActions();
            Status("已加入下载任务：" + version + " · " + loader);
            await Task.CompletedTask;
        }

        UIElement BuildDownloadCenter()
        {
            var root = Col();
            downloadSearchQuery = new TextBox();
            ApplySearchInputStyle(downloadSearchQuery, "按名称或资源类型查找任务");
            downloadStatusFilter = new ComboBox
            {
                ItemsSource = new[] { "全部任务", "进行中", "已完成", "失败或取消", "等待中" },
                SelectedIndex = 0
            };
            ApplySearchInputStyle(downloadStatusFilter, "按任务状态筛选");
            root.Children.Add(SearchCard(Col(Txt("下载任务", 16, true),
                SearchFields(("名称", downloadSearchQuery), ("状态", downloadStatusFilter)),
                Row(Btn("刷新列表", ApplyDownloadFilter),
                    Btn("重置条件", () =>
                    {
                        downloadSearchQuery.Clear();
                        downloadStatusFilter.SelectedIndex = 0;
                    })))));
            downloadCounter = Txt("", 12, false, true);
            downloadList = new ListBox
            {
                ItemsSource = downloads.Jobs, MinHeight = 134, MaxHeight = 294,
                ItemTemplate = (DataTemplate)FindResource("DownloadItemTemplate")
            };
            downloadSearchQuery.TextChanged += (sender, args) => ApplyDownloadFilter();
            downloadStatusFilter.SelectionChanged += (sender, args) => ApplyDownloadFilter();
            ApplyDownloadFilter();
            root.Children.Add(ResultsCard(Col(Txt("任务列表", 16, true), downloadCounter, downloadList)));

            var heading = Txt("任务详情", 16, true);
            var status = Txt("", 14);
            status.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Status")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = "尚未选择任务"
            });
            var detail = Txt("", 12, false, true);
            detail.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Detail")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = ""
            });
            var bytesLabel = Txt("", 13);
            bytesLabel.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.BytesLabel")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = ""
            });
            var speedLabel = Txt("", 13);
            speedLabel.Margin = new Thickness(24, 0, 0, 9);
            speedLabel.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Speed")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = ""
            });
            var percentLabel = Txt("", 13, true);
            percentLabel.Margin = new Thickness(24, 0, 0, 9);
            percentLabel.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.PercentLabel")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = ""
            });
            downloadProgressBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 18,
                Margin = new Thickness(0, 8, 0, 10) };
            // ProgressBar.Value inherits RangeBase's TwoWay binding default.
            // Percent has a private setter, so both bindings MUST be OneWay.
            downloadProgressBar.SetBinding(ProgressBar.ValueProperty, new Binding("SelectedItem.Percent")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = 0d
            });
            downloadProgressBar.SetBinding(ProgressBar.IsIndeterminateProperty,
                new Binding("SelectedItem.IsIndeterminate")
                {
                    Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = false
                });
            var cancel = Btn("取消任务", () => downloads.Cancel(downloadList.SelectedItem as DownloadJob));
            var retry = Btn("重试任务", () => downloads.Retry(downloadList.SelectedItem as DownloadJob), true);
            cancel.SetBinding(IsEnabledProperty, new Binding("SelectedItem.IsRunning")
                { Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = false });
            retry.SetBinding(IsEnabledProperty, new Binding("SelectedItem.CanRetry")
                { Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = false });
            root.Children.Add(Card(Col(heading, status, detail, downloadProgressBar,
                Row(bytesLabel, speedLabel, percentLabel),
                Row(cancel, retry, Btn("返回游戏下载", () => Navigate("游戏版本")),
                    Btn("皮肤下载", () => Navigate("皮肤下载"))))));
            if (downloadList.Items.Count > 0)
                downloadList.SelectedIndex = 0;
            return root;
        }

        /// <summary>
        /// Offline PCL-style layout regression. Exercise real WPF styles and
        /// navigation on the normal window and on the single-EXE payload.
        /// Never fetches online manifests, installs mods or opens sign-in.
        /// </summary>
        internal async Task VerifyPclLayoutAsync()
        {
            if (TopLaunchButton == null || TopDownloadButton == null || TopMultiplayerButton == null ||
                TopSettingsButton == null || TopMoreButton == null ||
                PageHost == null || SideMenu == null || BackButton == null)
                throw new InvalidOperationException("PCL 顶部导航或侧边栏控件缺失");
            if (PageScroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                throw new InvalidOperationException("内容页出现不允许的横向滚动");
            if (FindResource("ResourceItemTemplate") == null ||
                FindResource("VersionItemTemplate") == null ||
                FindResource("DownloadItemTemplate") == null ||
                FindResource("ServerItemTemplate") == null ||
                FindResource("LocalResourceItemTemplate") == null ||
                FindResource("CurseForgeItemTemplate") == null ||
                FindResource("SearchPanel") == null ||
                FindResource("ResultsPanel") == null ||
                FindResource("SearchTextBox") == null ||
                FindResource("SearchComboBox") == null ||
                FindResource("FilterChip") == null)
                throw new InvalidOperationException("资源、游戏版本或下载任务缺少卡片布局模板");

            var originalProfile = activeProfile;
            var sampleProfile = new GameProfile
            {
                Id = "offline-layout-profile", Name = "原版 1.21.1",
                BaseVersion = "1.21.1", LaunchVersion = "1.21.1", Loader = "vanilla",
                Directory = Path.Combine(Path.GetTempPath(), "blockcraft-layout-probe-" + Guid.NewGuid().ToString("N"))
            };
            settings.Profiles.Add(sampleProfile);
            activeProfile = sampleProfile;
            Navigate("首页");
            if (homeLaunchButton == null || PageHost.Content == null ||
                LaunchPane.Visibility != Visibility.Visible || SideMenu.Visibility != Visibility.Collapsed ||
                homeVersionCaption.Text != sampleProfile.Name)
                throw new InvalidOperationException("启动页账号面板或版本入口未显示");
            ArrangeLayoutProbe(1080, 680, "01-launch");

            Navigate("版本选择");
            if (profileList?.ItemTemplate == null || profileList.SelectedItem != sampleProfile ||
                ContextNavigation.Visibility != Visibility.Visible)
                throw new InvalidOperationException("版本选择没有显示已安装版本");
            ArrangeLayoutProbe(1080, 680, "02-versions");
            OpenVersionSettings("版本选择");
            Navigate("模组更新");
            if (localResourceList?.ItemTemplate == null || sidebarButtons.Count != 3)
                throw new InvalidOperationException("版本设置侧栏或本地资源列表缺失");
            await VerifyLocalModIconsAsync(sampleProfile);
            Navigate("存档管理");
            BackButton_Click(this, new RoutedEventArgs());
            if (currentPage != "版本选择")
                throw new InvalidOperationException("版本设置无法返回版本选择");

            Navigate("游戏版本");
            if (versionList == null || versionList.ItemTemplate == null ||
                versionQuery == null || versionCounter == null ||
                versionCategory == null || !sidebarButtons.ContainsKey("资源中心") ||
                !sidebarButtons.ContainsKey("皮肤下载") ||
                sidebarButtons.ContainsKey("设置") || sidebarButtons.ContainsKey("皮肤 DIY"))
                throw new InvalidOperationException("游戏版本卡片与侧边导航不完整");

            Navigate("资源中心");
            if (resourceList == null || resourceList.ItemTemplate == null ||
                resourceQuery == null || resourceSort == null ||
                resourceVersion == null || resourceSource == null ||
                resourceCategory == null || resourceLoader == null ||
                resourceSearchButton == null || resourceCounter == null ||
                resourceCategory.SelectedIndex != 0)
                throw new InvalidOperationException("PCL 模组搜索字段、分类或结果卡片未加载");
            resourceResults.Add(new ResourceHit {
                ProjectId = "offline-test-only", Kind = "mod", Name = "中文资源标题排布自检",
                Description = new string('测', 210), Author = "测试作者",
                DownloadsLabel = "3.1 万次下载",
                UpdatedLabel = "更新 2026-11-10", VersionsLabel = "1.21.1",
                LoaderLabel = "fabric", CategoryLabel = "性能优化"
            });
            resourceResults.Add(new ResourceHit
            {
                ProjectId = "second-test-item", Kind = "mod",
                Name = "示例模组 · 建筑装饰", Author = "作者名称",
                Description = "这里显示模组的简介。点击列表或按 Enter 查看详情与安装选项。",
                CategoryLabel = "装饰", DownloadsLabel = "1.2 万次下载", LoaderLabel = "Fabric"
            });
            resourceQuery.Text = "示例筛选";
            resourceList.SelectedIndex = 0;
            ArrangeLayoutProbe(1080, 680, "03-download");
            string originalTheme = settings.Theme;
            try
            {
                settings.Theme = "深色";
                ApplyTheme();
                ArrangeLayoutProbe(1080, 680, "06-dark-download");
            }
            finally
            {
                settings.Theme = originalTheme;
                ApplyTheme();
            }
            ArrangeLayoutProbe(850, 560);
            if (resourceList.Items.Count != 2 || resourceSource.SelectedIndex != 0 ||
                resourceMatchVersion == null || resourceQuery.ActualWidth < 140 ||
                Math.Abs(resourceQuery.ActualWidth - resourceSource.ActualWidth) > 2)
                throw new InvalidOperationException("资源列表或窄窗口筛选网格布局错误");
            var selection = resourceList.SelectedItem as ResourceHit;
            ShowResourceDetails(selection);
            if (DetailHost.Content == null || PageHost.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("资源详情未在当前窗口显示");
            BackButton_Click(this, new RoutedEventArgs());
            if (DetailHost.Content != null || PageHost.Visibility != Visibility.Visible ||
                resourceList.SelectedItem != selection || resourceQuery.Text != "示例筛选")
                throw new InvalidOperationException("从详情返回时丢失了搜索条件或选中项目");

            Navigate("光影引擎");
            Navigate("资源包");
            Navigate("整合包");
            if (packSearchResults == null || packSearchResults.ItemTemplate == null ||
                packSearchQuery == null || packSortMode == null ||
                packVersionFilter == null || packSearchStatus == null ||
                packPrevButton == null || packNextButton == null)
                throw new InvalidOperationException("整合包搜索、排序、版本筛选或分页控件未加载");

            Navigate("下载管理");
            if (downloadList == null || downloadList.ItemTemplate == null ||
                downloadProgressBar == null || downloadSearchQuery == null ||
                downloadStatusFilter == null || downloadCounter == null)
                throw new InvalidOperationException("下载任务搜索、筛选或进度布局不完整");

            Navigate("多人联机");
            if (serverList?.ItemTemplate == null || sidebarButtons.Count != 3 ||
                !sidebarButtons.ContainsKey("查找服务器"))
                throw new InvalidOperationException("服务器列表或联机导航缺失");
            Navigate("查找服务器");
            if (serverFinderResults?.ItemTemplate == null ||
                serverFinderSourceButton == null || serverFinderSearchButton == null ||
                serverFinderQuery == null || serverFinderProvider == null ||
                serverFinderVersion == null || serverFinderSort == null ||
                serverFinderOnlineOnly == null || serverFinderPrevButton == null ||
                serverFinderNextButton == null ||
                !sidebarButtons.ContainsKey("多人联机") ||
                !sidebarButtons.ContainsKey("局域网"))
                throw new InvalidOperationException("服务器查找搜索、筛选、分页或入口缺失");
            ArrangeLayoutProbe(1080, 680, "06-server-discovery");
            ArrangeLayoutProbe(850, 560, "06-server-discovery-narrow");
            Navigate("局域网");
            if (lanWorldList?.ItemTemplate == null)
                throw new InvalidOperationException("局域网列表缺少行布局模板");

            Navigate("皮肤下载");
            if (skinStudio == null || PageHost.Content != skinStudio ||
                !skinStudio.DownloadMode || !sidebarButtons.ContainsKey("皮肤下载") ||
                sidebarButtons.ContainsKey("皮肤 DIY"))
                throw new InvalidOperationException("皮肤下载未在下载分类中单独打开");
            skinStudio.VerifyOfflineLayout();
            Navigate("皮肤 DIY");
            if (skinStudio == null || PageHost.Content != skinStudio ||
                skinStudio.DownloadMode || !sidebarButtons.ContainsKey("皮肤 DIY") ||
                sidebarButtons.ContainsKey("皮肤下载"))
                throw new InvalidOperationException("皮肤 DIY 未独立于下载分类");
            skinStudio.VerifyOfflineLayout();
            ArrangeLayoutProbe(1080, 680, "04-skin");
            skinStudio.Verify3DRenderedPixels();

            // The optional MCIM page is built locally without calling its
            // service. Verify the complete visual search path and then restore
            // the user's consent setting before leaving the page.
            bool previousMcim = settings.McimEnabled;
            try
            {
                settings.McimEnabled = true;
                Navigate("CurseForge");
                if (cfSearchResults == null || cfSearchResults.ItemTemplate == null ||
                    cfSearchQuery == null || cfVersionFilter == null ||
                    cfCategoryFilter == null || cfSortMode == null ||
                    cfSearchStatus == null)
                    throw new InvalidOperationException("CurseForge 图文搜索筛选控件未加载");
                cfLoadedHits.Clear();
                cfLoadedHits.Add(new CurseForgeHit
                {
                    Id = 123, Name = "离线图文自检", Summary = "UI only",
                    CategoryLabel = "Technology", VersionsLabel = "近期版本：1.21.1",
                    DownloadCount = 50000, DownloadsLabel = "5 万次下载"
                });
                ApplyCurseForgeFilters();
                if (cfSearchResults.Items.Count != 1)
                    throw new InvalidOperationException("CurseForge 本页结果绑定失效");
                cfSearchResults.SelectedIndex = 0;
                ShowCurseForgeDetails();
                if (DetailHost.Content == null)
                    throw new InvalidOperationException("CurseForge 图文详情未显示");
                BackButton_Click(this, new RoutedEventArgs());
                cfVersionFilter.Text = "1.20.1";
                if (cfSearchResults.Items.Count != 0)
                    throw new InvalidOperationException("CurseForge 版本筛选未生效");
                cfVersionFilter.Text = "";
            }
            finally { settings.McimEnabled = previousMcim; }

            Navigate("启动器更新");
            if (PageHost.Content == null)
                throw new InvalidOperationException("启动器更新页无法打开");

            Navigate("运行日志");
            if (PageHost.Content == null)
                throw new InvalidOperationException("运行日志页无法打开");

            Navigate("设置");
            if (settingsRoot == null || settingsMemory == null || sidebarButtons.Count != 4)
                throw new InvalidOperationException("启动设置或设置分类缺失");
            var originalRoot = settingsRoot.Text;
            settingsRoot.Text = "layout-probe";
            Navigate("下载设置");
            if (settingsNetworkMode == null || settingsDownloadThreads == null)
                throw new InvalidOperationException("下载设置缺少字段");
            ArrangeLayoutProbe(1080, 680, "05-settings");
            if (Math.Abs(settingsNetworkMode.TranslatePoint(new Point(), PageHost).X -
                    settingsDownloadThreads.TranslatePoint(new Point(), PageHost).X) > 1)
                throw new InvalidOperationException("下载设置输入框未左对齐");
            Navigate("个性化");
            if (settingsTheme == null || settingsZoom == null)
                throw new InvalidOperationException("个性化设置缺少字段");
            Navigate("更新设置");
            if (settingsUpdateChannel == null || settingsAutoCheck == null ||
                settingsAutoDownload == null || settingsAutoInstallOnExit == null)
                throw new InvalidOperationException("更新设置缺少字段");
            Navigate("设置");
            if (settingsRoot.Text != "layout-probe")
                throw new InvalidOperationException("切换设置分类时丢失未保存的修改");
            settingsRoot.Text = originalRoot;

            Navigate("关于");
            settings.Profiles.Remove(sampleProfile);
            activeProfile = originalProfile;
            Navigate("首页");
            if (currentPage != "首页" || PageHost.Content == null)
                throw new InvalidOperationException("导航返回首页失败");
        }

        async Task VerifyLocalModIconsAsync(GameProfile profile)
        {
            try
            {
                var expected = LocalModIconSanityChecks.CreateFixtures(InstanceResourceService.Folder(profile, "模组"));
                await RefreshLocalResourceListAsync();
                if (localResourceList.Items.Count != expected.Count)
                    throw new InvalidOperationException("本地模组图标测试未列出全部文件");

                foreach (LocalResourceItem item in localResourceList.Items)
                {
                    int expectedWidth = expected[item.Name];
                    if (!item.IsMod || item.Disabled != item.Name.EndsWith(".disabled", StringComparison.Ordinal) ||
                        (item.Icon?.PixelWidth ?? 0) != expectedWidth || (item.Icon != null && !item.Icon.IsFrozen))
                        throw new InvalidOperationException("本地模组图片或停用状态读取错误：" + item.Name);

                    var template = localResourceList.ItemTemplate;
                    var row = new ContentPresenter { Content = item, ContentTemplate = template };
                    row.Measure(new Size(400, 70));
                    row.Arrange(new Rect(0, 0, 400, 70));
                    var thumbnail = template.FindName("LocalModThumbnail", row) as Image;
                    var fallback = template.FindName("LocalModFallback", row) as FrameworkElement;
                    if (thumbnail == null || fallback == null || thumbnail.Source != item.Icon ||
                        fallback.Visibility != (expectedWidth == 0 ? Visibility.Visible : Visibility.Collapsed))
                        throw new InvalidOperationException("本地模组列表图片或默认图标未正确显示：" + item.Name +
                            " · 缩略图绑定=" + (thumbnail != null && thumbnail.Source == item.Icon) +
                            " · 默认图标=" + fallback?.Visibility);
                }
                ArrangeLayoutProbe(1080, 680, "07-installed-mod-icons");
            }
            finally
            {
                if (Directory.Exists(profile.Directory)) Directory.Delete(profile.Directory, true);
            }
        }

        void ArrangeLayoutProbe(int width, int height, string captureName = null)
        {
            var root = (FrameworkElement)Content;
            var size = new Size(width, height);
            var rect = new Rect(size);
            root.Measure(size);
            root.Arrange(rect);
            UpdateShellSize();
            root.Measure(size);
            root.Arrange(rect);
            root.UpdateLayout();
            string directory = Environment.GetEnvironmentVariable("BLOCKCRAFT_UI_CAPTURE_DIR");
            if (captureName == null || string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, captureName + ".png")))
                encoder.Save(file);
        }

        /// <summary>
        /// Windows CI checks the real navigation/binding path that previously
        /// crashed when selecting a completed download (read-only Percent).
        /// No network or game files are touched.
        /// </summary>
        void ApplyDownloadFilter()
        {
            if (downloadList == null) return;
            string query = (downloadSearchQuery?.Text ?? "").Trim();
            string state = Convert.ToString(downloadStatusFilter?.SelectedItem) ?? "全部任务";
            downloadList.Items.Filter = raw =>
            {
                var job = raw as DownloadJob;
                if (job == null) return false;
                if (query.Length > 0 &&
                    (job.Title ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (job.Kind ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (job.Detail ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
                switch (state)
                {
                    case "进行中": return job.IsRunning;
                    case "已完成": return job.Status == "已完成";
                    case "失败或取消": return job.Status == "失败" || job.Status == "已取消";
                    case "等待中": return job.Status == "等待中";
                    default: return true;
                }
            };
            if (downloadCounter != null)
                downloadCounter.Text = downloadList.Items.Count == 0 ?
                    "当前没有符合条件的任务。" :
                    "显示 " + downloadList.Items.Count + " / " + downloads.Jobs.Count + " 个任务";
        }

        internal void VerifyDownloadPageBindings()
        {
            Navigate("下载管理");
            if (downloadList == null || downloadProgressBar == null)
                throw new InvalidOperationException("下载管理页面或进度条未建立");
            var valueBinding = BindingOperations.GetBinding(downloadProgressBar, ProgressBar.ValueProperty);
            var busyBinding = BindingOperations.GetBinding(downloadProgressBar, ProgressBar.IsIndeterminateProperty);
            if (valueBinding == null || valueBinding.Mode != BindingMode.OneWay ||
                busyBinding == null || busyBinding.Mode != BindingMode.OneWay)
                throw new InvalidOperationException("下载进度只读属性必须采用 OneWay 绑定");

            // Selecting a concrete DownloadJob forces WPF to resolve all
            // SelectedItem.* bindings, even if the original queue is empty.
            var sample = new DownloadJob("自检", "下载进度绑定验证",
                (token, stage, bytes) => Task.CompletedTask);
            downloads.Jobs.Insert(0, sample);
            downloadList.SelectedItem = sample;
            PageHost.UpdateLayout();
            downloadList.UpdateLayout();
            downloadProgressBar.UpdateLayout();
            BindingOperations.GetBindingExpression(downloadProgressBar, ProgressBar.ValueProperty)?.UpdateTarget();
            BindingOperations.GetBindingExpression(downloadProgressBar,
                ProgressBar.IsIndeterminateProperty)?.UpdateTarget();
        }

        static void WireResultActions(ListBox list, Action open, params Button[] actions)
        {
            void Refresh()
            {
                foreach (var action in actions) action.IsEnabled = list.SelectedItem != null;
            }
            list.SelectionChanged += (sender, args) => Refresh();
            list.MouseDoubleClick += (sender, args) =>
            {
                if (args.OriginalSource is DependencyObject source &&
                    ItemsControl.ContainerFromElement(list, source) is ListBoxItem)
                    open();
            };
            list.KeyDown += (sender, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter && list.SelectedItem != null)
                {
                    args.Handled = true;
                    open();
                }
            };
            Refresh();
        }

        static Border ProjectResults(TextBlock status, ListBox results, WrapPanel actions,
            Button previous = null, Button next = null)
        {
            var footer = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
            if (previous != null && next != null)
            {
                var pages = Row(previous, next);
                DockPanel.SetDock(pages, Dock.Right);
                footer.Children.Add(pages);
            }
            footer.Children.Add(actions);
            return ResultsCard(Col(Txt("搜索结果", 16, true), status, results, footer));
        }

        UIElement BuildResources(string type)
        {
            resourceType = type;
            resourceResults.Clear();
            resourcePage = 0;
            bool isMod = type == "mod";
            string title = isMod ? "搜索模组" : type == "shader" ? "搜索光影包" : "搜索资源包";
            resourceQuery = new TextBox();
            resourceVersion = new ComboBox
            {
                IsEditable = true, IsTextSearchEnabled = true,
                ItemsSource = (new[] { "全部版本", activeProfile?.BaseVersion,
                    "1.21.11", "1.21.10", "1.21.8", "1.21.1", "1.20.1", "1.19.4" })
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray(),
                SelectedIndex = 0
            };
            resourceSource = new ComboBox
            {
                ItemsSource = isMod ? new[] { "Modrinth", "CurseForge（通过 MCIM）", "GitHub 开源项目（Releases）" } : new[] { "Modrinth" },
                SelectedIndex = 0, IsEnabled = isMod
            };
            resourceCategory = new ComboBox
            {
                ItemsSource = new[] { "全部类型", "性能优化", "科技", "冒险", "装饰",
                    "实用工具", "世界生成", "生物", "支持库", "玩法", "装备", "存储", "管理" },
                SelectedIndex = 0
            };
            resourceLoader = new ComboBox
            {
                ItemsSource = new[] { "全部加载器", "Fabric", "Forge", "NeoForge", "Quilt" },
                SelectedIndex = 0
            };
            resourceSort = new ComboBox
            {
                ItemsSource = new[] { "相关度", "下载量", "最近更新", "最新发布" },
                SelectedIndex = 0
            };
            resourceMatchVersion = new CheckBox
            {
                Content = "仅显示与当前版本兼容的项目",
                IsEnabled = activeProfile != null,
                ToolTip = "使用当前游戏版本；搜索模组时同时匹配加载器。"
            };
            ApplySearchInputStyle(resourceQuery, "输入名称或关键词，留空可浏览推荐项目");
            ApplySearchInputStyle(resourceVersion, "可选择或输入 Minecraft 游戏版本");
            ApplySearchInputStyle(resourceSource, "选择搜索来源");
            ApplySearchInputStyle(resourceCategory, "筛选模组类型");
            ApplySearchInputStyle(resourceLoader, "筛选模组加载器");
            ApplySearchInputStyle(resourceSort, "设置结果排序");
            async Task SearchFirstPage()
            {
                resourcePage = 0;
                await SearchResourcesAsync();
            }
            resourceQuery.KeyDown += async (sender, args) =>
            {
                if (args.Key != System.Windows.Input.Key.Enter) return;
                args.Handled = true;
                await SearchFirstPage();
            };

            resourceSearchButton = Btn("搜索", async () => await SearchFirstPage(), true);
            resourceSearchButton.MinWidth = 120;
            var clear = Btn("重置条件", () =>
            {
                resourceSearchCancellation?.Cancel();
                resourceSearchCancellation = null;
                resourceSearchButton.IsEnabled = true;
                resourceQuery.Clear();
                resourceVersion.SelectedIndex = 0;
                resourceSource.SelectedIndex = 0;
                resourceCategory.SelectedIndex = 0;
                resourceLoader.SelectedIndex = 0;
                resourceSort.SelectedIndex = 0;
                resourceMatchVersion.IsChecked = false;
                resourcePage = 0;
                resourceResults.Clear();
                resourceCounter.Text = "点击“搜索”查看项目。";
                resourcePrevButton.IsEnabled = resourceNextButton.IsEnabled = false;
            });
            var form = isMod ? SearchFields(("名称", resourceQuery), ("来源", resourceSource),
                    ("版本", resourceVersion), ("类型", resourceCategory),
                    ("加载器", resourceLoader), ("排序", resourceSort))
                : SearchFields(("名称", resourceQuery), ("来源", resourceSource),
                    ("版本", resourceVersion), ("排序", resourceSort));
            var root = Col(SearchCard(Col(Txt(title, 16, true), form,
                resourceMatchVersion, Row(resourceSearchButton, clear))));

            resourceList = new ListBox
            {
                ItemsSource = resourceResults, ItemTemplate = (DataTemplate)FindResource("ResourceItemTemplate"),
                MinHeight = 132, MaxHeight = 372
            };
            resourceCounter = Txt("点击“搜索”查看项目。", 12, false, true);
            resourcePrevButton = Btn("上一页", async () =>
            {
                if (resourcePage <= 0) return;
                resourcePage--;
                await SearchResourcesAsync();
            });
            resourceNextButton = Btn("下一页", async () =>
            {
                resourcePage++;
                await SearchResourcesAsync();
            });
            resourcePrevButton.IsEnabled = resourceNextButton.IsEnabled = false;
            void Open() => ShowResourceDetails(resourceList.SelectedItem as ResourceHit);
            var details = Btn("查看详情", Open);
            var install = Btn("安装到当前版本", async () => await InstallResourceAsync(), true);
            WireResultActions(resourceList, Open, details, install);
            root.Children.Add(ProjectResults(resourceCounter, resourceList, Row(install, details),
                resourcePrevButton, resourceNextButton));

            if (type == "shader")
                root.Children.Add(Card(Col(Txt("光影组件", 16, true),
                    Txt("按当前版本安装 Iris 或 Sodium，也可前往 OptiFine 官网。", 12, false, true),
                    Row(Btn("安装 Iris", async () =>
                            await InstallGraphicsModAsync("YL57xq9U", "Iris Shaders"), true),
                        Btn("安装 Sodium", async () =>
                            await InstallGraphicsModAsync("AANobbMI", "Sodium")),
                        Btn("打开 OptiFine 官网", OpenOfficialOptiFine)))));
            return root;
        }

        async Task InstallGraphicsModAsync(string projectId, string name)
        {
            var profile = activeProfile;
            if (profile == null) { MessageBox.Show("请先选择已经安装的游戏实例"); return; }
            if (profile.Loader == "vanilla" || string.IsNullOrWhiteSpace(profile.Loader))
            {
                MessageBox.Show("当前为原版实例。请先安装支持的 Fabric、Quilt 或 NeoForge 加载器，再安装光影组件。");
                return;
            }
            var hit = new ResourceHit { ProjectId = projectId, Kind = "mod", Name = name };
            downloads.Enqueue("光影组件", name + " → " + profile.Name, async (token, stage, bytes) =>
            {
                stage.Report("正在选择与 " + profile.BaseVersion + " / " + profile.Loader + " 兼容的版本...");
                using (var client = new ModrinthClient(settings))
                {
                    string installed = await client.DownloadLatestAsync(hit, profile, token, bytes);
                    stage.Report("已安装：" + installed);
                }
            });
            Status("已加入光影组件任务：" + name);
            await Task.CompletedTask;
        }

        void OpenOfficialOptiFine()
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://optifine.net/downloads")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { ShowError("打开 OptiFine 官方网站失败", ex); }
        }

        async Task SearchResourcesAsync()
        {
            // An opt-in alternate provider remains a separate workflow. We
            // never promise merged results when CurseForge is not enabled.
            if (resourceSource?.SelectedIndex == 1)
            {
                string query = resourceQuery.Text;
                string version = resourceMatchVersion.IsChecked == true
                    ? activeProfile?.BaseVersion : resourceVersion.Text;
                Navigate("CurseForge");
                if (settings.McimEnabled)
                {
                    cfSearchQuery.Text = query;
                    if (!string.IsNullOrWhiteSpace(version) && !version.StartsWith("全部", StringComparison.Ordinal))
                        cfVersionFilter.Text = version;
                    await SearchCurseForgeAsync();
                }
                return;
            }

            bool searchGitHub = resourceType == "mod" &&
                resourceSource?.SelectedIndex == 2;
            var oldRequest = resourceSearchCancellation;
            oldRequest?.Cancel();
            using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            {
                resourceSearchCancellation = request;
                if (resourceSearchButton != null) resourceSearchButton.IsEnabled = false;
                if (resourceCounter != null)
                    resourceCounter.Text = searchGitHub
                        ? "正在搜索具有明确开源许可证的 GitHub 仓库..."
                        : "正在向 Modrinth 搜索项目...";
                Status(searchGitHub ? "正在搜索 GitHub 开源模组仓库..."
                    : "正在搜索 Modrinth...");
                try
                {
                    string sort = new[] { "relevance", "downloads", "updated", "newest" }
                        [Math.Max(0, Math.Min(3, resourceSort?.SelectedIndex ?? 0))];
                    bool compatible = resourceMatchVersion?.IsChecked == true && activeProfile != null;
                    string selectedVersion = resourceVersion?.Text?.Trim();
                    string gameVersion = compatible ? activeProfile.BaseVersion :
                        string.IsNullOrWhiteSpace(selectedVersion) ||
                        selectedVersion.StartsWith("全部", StringComparison.Ordinal) ?
                        null : selectedVersion;
                    string loader = resourceType != "mod" ? null :
                        compatible ? activeProfile.Loader : resourceLoader.SelectedIndex > 0
                            ? (resourceLoader.SelectedItem as string)?.ToLowerInvariant() : null;
                    string category = null;
                    if (resourceType == "mod" && resourceCategory != null &&
                        resourceCategory.SelectedIndex > 0)
                    {
                        string[] categories = { null, "optimization", "technology",
                            "adventure", "decoration", "utility", "worldgen", "mobs",
                            "library", "gameplay", "equipment", "storage", "management" };
                        category = categories[Math.Min(resourceCategory.SelectedIndex,
                            categories.Length - 1)];
                    }
                    ModrinthSearchPage result;
                    if (searchGitHub)
                    {
                        using (var client = new GitHubOpenSourceModClient(settings))
                            result = await client.SearchAsync(resourceQuery.Text,
                                resourcePage, request.Token);
                    }
                    else
                    {
                        using (var client = new ModrinthClient(settings))
                            result = await client.SearchPageAsync(resourceQuery.Text,
                                resourceType, request.Token, gameVersion, loader, sort,
                                resourcePage * 20, category);
                    }
                    if (request.IsCancellationRequested ||
                        !ReferenceEquals(resourceSearchCancellation, request))
                        return;
                    resourceResults.Clear();
                    foreach (var item in result.Hits) resourceResults.Add(item);
                    int total = Math.Min(2000, result.TotalHits);
                    int pageSize = searchGitHub ? 40 : 20;
                    string provider = searchGitHub ? "GitHub 开源仓库" : "Modrinth";
                    if (resourceCounter != null)
                        resourceCounter.Text = result.Hits.Count == 0
                            ? "当前页没有符合条件的开源项目；请尝试其他关键词或翻页"
                            : provider + "  ·  第 " + (resourcePage + 1) +
                              " 页  ·  " + result.Hits.Count + " 项" +
                              (searchGitHub ? "（安装前核对版本、加载器及 SHA-256）" :
                                  "  ·  共 " + result.TotalHits + " 项");
                    if (resourcePrevButton != null)
                        resourcePrevButton.IsEnabled = resourcePage > 0;
                    if (resourceNextButton != null)
                        resourceNextButton.IsEnabled = searchGitHub
                            ? (resourcePage + 1) * pageSize < total && resourcePage < 50
                            : result.Hits.Count == 20 &&
                              (resourcePage + 1) * 20 < total;
                    Status(provider + " 第 " + (resourcePage + 1) + " 页，"
                        + result.Hits.Count + " 个结果");
                }
                catch (OperationCanceledException)
                {
                    if (ReferenceEquals(resourceSearchCancellation, request) &&
                        resourceCounter != null)
                        resourceCounter.Text = "搜索已取消或超时，请重试";
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(resourceSearchCancellation, request)) return;
                    if (resourceCounter != null)
                        resourceCounter.Text = "搜索失败，请调整关键词或网络设置";
                    ShowError(searchGitHub ? "GitHub 开源模组搜索失败（可能有 API 限流）"
                        : "Modrinth 搜索失败", ex);
                }
                finally
                {
                    if (ReferenceEquals(resourceSearchCancellation, request))
                    {
                        resourceSearchCancellation = null;
                        if (resourceSearchButton != null)
                            resourceSearchButton.IsEnabled = true;
                    }
                }
            }
        }
        void ShowResourceDetails(ResourceHit hit)
        {
            if (hit == null) return;
            var description = Txt(hit.Description ?? "暂无项目简介。", 13);
            var project = Col(Txt(hit.Name, 20, true),
                Txt(hit.KindLabel + " · " + hit.SourceLabel + " · " + hit.Author,
                    12, false, true),
                description);
            // Always show a recognizable mod symbol. Overlay trusted artwork
            // when available; invalid network images leave the symbol visible.
            var artworkFrame = new Border {
                Width = 68, Height = 68, CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10), ClipToBounds = true
            };
            artworkFrame.SetResourceReference(Border.BackgroundProperty, "BadgeBrush");
            var artworkGrid = new Grid();
            var fallbackIcon = new TextBlock {
                Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 29, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            fallbackIcon.SetResourceReference(TextBlock.ForegroundProperty, "AccentInkBrush");
            artworkGrid.Children.Add(fallbackIcon);
            if (!string.IsNullOrEmpty(hit.IconUrl))
            {
                var remoteIcon = new Image {
                    Width = 68, Height = 68, Stretch = Stretch.UniformToFill,
                    ToolTip = hit.IconDescription
                };
                remoteIcon.SetBinding(Image.SourceProperty,
                    new Binding("IconUrl") { Source = hit, Mode = BindingMode.OneWay });
                artworkGrid.Children.Add(remoteIcon);
            }
            artworkFrame.Child = artworkGrid;
            project.Children.Insert(0, artworkFrame);
            var overview = Card(Col(Txt("项目信息", 16, true),
                Txt("游戏版本  " + (hit.VersionsLabel ?? "未标注"), 12),
                Txt("加载器  " + (hit.LoaderLabel ?? "未标注"), 12),
                Txt("类型  " + (hit.CategoryLabel ?? "未标注"), 12),
                Txt("下载量  " + (hit.DownloadsLabel ?? "未知"), 12),
                Txt("最近更新  " + (hit.UpdatedLabel ?? "未知"), 12)));
            bool isPack = hit.Kind == "modpack";
            var actions = Row(
                Btn(isPack ? "安装整合包" : "安装到当前版本", async () =>
                {
                    if (isPack) await InstallSelectedModpackAsync(hit);
                    else await InstallResourceAsync(hit);
                }, true),
                Btn(hit.Source == "GitHub" ? "打开 GitHub 项目页" : "打开 Modrinth 项目页", () =>
                {
                    if (hit.Source == "GitHub")
                    {
                        string repository = hit.ProjectId ?? "";
                        var parts = repository.Split('/');
                        if (parts.Length != 2 || parts[0].Length < 1 || parts[0].Length > 39 ||
                            parts[1].Length < 1 || parts[1].Length > 100 ||
                            parts.Any(value => value.Any(c =>
                                !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')))
                        {
                            MessageBox.Show(this, "GitHub 项目路径无效，无法打开。", "开源模组");
                            return;
                        }
                        try
                        {
                            Process.Start(new ProcessStartInfo(
                                "https://github.com/" + repository) { UseShellExecute = true });
                        }
                        catch (Exception ex) { ShowError("打开 GitHub 项目页失败", ex); }
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(hit.ProjectId) ||
                        hit.ProjectId.Length > 64 ||
                        hit.ProjectId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                    {
                        MessageBox.Show(this, "项目 ID 无效，无法打开项目页。", "项目详情");
                        return;
                    }
                    string path = hit.Kind == "shader" ? "shader" :
                        hit.Kind == "resourcepack" ? "resourcepack" : isPack ? "modpack" : "mod";
                    try
                    {
                        Process.Start(new ProcessStartInfo(
                            "https://modrinth.com/" + path + "/" + Uri.EscapeDataString(hit.ProjectId))
                            { UseShellExecute = true });
                    }
                    catch (Exception ex) { ShowError("打开 Modrinth 项目页失败", ex); }
                }));
            project.Children.Add(actions);
            ShowDetails(hit.Name, Col(Card(project), overview));
        }

        async Task InstallResourceAsync(ResourceHit hit = null)
        {
            hit = hit ?? resourceList?.SelectedItem as ResourceHit;
            var profile = activeProfile;
            if (profile == null || hit == null) { MessageBox.Show("请先选择实例和资源。"); return; }
            if (hit.Kind == "mod" && (string.IsNullOrWhiteSpace(profile.Loader) ||
                string.Equals(profile.Loader, "vanilla", StringComparison.OrdinalIgnoreCase) || profile.Loader == "原版"))
            {
                Status("安装模组前，请选择已安装 Fabric、Forge、NeoForge 或 Quilt 的版本。");
                return;
            }
            if (hit.Source == "GitHub")
            {
                if (MessageBox.Show(this,
                    "GitHub 开源搜索仅检索具有公开 SPDX 许可证的项目。\n" +
                    "自动安装必须具有兼容版本 JAR、匹配的模组加载器元数据和有效的 SHA-256。\n" +
                    "GitHub 发布文件可能缺少必需依赖，安装后请阅读项目说明。\n\n" +
                    "是否继续尝试安装？",
                    "安装 GitHub 开源模组", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                downloads.Enqueue("GitHub 开源", hit.Name + " → " + profile.Name,
                    async (token, progress, bytes) =>
                    {
                        progress.Report("正在检查开源许可证、官方 Releases、版本与 SHA-256...");
                        using (var client = new GitHubOpenSourceModClient(settings))
                        {
                            string file = await client.DownloadAsync(hit, profile, token, bytes);
                            progress.Report("已验证 SHA-256 和加载器信息，安装：" + file +
                                "；GitHub 项目依赖需遵循作者说明。");
                            Status("已安装 GitHub 开源模组：" + hit.Name);
                        }
                    });
                Status("已加入 GitHub 开源模组安装任务：" + hit.Name);
                return;
            }
            downloads.Enqueue("Modrinth", hit.Name + " → " + profile.Name, async (token, progress, bytes) =>
            {
                progress.Report("正在查找兼容的资源版本和依赖文件...");
                using (var client = new ModrinthClient(settings))
                {
                    var file = hit.Kind == "mod"
                        ? await client.DownloadModWithDependenciesAsync(hit, profile, token, bytes, progress)
                        : await client.DownloadLatestAsync(hit, profile, token, bytes);
                    progress.Report("已安装到：" + file);
                    Status("资源下载完成：" + hit.Name);
                }
            });
            Status("已将资源加入任务列表：" + hit.Name);
            await Task.CompletedTask;
        }
        UIElement BuildCurseForge()
        {
            cfLoadedHits.Clear();
            if (!settings.McimEnabled)
                return Col(SearchCard(Col(
                        Txt("搜索 CurseForge 模组", 16, true),
                        Txt("启用 MCIM 社区镜像后，可搜索模组并查看项目图标和截图。", 12, false, true),
                        Row(Btn("启用 CurseForge 搜索", () =>
                        {
                            if (MessageBox.Show(this,
                                "MCIM 是第三方社区镜像，缓存资料可能滞后。\n" +
                                "安装时仍会校验版本、文件哈希及允许分发的链接。\n\n" +
                                "是否启用 CurseForge 搜索？",
                                "启用社区模组来源",
                                MessageBoxButton.YesNo, MessageBoxImage.Question)
                                != MessageBoxResult.Yes) return;
                            settings.McimEnabled = true;
                            try { store.Save(settings); }
                            catch (Exception ex)
                            {
                                settings.McimEnabled = false;
                                ShowError("保存社区资源设置失败", ex);
                                return;
                            }
                            Navigate("CurseForge");
                            Status("已启用 CurseForge 图文搜索。");
                        }, true), Btn("下载设置", () => Navigate("下载设置"))))));

            cfSearchQuery = new TextBox();
            ApplySearchInputStyle(cfSearchQuery, "输入模组名称、作者或关键词；留空浏览热门模组");
            cfSearchQuery.KeyDown += async (sender, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    args.Handled = true;
                    await SearchCurseForgeAsync();
                }
            };
            cfVersionFilter = new TextBox();
            ApplySearchInputStyle(cfVersionFilter, "在当前页筛选，例如输入 1.21.1");
            cfVersionFilter.TextChanged += (sender, args) => ApplyCurseForgeFilters();
            cfCategoryFilter = new ComboBox
            {
                ItemsSource = new[] { "全部分类" }, SelectedIndex = 0
            };
            ApplySearchInputStyle(cfCategoryFilter, "当前页已有项目的分类");
            cfCategoryFilter.SelectionChanged += (sender, args) => ApplyCurseForgeFilters();
            cfSortMode = new ComboBox
            {
                ItemsSource = new[] { "相关度", "最多下载", "按名称" },
                SelectedIndex = 0
            };
            ApplySearchInputStyle(cfSortMode, "排序只针对当前已加载的项目");
            cfSortMode.SelectionChanged += (sender, args) => ApplyCurseForgeFilters();

            cfSearchStatus = Txt("点击“搜索”查看项目。", 12, false, true);
            cfSearchResults = new ListBox
            {
                MinHeight = 132, MaxHeight = 372,
                ItemTemplate = (DataTemplate)FindResource("CurseForgeItemTemplate")
            };
            var search = Btn("搜索", async () => await SearchCurseForgeAsync(), true);
            search.MinWidth = 120;
            var reset = Btn("重置条件", () =>
            {
                cfSearchCancellation?.Cancel();
                cfSearchQuery.Clear();
                cfVersionFilter.Clear();
                cfLoadedHits.Clear();
                cfCategoryFilter.ItemsSource = new[] { "全部分类" };
                cfCategoryFilter.SelectedIndex = 0;
                cfSortMode.SelectedIndex = 0;
                ApplyCurseForgeFilters();
            });
            var details = Btn("查看详情与截图", ShowCurseForgeDetails);
            var install = Btn("安装到当前版本", async () => await InstallCurseForgeAsync(), true);
            WireResultActions(cfSearchResults, ShowCurseForgeDetails, details, install);
            return Col(
                SearchCard(Col(Txt("搜索 CurseForge 模组", 16, true),
                    SearchFields(("名称", cfSearchQuery), ("类型", cfCategoryFilter),
                        ("版本", cfVersionFilter), ("排序", cfSortMode)),
                    Txt("来源为 MCIM。版本、类型和排序仅筛选已加载的项目。", 12, false, true),
                    Row(search, reset))),
                ProjectResults(cfSearchStatus, cfSearchResults, Row(install, details)));
        }

        void ApplyCurseForgeFilters()
        {
            if (cfSearchResults == null || currentPage != "CurseForge") return;
            string version = (cfVersionFilter?.Text ?? "").Trim();
            string category = Convert.ToString(cfCategoryFilter?.SelectedItem) ?? "全部分类";
            IEnumerable<CurseForgeHit> filtered = cfLoadedHits;
            if (version.Length > 0)
                filtered = filtered.Where(hit =>
                    (hit.VersionsLabel ?? "").IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0);
            if (category != "全部分类")
                filtered = filtered.Where(hit =>
                    (hit.CategoryLabel ?? "").Split(new[] { " · " },
                        StringSplitOptions.RemoveEmptyEntries).Any(value =>
                            string.Equals(value, category, StringComparison.OrdinalIgnoreCase)));
            if (cfSortMode?.SelectedIndex == 1)
                filtered = filtered.OrderByDescending(hit => hit.DownloadCount);
            else if (cfSortMode?.SelectedIndex == 2)
                filtered = filtered.OrderBy(hit => hit.Name, StringComparer.CurrentCultureIgnoreCase);
            var visible = filtered.ToList();
            cfSearchResults.ItemsSource = visible;
            if (cfSearchStatus != null)
                cfSearchStatus.Text = cfLoadedHits.Count == 0 ?
                    "暂无项目。试试更换关键词。" :
                    visible.Count == 0 ? "已加载项目中没有匹配项。" :
                    "显示 " + visible.Count + " / " + cfLoadedHits.Count + " 个模组";
        }

        void ShowCurseForgeDetails()
        {
            var hit = cfSearchResults?.SelectedItem as CurseForgeHit;
            if (hit == null) return;
            var overview = Col(Txt(hit.Name, 20, true),
                Txt(hit.AuthorLabel + "  ·  " + hit.DownloadsLabel + "  ·  " + hit.CategoryLabel, 12, false, true),
                Txt(hit.Summary, 13),
                Txt(hit.VersionsLabel, 12, false, true));

            if (hit.IconUrl != null)
            {
                var cover = new Image { Width = 90, Height = 90,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 0, 12) };
                cover.SetBinding(Image.SourceProperty, new Binding("IconUrl")
                { Source = hit, Mode = BindingMode.OneWay });
                overview.Children.Insert(0, cover);
            }

            var gallery = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 14)
            };
            foreach (var shot in hit.Screenshots ?? Array.Empty<CurseForgeScreenshot>())
            {
                var tile = Col();
                tile.Width = 264;
                tile.Margin = new Thickness(0, 0, 12, 12);
                var picture = new Image
                {
                    Width = 250, Height = 154, Stretch = Stretch.UniformToFill,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    ToolTip = shot.Title
                };
                picture.SetBinding(Image.SourceProperty,
                    new Binding("ThumbnailUrl") { Source = shot, Mode = BindingMode.OneWay });
                var frame = new Border { Child = picture,
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
                frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrushTheme");
                tile.Children.Add(frame);
                if (!string.IsNullOrWhiteSpace(shot.Title))
                    tile.Children.Add(Txt(shot.Title, 11, false, true));
                gallery.Children.Add(tile);
            }
            var actions = Row(Btn("安装到当前版本", async () => await InstallCurseForgeAsync(hit), true));
            if (!string.IsNullOrEmpty(hit.OfficialUrl))
                actions.Children.Add(Btn("打开 CurseForge 项目页", () =>
                {
                    try { Process.Start(new ProcessStartInfo(hit.OfficialUrl) { UseShellExecute = true }); }
                    catch (Exception ex) { ShowError("打开 CurseForge 项目页失败", ex); }
                }));
            overview.Children.Add(actions);
            ShowDetails(hit.Name, Col(Card(overview),
                Card(Col(Txt("项目截图", 16, true), gallery.Children.Count == 0
                    ? (UIElement)Txt("作者暂未提供截图。", 12, false, true) : gallery))));
        }

        async Task SearchCurseForgeAsync()
        {
            var query = (cfSearchQuery?.Text ?? "").Trim();
            var results = cfSearchResults;
            var label = cfSearchStatus;
            if (results == null || !settings.McimEnabled) return;

            cfSearchCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            cfSearchCancellation = cancellation;
            if (label != null) label.Text = "正在加载模组封面、作者和截图信息...";
            try
            {
                Status("正在通过 MCIM 查询 CurseForge...");
                IReadOnlyList<CurseForgeHit> found;
                using (var client = new CurseForgeMirrorClient(settings))
                    found = await client.SearchAsync(query, cancellation.Token);
                if (cancellation.IsCancellationRequested ||
                    currentPage != "CurseForge" || results != cfSearchResults) return;
                cfLoadedHits.Clear();
                cfLoadedHits.AddRange(found);
                if (cfCategoryFilter != null)
                {
                    var categories = found.SelectMany(hit =>
                        (hit.CategoryLabel ?? "").Split(new[] { " · " },
                            StringSplitOptions.RemoveEmptyEntries))
                        .Distinct(StringComparer.CurrentCultureIgnoreCase)
                        .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase);
                    cfCategoryFilter.ItemsSource = new[] { "全部分类" }
                        .Concat(categories).ToArray();
                    cfCategoryFilter.SelectedIndex = 0;
                }
                ApplyCurseForgeFilters();
                Status("CurseForge 已加载 " + found.Count + " 个图文项目");
            }
            catch (OperationCanceledException) { /* Search or page switch cancelled. */ }
            catch (Exception ex)
            {
                if (!cancellation.IsCancellationRequested)
                {
                    if (label != null) label.Text = "搜索失败：可以稍后重试或检查 MCIM 设置。";
                    ShowError("CurseForge 查询失败", ex);
                }
            }
            finally
            {
                if (cfSearchCancellation == cancellation) cfSearchCancellation = null;
                cancellation.Dispose();
            }
        }

        async Task InstallCurseForgeAsync(CurseForgeHit hit = null)
        {
            hit = hit ?? cfSearchResults?.SelectedItem as CurseForgeHit;
            var profile = activeProfile;
            if (hit == null || profile == null) { MessageBox.Show("请先选择模组和游戏实例"); return; }
            if (MessageBox.Show("将为 " + profile.Name + " 下载 " + hit.Name +
                " 及必要依赖。仅从允许的镜像/官方 CDN 下载，不覆盖已有文件。\n\n继续？",
                "CurseForge 安装确认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            downloads.Enqueue("CurseForge", hit.Name + " → " + profile.Name, async (token, progress, bytes) =>
            {
                using (var client = new CurseForgeMirrorClient(settings))
                {
                    var files = await client.InstallAsync(hit.Id, profile, progress, token);
                    progress.Report("CurseForge 安装完成：" + files.Count + " 个文件");
                    Status("CurseForge 已安装：" + hit.Name);
                }
            });
            Status("已将 CurseForge 模组加入任务队列：" + hit.Name);
            await Task.CompletedTask;
        }

        UIElement BuildModpacks()
        {
            packPage = 0;
            packSearchQuery = new TextBox();
            packVersionFilter = new ComboBox
            {
                IsEditable = true,
                ItemsSource = (new[] { "全部版本", activeProfile?.BaseVersion, "1.21.1",
                    "1.20.1", "1.19.4", "1.18.2" })
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray(),
                SelectedIndex = 0
            };
            packSortMode = new ComboBox
            {
                ItemsSource = new[] { "相关度", "下载量", "最近更新", "最新发布" },
                SelectedIndex = 0
            };
            var source = new ComboBox { ItemsSource = new[] { "Modrinth" }, SelectedIndex = 0, IsEnabled = false };
            ApplySearchInputStyle(packSearchQuery, "输入名称或关键词，按 Enter 搜索");
            ApplySearchInputStyle(packVersionFilter, "可选择或输入 Minecraft 游戏版本");
            ApplySearchInputStyle(packSortMode, "设置结果排序");
            ApplySearchInputStyle(source, "Modrinth 整合包");
            async Task SearchFirstPage()
            {
                packPage = 0;
                await SearchModpacksAsync();
            }
            packSearchQuery.KeyDown += async (sender, args) =>
            {
                if (args.Key != System.Windows.Input.Key.Enter) return;
                args.Handled = true;
                await SearchFirstPage();
            };
            packSearchButton = Btn("搜索", async () => await SearchFirstPage(), true);
            packSearchButton.MinWidth = 120;
            var reset = Btn("重置条件", () =>
            {
                packSearchCancellation?.Cancel();
                packSearchCancellation = null;
                packSearchButton.IsEnabled = true;
                packSearchQuery.Clear();
                packVersionFilter.SelectedIndex = 0;
                packSortMode.SelectedIndex = 0;
                packPage = 0;
                packSearchResults.ItemsSource = null;
                packSearchStatus.Text = "点击“搜索”查看项目。";
                packPrevButton.IsEnabled = packNextButton.IsEnabled = false;
            });
            var root = Col(SearchCard(Col(Txt("搜索整合包", 16, true),
                SearchFields(("名称", packSearchQuery), ("来源", source),
                    ("版本", packVersionFilter), ("排序", packSortMode)),
                Row(packSearchButton, reset, Btn("导入已有整合包", ImportMrpackFromDisk)))));

            packSearchResults = new ListBox
            {
                MinHeight = 132, MaxHeight = 372,
                ItemTemplate = (DataTemplate)FindResource("ResourceItemTemplate")
            };
            packSearchStatus = Txt("点击“搜索”查看项目。", 12, false, true);
            packPrevButton = Btn("上一页", async () =>
            {
                if (packPage == 0) return;
                packPage--;
                await SearchModpacksAsync();
            });
            packNextButton = Btn("下一页", async () =>
            {
                packPage++;
                await SearchModpacksAsync();
            });
            packPrevButton.IsEnabled = packNextButton.IsEnabled = false;
            void Open() => ShowResourceDetails(packSearchResults.SelectedItem as ResourceHit);
            var details = Btn("查看详情", Open);
            var install = Btn("安装整合包", async () => await InstallSelectedModpackAsync(), true);
            WireResultActions(packSearchResults, Open, details, install);
            root.Children.Add(ProjectResults(packSearchStatus, packSearchResults, Row(install, details),
                packPrevButton, packNextButton));
            root.Children.Add(Card(Col(Txt("本地导入", 16, true),
                Txt("支持 .mrpack 文件，导入后会创建独立版本。", 12, false, true),
                Row(Btn("取消本地导入", () => currentOperation?.Cancel())))));
            return root;
        }

        async Task SearchModpacksAsync()
        {
            if (packSearchResults == null) return;
            packSearchCancellation?.Cancel();
            using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            {
                packSearchCancellation = request;
                if (packSearchButton != null) packSearchButton.IsEnabled = false;
                if (packSearchStatus != null) packSearchStatus.Text = "正在搜索 Modrinth 整合包...";
                try
                {
                    string version = packVersionFilter?.Text?.Trim();
                    if (string.IsNullOrWhiteSpace(version) || version == "全部版本")
                        version = null;
                    string sort = new[] { "relevance", "downloads", "updated", "newest" }[
                        Math.Max(0, Math.Min(3, packSortMode?.SelectedIndex ?? 0))];
                    using (var client = new ModrinthClient(settings))
                    {
                        var result = await client.SearchPageAsync(packSearchQuery.Text,
                            "modpack", request.Token, version, null, sort, packPage * 20, null);
                        if (request.IsCancellationRequested ||
                            !ReferenceEquals(packSearchCancellation, request) ||
                            currentPage != "整合包") return;
                        packSearchResults.ItemsSource = result.Hits;
                        int total = Math.Min(2000, result.TotalHits);
                        if (packSearchStatus != null)
                            packSearchStatus.Text = total == 0 ?
                                "没有找到符合条件的整合包。请减少筛选条件后重试。" :
                                "第 " + (packPage + 1) + " 页 · 显示 " +
                                result.Hits.Count + " 项 · 共 " + result.TotalHits + " 项";
                        packPrevButton.IsEnabled = packPage > 0;
                        packNextButton.IsEnabled = result.Hits.Count == 20 &&
                            (packPage + 1) * 20 < total;
                        Status("已加载第 " + (packPage + 1) + " 页整合包结果。");
                    }
                }
                catch (OperationCanceledException)
                {
                    if (ReferenceEquals(packSearchCancellation, request) &&
                        packSearchStatus != null)
                        packSearchStatus.Text = "搜索已取消或超时。可以重新搜索。";
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(packSearchCancellation, request)) return;
                    if (packSearchStatus != null)
                        packSearchStatus.Text = "获取整合包失败，请检查网络或调整筛选条件。";
                    ShowError("Modrinth 整合包搜索失败", ex);
                }
                finally
                {
                    if (ReferenceEquals(packSearchCancellation, request))
                    {
                        packSearchCancellation = null;
                        if (packSearchButton != null) packSearchButton.IsEnabled = true;
                    }
                }
            }
        }

        async Task InstallSelectedModpackAsync(ResourceHit hit = null)
        {
            hit = hit ?? packSearchResults?.SelectedItem as ResourceHit;
            if (hit == null) { MessageBox.Show("请先选择一个整合包"); return; }
            downloads.Enqueue("整合包", hit.Name, async (token, progress, bytes) =>
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlockCraftLauncher", "wpf-pack-cache");
                Directory.CreateDirectory(folder);
                string archive = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mrpack");
                try
                {
                    progress.Report("正在下载整合包：" + hit.Name);
                    using (var client = new ModrinthClient(settings))
                        await client.DownloadModpackArchiveAsync(hit, archive, token, bytes);
                    progress.Report("正在安装 Minecraft 与整合包文件，请勿关闭启动器...");
                    var profile = await new MrpackInstaller(settings).InstallAsync(archive, progress, token);
                    token.ThrowIfCancellationRequested();
                    settings.Profiles.Add(profile);
                    try { store.Save(settings); }
                    catch { settings.Profiles.Remove(profile); throw; }
                    UseInstalledProfile(profile);
                    progress.Report("已创建独立游戏实例：" + profile.Name);
                    Status("整合包安装完成：" + profile.Name);
                }
                finally
                {
                    if (File.Exists(archive)) File.Delete(archive);
                }
            });
            Status("已将整合包加入任务队列：" + hit.Name);
            await Task.CompletedTask;
        }

        void ImportMrpackFromDisk()
        {
            var dialog = new OpenFileDialog { Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack" };
            if (dialog.ShowDialog(this) != true) return;
            if (currentOperation != null) { MessageBox.Show("已有安装任务正在进行"); return; }
            var installer = new MrpackInstaller(settings);
            try
            {
                var info = installer.Preview(dialog.FileName);
                if (MessageBox.Show("整合包：" + info.Name + "\nMinecraft：" + info.Minecraft +
                    "\n加载器：" + info.Loader + " " + info.LoaderVersion +
                    "\n下载文件数：" + info.FileCount + "\n预计下载："
                    + (info.DownloadBytes / 1024 / 1024) + " MiB\n\n确认安装到新实例？",
                    "整合包安全检查", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }
            catch (Exception ex) { ShowError("整合包安全检查未通过", ex); return; }
            currentOperation = new CancellationTokenSource();
            _ = InstallLocalMrpackAndResetAsync(dialog.FileName);
        }
        async Task InstallLocalMrpackAndResetAsync(string archive)
        {
            try { await InstallMrpackAsync(archive, currentOperation.Token); }
            catch (OperationCanceledException) { Status("整合包操作已取消"); }
            catch (Exception ex) { ShowError("整合包安装失败", ex); }
            finally { currentOperation.Dispose(); currentOperation = null; }
        }
        async Task InstallMrpackAsync(string archive, CancellationToken ct)
        {
            var installer = new MrpackInstaller(settings);
            var progress = new Progress<string>(Status);
            var profile = await installer.InstallAsync(archive, progress, ct);
            settings.Profiles.Add(profile);
            try { store.Save(settings); }
            catch { settings.Profiles.Remove(profile); throw; }
            activeProfile = profile;
            Navigate("首页");
            Status("已创建整合包实例：" + profile.Name);
        }


        UIElement BuildModUpdates()
        {
            if (activeProfile == null) return NoVersionSelected("模组与资源");
            localResourceKind = new ComboBox
            {
                ItemsSource = new[] { "模组", "光影", "资源包" }, SelectedIndex = 0, MaxWidth = 160
            };
            localResourceList = new ListBox
            {
                MinHeight = 140, MaxHeight = 310,
                ItemTemplate = (DataTemplate)FindResource("LocalResourceItemTemplate")
            };
            localResourceKind.SelectionChanged += async (sender, args) => await RefreshLocalResourceListAsync();
            var local = Card(Col(Txt("已安装资源", 16, true),
                SearchFields(("类型", localResourceKind)), localResourceList,
                Row(Btn("导入文件", ImportLocalResource, true),
                    Btn("启用", () => ToggleLocalResource(true)),
                    Btn("停用", () => ToggleLocalResource(false)),
                    Btn("刷新列表", async () => await RefreshLocalResourceListAsync()),
                    Btn("打开游戏目录", OpenGameFolder))));
            modUpgradeResults = new ListBox { MinHeight = 92, MaxHeight = 212 };
            var updates = Card(Col(Txt("Modrinth 更新", 16, true),
                Txt("检查通过启动器安装的资源。更新前会备份旧文件。", 12, false, true),
                modUpgradeResults,
                Row(Btn("检查更新", async () => await CheckManagedUpdatesAsync(), true),
                    Btn("更新选中", async () => await ApplyManagedUpdatesAsync(false)),
                    Btn("更新全部", async () => await ApplyManagedUpdatesAsync(true)))));
            _ = RefreshLocalResourceListAsync();
            return Col(PageHeading("模组与资源", "当前版本  " + activeProfile.Name), local, updates);
        }
        async Task CheckManagedUpdatesAsync()
        {
            var profile = activeProfile;
            var results = modUpgradeResults;
            if (profile == null || results == null) return;
            if (currentOperation != null) { MessageBox.Show("已有安装操作进行中"); return; }
            currentOperation = new CancellationTokenSource();
            try
            {
                Status("正在检查已登记的 Modrinth 资源更新...");
                var updates = await new ManagedModUpdateService(settings).CheckAsync(profile, currentOperation.Token);
                results.ItemsSource = updates;
                Status("发现兼容更新：" + updates.Count + " 项");
            }
            catch (OperationCanceledException) { Status("更新检查已取消"); }
            catch (Exception ex) { ShowError("Modrinth 更新检查失败", ex); }
            finally { currentOperation.Dispose(); currentOperation = null; }
        }
        async Task ApplyManagedUpdatesAsync(bool all)
        {
            var profile = activeProfile;
            var results = modUpgradeResults;
            if (profile == null || results == null) return;
            if (currentOperation != null) { MessageBox.Show("已有安装操作进行中"); return; }
            var updates = all
                ? results.Items.OfType<ResourceUpgrade>().ToList()
                : new[] { results.SelectedItem as ResourceUpgrade }.Where(x => x != null).ToList();
            if (updates.Count == 0) { MessageBox.Show("没有选择可用更新"); return; }
            if (MessageBox.Show("将更新 " + updates.Count + " 个已登记资源，更新前自动备份旧文件。确认？",
                    "模组更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            currentOperation = new CancellationTokenSource();
            try
            {
                var manager = new ManagedModUpdateService(settings);
                foreach (var item in updates)
                {
                    Status("正在更新：" + item.Old.Title);
                    await manager.ApplyAsync(profile, item, currentOperation.Token);
                }
                Status("Modrinth 资源更新完成：" + updates.Count + " 项");
                results.ItemsSource = null;
                if (currentPage == "模组更新" && ReferenceEquals(profile, activeProfile))
                    _ = RefreshLocalResourceListAsync();
            }
            catch (OperationCanceledException) { Status("模组更新已取消"); }
            catch (Exception ex) { ShowError("资源更新失败", ex); }
            finally { currentOperation.Dispose(); currentOperation = null; }
        }
        async Task RefreshLocalResourceListAsync()
        {
            localModIconLoad?.Cancel();
            if (localResourceList == null || localResourceKind == null || activeProfile == null) return;
            string kind = Convert.ToString(localResourceKind.SelectedItem);
            IReadOnlyList<LocalResourceItem> items;
            try
            {
                items = InstanceResourceService.List(activeProfile, kind);
                localResourceList.ItemsSource = items;
            }
            catch (Exception ex)
            {
                ShowError("读取本地资源失败", ex);
                return;
            }
            if (kind != "模组" || items.Count == 0) return;

            var load = new CancellationTokenSource();
            localModIconLoad = load;
            try
            {
                // A large modpack may include hundreds of JARs. Extract at
                // most four thumbnails in parallel while updating each card
                // on the captured WPF Dispatcher as its icon becomes ready.
                using (var slots = new SemaphoreSlim(4, 4))
                {
                    var workers = items.Select(async item =>
                    {
                        await slots.WaitAsync(load.Token);
                        try
                        {
                            var icon = await Task.Run(
                                () => LocalModIcon.Read(item.FullPath), load.Token);
                            load.Token.ThrowIfCancellationRequested();
                            item.Icon = icon;
                        }
                        finally { slots.Release(); }
                    }).ToArray();
                    await Task.WhenAll(workers);
                }
            }
            catch (OperationCanceledException) when (load.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(localModIconLoad, load)) localModIconLoad = null;
                load.Dispose();
            }
        }
        void ImportLocalResource()
        {
            if (activeProfile == null || localResourceKind == null) return;
            string kind = Convert.ToString(localResourceKind.SelectedItem);
            var dialog = new OpenFileDialog
            {
                Filter = kind == "模组" ? "Minecraft 模组 (*.jar)|*.jar" : "Minecraft 资源 (*.zip)|*.zip",
                Title = "选择要导入到当前实例的文件"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                string added = InstanceResourceService.Import(activeProfile, kind, dialog.FileName);
                Status("导入完成：" + Path.GetFileName(added));
                _ = RefreshLocalResourceListAsync();
            }
            catch (Exception ex) { ShowError("导入资源失败", ex); }
        }
        void ToggleLocalResource(bool enabled)
        {
            var item = localResourceList?.SelectedItem as LocalResourceItem;
            if (activeProfile == null || item == null) { MessageBox.Show("请先选择要操作的本地文件"); return; }
            try
            {
                InstanceResourceService.SetEnabled(activeProfile, Convert.ToString(localResourceKind.SelectedItem), item, enabled);
                Status((enabled ? "已启用：" : "已停用：") + item.Name);
                _ = RefreshLocalResourceListAsync();
            }
            catch (Exception ex) { ShowError("操作本地资源失败", ex); }
        }

        UIElement BuildMultiplayer()
        {
            serverList = new ListBox
            {
                MinHeight = 126, MaxHeight = 245,
                ItemTemplate = (DataTemplate)FindResource("ServerItemTemplate")
            };
            serverName = new TextBox { ToolTip = "服务器在收藏列表中的名称" };
            serverAddress = new TextBox { ToolTip = "输入域名或 IP，可附加端口" };
            var saved = Card(Col(Txt("服务器收藏", 16, true), serverList,
                Row(Btn("新增服务器", () =>
                    {
                        serverList.UnselectAll();
                        serverName.Clear();
                        serverAddress.Clear();
                        serverName.Focus();
                    }),
                    Btn("复制地址", CopyServerAddress), Btn("删除收藏", DeleteServer))));
            var form = Card(Col(Txt("服务器信息", 16, true),
                SearchFields(("名称", serverName), ("地址", serverAddress)),
                Row(Btn("加入服务器", async () => await JoinServerAsync(), true),
                    Btn("查找服务器", () => Navigate("查找服务器")),
                    Btn("保存收藏", SaveServer),
                    Btn("检测连接", async () => await CheckServerAsync()),
                    Btn("启动当前版本", async () => await LaunchAsync()))));
            serverList.SelectionChanged += (sender, args) =>
            {
                if (serverList.SelectedItem is ServerEntry selected)
                {
                    serverName.Text = selected.Name;
                    serverAddress.Text = selected.Address;
                }
            };
            RefreshServers();
            return Col(PageHeading("服务器", "使用当前 Minecraft Java 版本加入服务器。"),
                saved, form);
        }

        UIElement BuildServerFinder()
        {
            serverFinderPage = 1;
            serverFinderPages = 1;
            serverFinderQuery = new TextBox { MaxLength = 80 };
            serverFinderVersion = new TextBox { MaxLength = 18 };
            serverFinderProvider = new ComboBox
            {
                ItemsSource = new[] { "全部来源", "MC Monitor",
                    "minecraftserve.rs", "MSID" },
                SelectedIndex = 0
            };
            serverFinderSort = new ComboBox
            {
                ItemsSource = new[] { "综合推荐", "在线人数", "最新收录" },
                SelectedIndex = 0
            };
            serverFinderOnlineOnly = new CheckBox
            {
                Content = "仅显示目录标记为在线的 Java 服务器",
                IsChecked = true,
                Margin = new Thickness(0, 4, 0, 8)
            };
            ApplySearchInputStyle(serverFinderQuery, "名称、IP 或描述关键词（2～80 字），留空浏览服务器");
            ApplySearchInputStyle(serverFinderVersion, "Minecraft Java 版本，例如 1.21、1.20.1；留空不限");
            ApplySearchInputStyle(serverFinderProvider, "全部来源 / MC Monitor / minecraftserve.rs / MSID");
            ApplySearchInputStyle(serverFinderSort, "按综合推荐、玩家人数或新收录排序");
            serverFinderQuery.KeyDown += async (sender, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter)
                {
                    e.Handled = true;
                    await SearchServersAsync(1);
                }
            };
            serverFinderSearchButton = Btn("搜索服务器", async () =>
                await SearchServersAsync(1), true);
            var form = SearchCard(Col(
                Txt("服务器查找", 16, true),
                Txt("通过公开服务器目录查找 Minecraft Java 联机服务器，支持版本、来源、人数和状态筛选。", 12, false, true),
                SearchFields(("名称 / IP", serverFinderQuery), ("来源", serverFinderProvider),
                    ("游戏版本", serverFinderVersion), ("排序", serverFinderSort)),
                serverFinderOnlineOnly,
                Row(serverFinderSearchButton, Btn("清除筛选", () =>
                {
                    serverFinderQuery.Clear();
                    serverFinderVersion.Clear();
                    serverFinderProvider.SelectedIndex = 0;
                    serverFinderSort.SelectedIndex = 0;
                    serverFinderOnlineOnly.IsChecked = true;
                    serverFinderStatus.Text = "已清除筛选，点击“搜索服务器”浏览公开目录。";
                }), Btn("我的服务器收藏", () => Navigate("多人联机"))),
                Txt("目录信息可能延迟；服务器由第三方运营，不代表启动器官方推荐。仅列出 Java 或可使用 Java 加入的公开服务器。",
                    12, false, true)));
            serverFinderResults = new ListBox
            {
                MinHeight = 290, MaxHeight = 490,
                ItemTemplate = (DataTemplate)FindResource("PublicServerResultTemplate")
            };
            serverFinderStatus = Txt("输入关键词并点击搜索，或留空浏览。不会自动联网。",
                12, false, true);
            serverFinderDetails = Txt("请选择一个结果查看地址、版本、玩家人数和来源。",
                12, false, true);
            serverFinderDetails.TextWrapping = TextWrapping.Wrap;
            serverFinderResults.SelectionChanged += (sender, args) =>
                RefreshServerFinderSelection();
            serverFinderPrevButton = Btn("上一页", async () =>
                await SearchServersAsync(serverFinderPage - 1));
            serverFinderNextButton = Btn("下一页", async () =>
                await SearchServersAsync(serverFinderPage + 1));
            serverFinderPrevButton.IsEnabled = false;
            serverFinderNextButton.IsEnabled = false;
            serverFinderFavoriteButton = Btn("加入收藏", SaveDiscoveredServer, true);
            serverFinderJoinButton = Btn("直接连接", async () =>
                await JoinDiscoveredServerAsync());
            serverFinderCopyButton = Btn("复制服务器地址", CopyDiscoveredServer);
            serverFinderSourceButton = Btn("查看来源网站", OpenDiscoveredSource);
            serverFinderFavoriteButton.IsEnabled = serverFinderJoinButton.IsEnabled =
                serverFinderCopyButton.IsEnabled = serverFinderSourceButton.IsEnabled = false;

            var results = ResultsCard(Col(Txt("发现的服务器", 16, true),
                serverFinderStatus, serverFinderResults,
                Row(serverFinderPrevButton, serverFinderNextButton)));
            var actions = Card(Col(Txt("服务器详情", 16, true), serverFinderDetails,
                Row(serverFinderFavoriteButton, serverFinderJoinButton,
                    serverFinderCopyButton, serverFinderSourceButton),
                Txt("直接连接需要已安装 Minecraft Java 实例。目录记录不保证实时可连接，版本与账号权限以目标服务器为准。",
                    12, false, true)));
            return Col(PageHeading("查找服务器",
                "搜索公开 Minecraft Java 服务器，收藏喜欢的服务器或使用当前实例加入。"),
                form, results, actions,
                Card(Col(Txt("数据来源", 14, true),
                    Txt("服务器资料来自 MC Monitor、minecraftserve.rs、minecraftserver.id（MSID）。不发送微软登录令牌。数据缓存至少 60 秒，失败可切换来源，内容以第三方为准。",
                        12, false, true))));
        }

        void RefreshServerFinderSelection()
        {
            var hit = serverFinderResults?.SelectedItem as PublicServerHit;
            bool available = hit != null;
            if (serverFinderFavoriteButton != null)
                serverFinderFavoriteButton.IsEnabled = available;
            if (serverFinderJoinButton != null)
                serverFinderJoinButton.IsEnabled = available;
            if (serverFinderCopyButton != null)
                serverFinderCopyButton.IsEnabled = available;
            if (serverFinderSourceButton != null)
                serverFinderSourceButton.IsEnabled = available;
            if (serverFinderDetails == null) return;
            serverFinderDetails.Text = hit == null ?
                "请选择一个结果查看服务器详情。" :
                hit.Name + "\n地址：" + hit.Address +
                "\n" + hit.StatusLabel + " · " + hit.PlayerLabel +
                "\n" + hit.VersionLabel +
                "\n分类：" + (string.IsNullOrWhiteSpace(hit.Tags) ? "未标注" : hit.Tags) +
                "\n介绍：" + hit.Summary +
                "\n来源：" + hit.Source +
                "\n状态是目录缓存，不代表刚刚完成实时检测。";
        }

        async Task SearchServersAsync(int page)
        {
            if (serverFinderSearching || page < 1 || page > 200) return;
            string query = serverFinderQuery.Text.Trim();
            string version = serverFinderVersion.Text.Trim();
            string provider = Convert.ToString(serverFinderProvider.SelectedItem);
            string sort = serverFinderSort.SelectedIndex == 1 ? "players" :
                serverFinderSort.SelectedIndex == 2 ? "newest" : "score";
            bool onlineOnly = serverFinderOnlineOnly.IsChecked == true;
            serverFinderCancellation?.Cancel();
            var cancel = new CancellationTokenSource();
            serverFinderCancellation = cancel;
            serverFinderSearching = true;
            serverFinderSearchButton.IsEnabled = false;
            serverFinderPrevButton.IsEnabled = serverFinderNextButton.IsEnabled = false;
            serverFinderStatus.Text = "正在从公开服务器目录加载信息...";
            try
            {
                var found = await PublicServerDirectoryService.SearchAsync(
                    provider, query, version, sort, onlineOnly, page, cancel.Token);
                if (cancel.IsCancellationRequested || currentPage != "查找服务器")
                    return;
                serverFinderPage = page;
                serverFinderPages = found.Pages;
                serverFinderResults.ItemsSource = found.Servers;
                serverFinderResults.SelectedIndex = found.Servers.Count > 0 ? 0 : -1;
                serverFinderStatus.Text = "第 " + page + " / " + found.Pages +
                    " 页 · 本页 " + found.Servers.Count + " 个服务器" +
                    (provider != "全部来源" ? " · 目录记录 " + found.Total : "") +
                    (found.Warning.Length > 0 ? "\n部分来源失败：" + found.Warning : "") +
                    "\n数据为目录缓存，搜索结果不代表实时可连接。";
                serverFinderPrevButton.IsEnabled = page > 1;
                serverFinderNextButton.IsEnabled = page < found.Pages;
                Status("服务器查找完成：" + found.Servers.Count + " 个结果");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!cancel.IsCancellationRequested && currentPage == "查找服务器")
                    serverFinderStatus.Text = "查找服务器失败：" +
                        ex.GetBaseException().Message +
                        "。请检查网络、缩短关键词、切换来源后再试。";
            }
            finally
            {
                if (ReferenceEquals(serverFinderCancellation, cancel))
                {
                    serverFinderSearching = false;
                    serverFinderSearchButton.IsEnabled = true;
                }
                cancel.Dispose();
            }
        }

        void SaveDiscoveredServer()
        {
            var hit = serverFinderResults?.SelectedItem as PublicServerHit;
            if (hit == null) return;
            try
            {
                MultiplayerService.ParseAddress(hit.Address);
                var saved = multiplayer.Load();
                var existing = saved.FirstOrDefault(x =>
                    x.Address.Equals(hit.Address, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    saved.Add(new ServerEntry { Name = hit.Name, Address = hit.Address });
                else
                    existing.Name = hit.Name;
                multiplayer.Save(saved);
                Status("服务器已加入收藏：" + hit.Name);
                serverFinderStatus.Text = "已收藏：" + hit.Name +
                    "。可在「联机 → 服务器收藏」管理和连接。";
            }
            catch (Exception ex) { ShowError("收藏服务器失败", ex); }
        }

        void CopyDiscoveredServer()
        {
            var hit = serverFinderResults?.SelectedItem as PublicServerHit;
            if (hit == null) return;
            try
            {
                MultiplayerService.ParseAddress(hit.Address);
                Clipboard.SetText(hit.Address);
                Status("已复制服务器地址：" + hit.Address);
            }
            catch (Exception ex) { ShowError("复制服务器地址失败", ex); }
        }

        void OpenDiscoveredSource()
        {
            var hit = serverFinderResults?.SelectedItem as PublicServerHit;
            if (hit == null || !Uri.TryCreate(hit.SourceUrl, UriKind.Absolute, out Uri uri) ||
                uri.Scheme != "https" || !uri.IsDefaultPort ||
                !(uri.Host == "www.mc-monitor.app" || uri.Host == "minecraftserve.rs" ||
                    uri.Host == "minecraftserver.id"))
                return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("打开服务器目录网站失败", ex); }
        }

        async Task JoinDiscoveredServerAsync()
        {
            var hit = serverFinderResults?.SelectedItem as PublicServerHit;
            if (hit == null) return;
            if (activeProfile == null)
            {
                MessageBox.Show("请先安装并选择 Minecraft Java 游戏实例。");
                return;
            }
            try
            {
                MultiplayerService.ParseAddress(hit.Address);
                string installed = activeProfile.BaseVersion ?? "";
                bool possiblyIncompatible = hit.Version.Length > 0 && installed.Length > 0 &&
                    hit.Version.IndexOf(installed, StringComparison.OrdinalIgnoreCase) < 0;
                if (possiblyIncompatible || hit.Online != true)
                {
                    if (MessageBox.Show(
                        "服务器：" + hit.Name + "\n地址：" + hit.Address +
                        "\n目录版本：" + hit.Version +
                        "\n当前游戏版本：" + installed + "\n" +
                        (hit.Online != true ? "目录中并未确认在线。\n" : "") +
                        "目录信息可能滞后，且可能需要正版账号或白名单。" +
                        "\n\n仍要尝试启动并连接吗？",
                        "确认服务器连接", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return;
                }
                await LaunchAsync(hit.Address);
            }
            catch (Exception ex) { ShowError("加入发现的服务器失败", ex); }
        }

        UIElement BuildLanWorlds()
        {
            lanWorldList = new ListBox
            {
                MinHeight = 150, MaxHeight = 300,
                ItemTemplate = (DataTemplate)FindResource("ServerItemTemplate")
            };
            return Col(PageHeading("局域网", "查找同一网络中已开放的 Minecraft Java 世界。"),
                Card(Col(Txt("可加入的世界", 16, true), lanWorldList,
                    Row(Btn("查找局域网世界", async () => await ScanLanWorldsAsync(), true),
                        Btn("加入选中世界", async () => await JoinLanWorldAsync())))));
        }

        void RefreshServers()
        {
            if (serverList == null) return;
            try { serverList.ItemsSource = multiplayer.Load(); }
            catch (Exception ex) { ShowError("读取服务器收藏失败", ex); }
        }
        void SaveServer()
        {
            if (serverName == null || serverAddress == null) return;
            try
            {
                var list = multiplayer.Load();
                var name = (serverName.Text ?? "").Trim();
                var address = (serverAddress.Text ?? "").Trim();
                var selected = serverList?.SelectedItem as ServerEntry;
                var item = new ServerEntry { Name = name, Address = address };
                if (selected != null)
                {
                    int i = list.FindIndex(x => x.Name == selected.Name && x.Address == selected.Address);
                    if (i >= 0) list[i] = item;
                    else list.Add(item);
                }
                else list.Add(item);
                multiplayer.Save(list); RefreshServers(); Status("已保存服务器：" + name);
            }
            catch (Exception ex) { ShowError("保存服务器失败", ex); }
        }
        void DeleteServer()
        {
            var selected = serverList?.SelectedItem as ServerEntry;
            if (selected == null) { MessageBox.Show("请先选择服务器"); return; }
            if (MessageBox.Show("删除收藏的 " + selected.Name + "？", "确认删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                var list = multiplayer.Load();
                list.RemoveAll(x => x.Name == selected.Name && x.Address == selected.Address);
                multiplayer.Save(list); RefreshServers(); Status("已删除服务器：" + selected.Name);
            }
            catch (Exception ex) { ShowError("删除服务器失败", ex); }
        }
        void CopyServerAddress()
        {
            string address = serverAddress?.Text;
            try
            {
                MultiplayerService.ParseAddress(address);
                Clipboard.SetText(address.Trim());
                Status("服务器地址已复制，请在 Minecraft 多人游戏中添加或连接。");
            }
            catch (Exception ex) { ShowError("复制服务器地址失败", ex); }
        }
        async Task ScanLanWorldsAsync()
        {
            var list = lanWorldList;
            try
            {
                Status("正在扫描 Minecraft 局域网公告...");
                var worlds = await multiplayer.ScanLanAsync(CancellationToken.None);
                if (list != null) list.ItemsSource = worlds;
                Status("找到 " + worlds.Count + " 个局域网世界。");
            }
            catch (Exception ex) { ShowError("局域网扫描失败", ex); }
        }
        async Task JoinLanWorldAsync()
        {
            var selected = lanWorldList?.SelectedItem as ServerEntry;
            if (selected == null) { MessageBox.Show("请先选择局域网世界"); return; }
            if (activeProfile == null) { MessageBox.Show("请先选择游戏实例"); return; }
            try { await LaunchAsync(selected.Address); }
            catch (Exception ex) { ShowError("连接局域网失败", ex); }
        }
        async Task JoinServerAsync()
        {
            try
            {
                string address = serverAddress?.Text;
                MultiplayerService.ParseAddress(address);
                if (activeProfile == null) { MessageBox.Show("请先选择已安装的 Minecraft 实例"); return; }
                await LaunchAsync(address);
            }
            catch (Exception ex) { ShowError("连接服务器失败", ex); }
        }
        async Task CheckServerAsync()
        {
            string address = serverAddress?.Text;
            try
            {
                Status("正在检测服务器连通性：" + address);
                string result = await multiplayer.CheckReachabilityAsync(address, CancellationToken.None);
                Status(result); MessageBox.Show(result, "服务器连通性", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { ShowError("服务器检测失败", ex); }
        }
        UIElement BuildLauncherUpdates()
        {
            var root = Col(PageHeading("启动器更新", "从 BlockCraft Launcher 的 GitHub 发布页获取新版。"));
            string package = LauncherAutoUpdateService.InstallMode == "onefile" ? "单文件 EXE" : "完整 ZIP";
            root.Children.Add(Card(Col(Txt("当前版本", 16, true),
                Txt(LauncherAutoUpdateService.CurrentTag + "  ·  " + package + "  ·  Windows x64",
                    13, false, true))));

            updateStatusText = Txt("", 14, true);
            updateProgressBar = new ProgressBar
            {
                Height = 9, Minimum = 0, Maximum = 100,
                Margin = new Thickness(0, 6, 0, 13),
                Visibility = Visibility.Collapsed
            };
            updateProgressBar.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
            updateNotesText = Txt("", 12, false, true);
            var notes = new ScrollViewer
            {
                Content = updateNotesText, MaxHeight = 198,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            updateCheckButton = Btn("检查更新", async () =>
                await CheckLauncherUpdatesAsync(true), true);
            updateInstallButton = Btn("更新并替换旧版", async () =>
                await InstallLauncherUpdateAsync(), true);
            updateDirectOpenButton = Btn("临时试用（不替换）", async () =>
                await OpenDownloadedUpdateDirectlyAsync());
            root.Children.Add(Card(Col(Txt("更新状态", 16, true),
                updateStatusText, updateProgressBar,
                Row(updateCheckButton, updateInstallButton, updateDirectOpenButton),
                notes,
                Txt("安装会在原位置替换旧启动器 EXE，原路径、文件名及快捷方式保持不变；" +
                    "成功后清理临时包和旧备份。“临时试用”不会替换旧 EXE。",
                    12, false, true))));
            root.Children.Add(Card(Col(Txt("更新选项", 16, true),
                Row(Btn("更新设置", () => Navigate("更新设置")),
                    Btn("查看发布记录", () => LauncherReleaseService.OpenOfficialRelease(
                        "https://github.com/minecraftlu/BlockCraftLauncher/releases"))))));
            RefreshLauncherUpdatePanel();
            return root;
        }

        void RefreshLauncherUpdatePanel()
        {
            bool available = offeredUpdate != null;
            if (homeUpdateCard != null)
            {
                homeUpdateCard.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
                if (homeUpdateText != null && available)
                    homeUpdateText.Text = "可用：" + offeredUpdate.Tag +
                        "  ·  " + (settings.UpdateChannel == "preview" ? "预览版" : "稳定版");
            }
            if (currentPage == "启动器更新" && updateStatusText != null)
            {
                updateStatusText.Text = updatePreparing ?
                    "正在下载并校验更新..." :
                    updateChecking ? "正在连接 GitHub 检查更新..." :
                    preparedUpdate != null ?
                        (settings.AutoInstallUpdatesOnExit ?
                            "已校验 " + preparedUpdate.Offer.Tag +
                            "，下次正常关闭启动器时将自动替换旧 EXE。" :
                            "更新已下载并通过启动校验，准备安装：" + preparedUpdate.Offer.Tag) :
                    available ? "发现可用更新：" + offeredUpdate.Tag :
                        updateCheckFailed ? "暂时无法连接 GitHub。可检测线路后重试。" :
                        "当前通道没有已确认的更高版本。";

                updateNotesText.Text = available ?
                    "通道：" + (settings.UpdateChannel == "preview" ? "预览版" : "正式稳定版") +
                    "  ·  来源：GitHub Releases\n" +
                    (offeredUpdate.Description ?? "") :
                    "当前通道：" + (settings.UpdateChannel == "preview" ?
                        "预览版" : "稳定版") + "。";
                updateInstallButton.IsEnabled = available && !updatePreparing && !updateChecking;
                updateDirectOpenButton.IsEnabled = available && !updatePreparing && !updateChecking;
                updateCheckButton.IsEnabled = !updateChecking && !updatePreparing;
            }

            if (sidebarButtons.TryGetValue("启动器更新", out var nav) &&
                nav.Content is StackPanel panel && panel.Children.Count >= 2 &&
                panel.Children[1] is TextBlock label)
            {
                label.Text = available ? "启动器更新 · 新" : "启动器更新";
                nav.ToolTip = available ? "有新版 " + offeredUpdate.Tag + " 可用" : "检查启动器更新";
            }
        }

        async Task CheckLauncherUpdatesAsync(bool manual)
        {
            if (updateChecking || updatePreparing || updateCancellation.IsCancellationRequested)
                return;
            if (!manual)
            {
                if (!settings.AutoCheckUpdates) return;
                if (settings.LastUpdateCheckUtc <= DateTime.UtcNow &&
                    DateTime.UtcNow - settings.LastUpdateCheckUtc < TimeSpan.FromHours(6))
                    return;
            }
            updateChecking = true;
            updateCheckFailed = false;
            string channel = settings.UpdateChannel;
            RefreshLauncherUpdatePanel();
            try
            {
                var candidate = await autoUpdater.CheckAsync(updateCancellation.Token);
                if (updateCancellation.IsCancellationRequested || settings.UpdateChannel != channel) return;
                if (offeredUpdate?.Tag != candidate?.Tag ||
                    offeredUpdate?.Mode != candidate?.Mode)
                    preparedUpdate = null;
                offeredUpdate = candidate;
                settings.LastUpdateCheckUtc = DateTime.UtcNow;
                try { store.Save(settings); }
                catch (Exception saveError)
                { Log("更新检查时间保存失败：" + saveError.GetType().Name); }
                if (candidate == null)
                {
                    if (manual) Status("当前" +
                        (settings.UpdateChannel == "preview" ? "预览" : "稳定") +
                        "通道没有可用的更高版本。");
                }
                else
                {
                    Status("发现 BlockCraft Launcher 新版本：" + candidate.Tag +
                        "（进入“启动器更新”即可下载安装）");
                }
            }
            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                updateCheckFailed = true;
                if (manual) ShowError("检查更新失败", ex);
                else Log("后台检查更新暂不可用：" + ex.GetType().Name);
            }
            finally
            {
                updateChecking = false;
                RefreshLauncherUpdatePanel();
            }
            if (offeredUpdate != null && settings.AutoDownloadUpdates &&
                preparedUpdate == null && !updateCancellation.IsCancellationRequested)
                await PrepareLauncherUpdateAsync(false);
        }

        async Task PrepareLauncherUpdateAsync(bool interactive)
        {
            if (updatePreparing || offeredUpdate == null ||
                updateCancellation.IsCancellationRequested) return;
            updatePreparing = true;
            if (updateProgressBar != null)
            {
                updateProgressBar.Visibility = Visibility.Visible;
                updateProgressBar.IsIndeterminate = true;
                updateProgressBar.Value = 0;
            }
            RefreshLauncherUpdatePanel();
            try
            {
                var selected = offeredUpdate;
                var progress = new Progress<LauncherUpdateProgress>(item =>
                {
                    if (updateCancellation.IsCancellationRequested) return;
                    string status = item.Message ?? "正在准备更新";
                    if (item.Total > 0)
                        status += " · " + (item.Completed * 100.0 / item.Total).ToString("0") + "%";
                    if (updateStatusText != null && currentPage == "启动器更新")
                        updateStatusText.Text = status;
                    if (updateProgressBar != null && currentPage == "启动器更新")
                    {
                        updateProgressBar.IsIndeterminate = item.Total <= 0;
                        if (item.Total > 0)
                            updateProgressBar.Value = Math.Min(100d,
                                item.Completed * 100d / item.Total);
                    }
                    Status(status);
                });
                var stage = await autoUpdater.PrepareAsync(
                    selected, updateCancellation.Token, progress);
                if (updateCancellation.IsCancellationRequested || offeredUpdate != selected)
                    return;
                preparedUpdate = stage;
                Status("更新 " + selected.Tag + " 已下载并通过完整性与启动自检；等待安装确认。");
            }
            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (interactive) ShowError("下载或校验更新失败", ex);
                else Log("后台预下载失败：" + ex.GetType().Name);
            }
            finally
            {
                updatePreparing = false;
                if (updateProgressBar != null)
                    updateProgressBar.Visibility = Visibility.Collapsed;
                RefreshLauncherUpdatePanel();
            }
        }

        async Task OpenDownloadedUpdateDirectlyAsync()
        {
            if (updatePreparing || updateChecking || updateCancellation.IsCancellationRequested)
                return;
            if (offeredUpdate == null)
            {
                await CheckLauncherUpdatesAsync(true);
                if (offeredUpdate == null) return;
            }
            if (preparedUpdate == null || preparedUpdate.Offer != offeredUpdate)
                await PrepareLauncherUpdateAsync(true);
            if (preparedUpdate == null) return;

            var candidate = preparedUpdate;
            if (MessageBox.Show(this,
                "是否直接打开 " + candidate.Offer.Tag + "？\n\n" +
                "将从已校验的临时下载目录直接运行新版，确认新版主窗口显示后才关闭旧窗口。" +
                "\n不会替换桌面上的旧 EXE，也不影响 Minecraft 世界或游戏进程。" +
                "\n下次仍从旧安装位置启动。需要永久升级请点击“更新并替换旧版”。",
                "直接打开新版（不安装）",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            updatePreparing = true;
            RefreshLauncherUpdatePanel();
            try
            {
                Status("正在直接打开已校验的新版，确认窗口显示后关闭当前窗口...");
                if (updateStatusText != null)
                    updateStatusText.Text = "正在直接打开 " + candidate.Offer.Tag +
                        "，等待新主窗口渲染...";
                bool shown = await autoUpdater.OpenPreparedDirectlyAsync(candidate,
                    updateCancellation.Token);
                if (!shown || updateCancellation.IsCancellationRequested) return;
                suppressAutomaticInstallOnClose = true;
                Application.Current.Shutdown();
            }
            catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
            catch (Exception ex) { ShowError("无法直接打开新版", ex); }
            finally
            {
                updatePreparing = false;
                RefreshLauncherUpdatePanel();
            }
        }

        async Task InstallLauncherUpdateAsync()
        {
            if (updatePreparing || updateChecking) return;
            if (offeredUpdate == null)
            {
                await CheckLauncherUpdatesAsync(true);
                if (offeredUpdate == null) return;
            }
            if (preparedUpdate == null || preparedUpdate.Offer != offeredUpdate)
                await PrepareLauncherUpdateAsync(true);
            if (preparedUpdate == null) return;

            // The user explicitly requested installation by pressing the
            // update button. Do not show the old "确认安装" modal on success:
            // only genuine failures in the external updater need a dialog.
            // Auto-download never calls this method without user interaction.
            try
            {
                autoUpdater.StartExternalInstaller(preparedUpdate);
                suppressAutomaticInstallOnClose = true;
                Status("正在原位替换已校验的 " + preparedUpdate.Offer.Tag +
                    "；原 EXE 路径保持不变，完成后自动打开新版并清理临时文件。");
                Application.Current.Shutdown();
            }
            catch (Exception ex) { ShowError("无法启动独立更新程序", ex); }
        }

        // Shared two-column settings rows, matching the PCL2-style label /
        // value rhythm. Kept in WPF so it works across light/dark themes.
        static Grid SettingsForm()
        {
            var form = new Grid { Margin = new Thickness(2, 7, 0, 0) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return form;
        }

        static void SettingRow(Grid form, string name, FrameworkElement editor, string description = null)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new Label
            {
                Content = name, Target = editor, Padding = new Thickness(0, 7, 9, 0),
                FontSize = 13, VerticalAlignment = VerticalAlignment.Top
            };
            title.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            AutomationProperties.SetLabeledBy(editor, title);
            Place(form, title, row, 0);

            var right = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            if (!double.IsPositiveInfinity(editor.MaxWidth))
            {
                editor.Width = editor.MaxWidth;
                editor.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
                editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (editor is TextBox input)
            {
                input.MinWidth = 100;
                input.Margin = new Thickness(0, 0, 0, 2);
            }
            else if (editor is ComboBox choice)
                choice.Margin = new Thickness(0, 0, 0, 2);
            right.Children.Add(editor);
            if (!string.IsNullOrWhiteSpace(description))
            {
                var note = Txt(description, 11, false, true);
                note.Margin = new Thickness(0, 3, 0, 0);
                right.Children.Add(note);
            }
            Place(form, right, row, 1);
        }

        UIElement BuildSettings(string page)
        {
            if (settingsPages != null) return settingsPages[page];
            settingsPages = new Dictionary<string, UIElement>();
            StackPanel AddPage(string key, string title, string description, Grid form, params Button[] links)
            {
                var actions = Row(Btn("保存设置", SaveSettings, true));
                foreach (var link in links) actions.Children.Add(link);
                var content = Col(PageHeading(title, description), Card(Col(form, actions)));
                settingsPages.Add(key, content);
                return content;
            }

            var appearance = SettingsForm();
            settingsTheme = new ComboBox { ItemsSource = new[] { "浅色", "深色" },
                SelectedItem = settings.Theme, MaxWidth = 240 };
            settingsTheme.SelectionChanged += (sender, args) =>
            {
                settings.Theme = Convert.ToString(settingsTheme.SelectedItem);
                ApplyTheme();
            };
            SettingRow(appearance, "界面主题", settingsTheme);

            zoomValue = Txt(settings.Zoom + "%", 13, true);
            zoomValue.VerticalAlignment = VerticalAlignment.Center;
            zoomValue.Margin = new Thickness(11, 0, 0, 0);
            settingsZoom = new Slider
            {
                Minimum = 80, Maximum = 140, TickFrequency = 10,
                IsSnapToTickEnabled = true, Value = settings.Zoom,
                Width = 265, VerticalAlignment = VerticalAlignment.Center
            };
            settingsZoom.ValueChanged += (sender, args) =>
            {
                settings.Zoom = (int)settingsZoom.Value;
                ApplyZoom();
                if (zoomValue != null) zoomValue.Text = settings.Zoom + "%";
            };
            var zoomRow = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            zoomRow.Children.Add(settingsZoom);
            zoomRow.Children.Add(zoomValue);
            SettingRow(appearance, "界面缩放", zoomRow, "调整页面内容和侧栏的大小。");
            AddPage("个性化", "个性化", "调整主题与缩放。", appearance,
                Btn("恢复默认缩放", () => settingsZoom.Value = 100));

            var game = SettingsForm();
            settingsRoot = new TextBox { Text = settings.Root, ToolTip = "Minecraft 根目录（含 versions、libraries 等）" };
            SettingRow(game, "游戏根目录", settingsRoot, "作为下载和导入游戏的默认目录。");
            settingsJava = new TextBox { Text = settings.JavaPath, ToolTip = "留空时由启动组件自动选择 Java" };
            SettingRow(game, "Java 路径", settingsJava, "留空时自动查找 Java。");
            settingsPlayer = new TextBox { Text = settings.OfflineName, MaxWidth = 240 };
            SettingRow(game, "离线玩家名", settingsPlayer, "3～16 位字母、数字或下划线。");
            settingsMemory = new TextBox { Text = settings.MemoryMb.ToString(), MaxWidth = 180 };
            SettingRow(game, "游戏内存（MB）", settingsMemory, "至少 512 MB，例如 4096。");
            AddPage("设置", "启动设置", "配置游戏目录、Java 和运行内存。", game,
                Btn("打开游戏目录", OpenGameFolder));

            var network = SettingsForm();
            var routes = new[]
            {
                new KeyValuePair<string, string>("auto", "自动选择可用线路"),
                new KeyValuePair<string, string>("china", "中国大陆优先 · 镜像 → 官方"),
                new KeyValuePair<string, string>("international", "海外优先 · 官方 → 镜像"),
                new KeyValuePair<string, string>("official", "仅官方源")
            };
            settingsNetworkMode = new ComboBox {
                ItemsSource = routes,
                DisplayMemberPath = "Value",
                SelectedItem = routes.FirstOrDefault(x => x.Key == settings.NetworkMode),
                MaxWidth = 340 };
            if (settingsNetworkMode.SelectedIndex < 0) settingsNetworkMode.SelectedIndex = 0;
            SettingRow(network, "下载线路", settingsNetworkMode,
                "游戏和加载器文件来源为 Mojang、加载器官方站点与 BMCLAPI。");
            settingsDownloadThreads = new TextBox { Text = settings.DownloadThreads.ToString(), MaxWidth = 130 };
            SettingRow(network, "并发连接", settingsDownloadThreads, "2～32 个连接");
            settingsDownloadRetries = new TextBox { Text = settings.DownloadRetries.ToString(), MaxWidth = 130 };
            SettingRow(network, "失败重试", settingsDownloadRetries, "1～6 次");
            settingsProxy = new TextBox { Text = settings.Proxy, ToolTip = "例如 http://127.0.0.1:7890" };
            SettingRow(network, "下载代理", settingsProxy, "留空沿用 Windows 系统代理。");
            settingsMcim = new CheckBox
            {
                IsChecked = settings.McimEnabled,
                Content = new TextBlock
                {
                    Text = "启用 MCIM（CurseForge 可选镜像）",
                    TextWrapping = TextWrapping.Wrap
                }
            };
            SettingRow(network, "社区镜像", settingsMcim,
                "用于 CurseForge 搜索与下载，并可作为 Modrinth 的备用来源。");
            var probeText = Txt("", 12);
            var probeCard = Card(Col(Txt("线路检测结果", 16, true),
                Txt("检测公开接口的连接和首包耗时。微软登录使用 Windows 系统网络。", 12, false, true),
                probeText));
            probeCard.Visibility = Visibility.Collapsed;
            Button probe = null;
            probe = Btn("检测线路", async () =>
            {
                probeCard.Visibility = Visibility.Visible;
                if (!NetworkDownloader.TryNormalizeProxy(settingsProxy.Text, out var address))
                {
                    probeText.Text = "请输入有效的下载代理地址。";
                    return;
                }
                var draft = new LauncherConfig
                {
                    Proxy = address, McimEnabled = settingsMcim.IsChecked == true,
                    NetworkMode = (settingsNetworkMode.SelectedItem as KeyValuePair<string, string>?)?.Key ?? "auto"
                };
                probe.IsEnabled = false;
                probeText.Text = "正在检测线路…";
                try
                {
                    var result = await NetworkDiagnostics.RunAsync(draft, updateCancellation.Token);
                    probeText.Text = string.Join(Environment.NewLine, result);
                }
                catch (OperationCanceledException) when (updateCancellation.IsCancellationRequested) { }
                catch (Exception ex) { probeText.Text = "检测失败：" + ex.Message; }
                finally { probe.IsEnabled = true; }
            });
            var networkPage = AddPage("下载设置", "下载设置", "选择网络线路、并发数和资源来源。", network, probe);
            networkPage.Children.Add(probeCard);

            var updates = SettingsForm();
            var channels = new[]
            {
                new KeyValuePair<string, string>("stable", "稳定版"),
                new KeyValuePair<string, string>("preview", "预览版")
            };
            settingsUpdateChannel = new ComboBox
            {
                ItemsSource = channels, DisplayMemberPath = "Value",
                SelectedItem = channels.FirstOrDefault(x => x.Key == settings.UpdateChannel),
                MaxWidth = 340
            };
            if (settingsUpdateChannel.SelectedIndex < 0)
                settingsUpdateChannel.SelectedIndex = 0;
            SettingRow(updates, "更新通道", settingsUpdateChannel,
                "预览版可接收 Windows WPF 候选版本。");
            settingsAutoCheck = new CheckBox
            {
                IsChecked = settings.AutoCheckUpdates,
                Content = "启动时检查更新"
            };
            SettingRow(updates, "自动检测", settingsAutoCheck,
                "每 6 小时最多检查一次。");
            settingsAutoDownload = new CheckBox
            {
                IsChecked = settings.AutoDownloadUpdates,
                Content = "自动下载可用更新"
            };
            SettingRow(updates, "自动下载", settingsAutoDownload,
                "下载并完整校验新版；默认不会自行替换当前程序。");
            settingsAutoInstallOnExit = new CheckBox
            {
                IsChecked = settings.AutoInstallUpdatesOnExit,
                Content = "正常关闭启动器时自动替换原 EXE"
            };
            SettingRow(updates, "自动安装", settingsAutoInstallOnExit,
                "可选：同时启用自动检查与自动下载。仅在更新已校验完成后，" +
                "当你正常关闭启动器才安装并重新打开新版；不会中断正在使用的窗口。");
            AddPage("更新设置", "更新设置", "选择接收的版本和自动更新行为。", updates,
                Btn("查看更新", () => Navigate("启动器更新")));
            return settingsPages[page];
        }
        void SaveSettings()
        {
            if (!NetworkDownloader.TryNormalizeProxy(settingsProxy?.Text ?? settings.Proxy, out var proxy))
            {
                Navigate("下载设置");
                settingsProxy.Focus();
                Status("下载代理地址无效，例如 http://127.0.0.1:7890。");
                return;
            }
            settings.Root = settingsRoot?.Text ?? settings.Root;
            settings.Proxy = proxy;
            if (settingsProxy != null) settingsProxy.Text = proxy;
            if (settingsNetworkMode?.SelectedItem is KeyValuePair<string, string> selectedRoute)
                settings.NetworkMode = selectedRoute.Key;
            if (int.TryParse(settingsDownloadThreads?.Text, out var threads))
                settings.DownloadThreads = Math.Max(2, Math.Min(32, threads));
            if (int.TryParse(settingsDownloadRetries?.Text, out var retries))
                settings.DownloadRetries = Math.Max(1, Math.Min(6, retries));
            settings.McimEnabled = settingsMcim?.IsChecked == true;
            settings.AutoCheckUpdates = settingsAutoCheck?.IsChecked == true;
            settings.AutoDownloadUpdates = settingsAutoDownload?.IsChecked == true;
            // Do not reset an existing opt-in merely because this settings
            // subpage has not been opened during the current window session.
            if (settingsAutoInstallOnExit != null)
                settings.AutoInstallUpdatesOnExit = settingsAutoInstallOnExit.IsChecked == true;
            if (settings.AutoInstallUpdatesOnExit)
            {
                // Installing on exit is meaningful only if a verified file
                // was automatically downloaded. Make both prerequisites
                // explicit rather than silently closing a running launcher.
                settings.AutoCheckUpdates = true;
                settings.AutoDownloadUpdates = true;
                if (settingsAutoCheck != null) settingsAutoCheck.IsChecked = true;
                if (settingsAutoDownload != null) settingsAutoDownload.IsChecked = true;
            }
            if (settingsUpdateChannel?.SelectedItem is KeyValuePair<string, string> selectedUpdateChannel &&
                settings.UpdateChannel != selectedUpdateChannel.Key)
            {
                settings.UpdateChannel = selectedUpdateChannel.Key;
                updateCheckFailed = false;
                offeredUpdate = null;
                preparedUpdate = null;
                settings.LastUpdateCheckUtc = DateTime.MinValue;
                RefreshLauncherUpdatePanel();
            }
            settings.JavaPath = settingsJava?.Text ?? settings.JavaPath;
            settings.OfflineName = settingsPlayer?.Text ?? settings.OfflineName;
            if (int.TryParse(settingsMemory?.Text, out var mb)) settings.MemoryMb = Math.Max(512, mb);
            try
            {
                store.Save(settings);
                Status("设置已保存；自动检查将按新通道在下次启动时运行。");
            }
            catch (Exception ex) { ShowError("保存失败", ex); }
        }
        UIElement BuildLogs()
        {
            var box = new TextBox { Text = string.Join(Environment.NewLine, logs), IsReadOnly = true,
                AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 450,
                TextWrapping = TextWrapping.Wrap };
            return Col(PageHeading("运行日志", "本次启动器运行记录。"),
                Card(Col(Row(Btn("刷新日志", () => Navigate("运行日志")),
                        Btn("复制日志", () =>
                        {
                            if (!string.IsNullOrEmpty(box.Text))
                            {
                                Clipboard.SetText(box.Text);
                                Status("日志已复制。");
                            }
                        })),
                    box)));
        }
        void EditOfflineAccount()
        {
            var dialog = new Window
            {
                Owner = this, Title = "离线登录", Width = 395, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var entry = new TextBox { Text = settings.OfflineName, MaxLength = 16 };
            AutomationProperties.SetName(entry, "玩家名");
            var hint = Txt("玩家名可使用 3～16 位字母、数字或下划线。", 12, false, true);
            var confirm = Btn("使用此玩家名", () =>
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Text, "^[A-Za-z0-9_]{3,16}$"))
                {
                    hint.Text = "请检查玩家名的长度和字符。";
                    entry.Focus();
                    return;
                }
                string previousName = settings.OfflineName;
                string previousMode = settings.AccountMode;
                settings.OfflineName = entry.Text;
                settings.AccountMode = "offline";
                try { store.Save(settings); }
                catch (Exception ex)
                {
                    settings.OfflineName = previousName;
                    settings.AccountMode = previousMode;
                    hint.Text = "保存失败：" + ex.Message;
                    return;
                }
                dialog.DialogResult = true;
            }, true);
            confirm.IsDefault = true;
            var cancel = new Button { Content = "取消", IsCancel = true };
            var body = Card(Col(Txt("玩家名", 16, true), hint, entry, Row(confirm, cancel)));
            body.Margin = new Thickness(14);
            dialog.Content = body;
            dialog.Loaded += (sender, args) => { entry.Focus(); entry.SelectAll(); };
            if (dialog.ShowDialog() == true) Navigate("首页");
        }
        async Task LoginMicrosoftAsync()
        {
            // Check the installed Evergreen Runtime before invoking CmlLib's
            // embedded OAuth browser, rather than letting its native host fail.
            if (!WebView2RuntimeSupport.IsAvailable(out _))
            {
                Status("微软正版登录需要安装 Microsoft Edge WebView2 Runtime。");
                WebView2RuntimeSupport.OfferOfficialSetup(this);
                return;
            }
            try
            {
                Status("正在启动 Microsoft 正版登录，请在安全登录窗口中完成授权...");
                string name = await GetMicrosoftService().SignInAsync();
                settings.AccountMode = "microsoft";
                settings.AccountName = name;
                store.Save(settings); Navigate("首页");
                Status("已登录 Minecraft Java 正版账号：" + name);
            }
            catch (Exception ex) when (WebView2RuntimeSupport.IsRuntimeFailure(ex))
            {
                Log("微软登录 WebView2 运行环境异常：" + ex.GetType().Name);
                Status("微软正版登录需要安装或修复 WebView2 Runtime。");
                WebView2RuntimeSupport.OfferOfficialSetup(this);
            }
            catch (Exception ex) { ShowError("微软正版登录失败", ex); }
        }
        async Task SignOutAsync()
        {
            try
            {
                if (settings.AccountMode == "microsoft")
                    await GetMicrosoftService().SignOutAsync(); // WPF preview token store and browser session
                settings.AccountMode = "signed_out"; settings.AccountName = "";
                store.Save(settings); Navigate("首页");
            }
            catch (Exception ex) { ShowError("退出账户失败", ex); }
        }
        static string ProfileVersion(GameProfile profile)
        {
            string version = string.IsNullOrWhiteSpace(profile.BaseVersion)
                ? profile.LaunchVersion : profile.BaseVersion;
            string loader = string.IsNullOrWhiteSpace(profile.Loader) ||
                string.Equals(profile.Loader, "vanilla", StringComparison.OrdinalIgnoreCase)
                    ? "原版" : profile.Loader;
            return string.Join("  ·  ", new[] { version, loader }.Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        void RefreshGameProcessStatus()
        {
            if (currentPage != "首页" || gameProcessText == null) return;
            var profile = activeProfile;
            bool hasProfile = profile != null;
            homeProfileName.Text = hasProfile ? profile.Name : "尚未选择游戏版本";
            homeProfileInfo.Text = hasProfile ? ProfileVersion(profile) : "从左侧选择已安装的版本，或先下载游戏。";
            homeVersionCaption.Text = hasProfile ? profile.Name : "Minecraft Java 版";
            if (!hasProfile)
            {
                homeLaunchButton.IsEnabled = true;
                homeLaunchLabel.Text = "下载游戏";
                AutomationProperties.SetName(homeLaunchButton, homeLaunchLabel.Text);
                gameProcessText.Text = "";
                return;
            }

            var state = gameProcesses.GetState(profile);
            bool launching = launchingProfiles.Contains(profile);
            bool running = state.Status == GameProcessStatus.Running;
            homeLaunchButton.IsEnabled = !launching && !running;
            homeLaunchLabel.Text = launching ? "正在启动…" : running ? "游戏运行中" : "启动游戏";
            AutomationProperties.SetName(homeLaunchButton, homeLaunchLabel.Text);
            if (launching)
                gameProcessText.Text = "正在准备游戏文件与运行环境。";
            else if (running)
                gameProcessText.Text = "进程 " + state.ProcessId;
            else if (state.Status == GameProcessStatus.Exited)
                gameProcessText.Text = "游戏已退出  ·  退出码 " + (state.ExitCode?.ToString() ?? "未知");
            else if (state.Status == GameProcessStatus.Unavailable)
                gameProcessText.Text = "暂时无法读取游戏进程状态。";
            else
                gameProcessText.Text = "";
        }

        async Task LaunchAsync(string serverAddress = null)
        {
            var profile = activeProfile;
            if (profile == null) { MessageBox.Show("请先导入或安装游戏实例"); return; }
            if (launchingProfiles.Contains(profile))
            {
                Status("当前实例正在启动，请不要重复点击。");
                return;
            }
            if (gameProcesses.GetState(profile).Status == GameProcessStatus.Running)
            {
                Status("当前实例已经在运行，请返回游戏窗口；若要进入服务器，请在游戏内连接。");
                return;
            }

            launchingProfiles.Add(profile);
            RefreshGameProcessStatus();
            try
            {
                Status("准备启动 " + profile.Name);
                Process started;
                if (settings.AccountMode == "microsoft")
                {
                    // Never silently use an offline identity for a Microsoft
                    // premium account, or expose OAuth provider exceptions.
                    CmlLib.Core.Auth.MSession session;
                    try
                    {
                        session = await GetMicrosoftService().GetSessionAsync();
                    }
                    catch (MicrosoftReauthenticationRequiredException)
                    {
                        Status("微软正版登录凭据已失效，需要重新授权。");
                        var answer = MessageBox.Show(this,
                            "当前 Microsoft 账号没有可用于静默续期的登录令牌。\n\n" +
                            "这通常发生在首次切换到 WPF 启动器、缓存缺失或登录过期后。\n" +
                            "Minecraft 游戏和存档不受影响。\n\n" +
                            "是否现在打开微软正版登录窗口？完成授权后将继续启动游戏。",
                            "需要重新登录微软账号",
                            MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (answer != MessageBoxResult.Yes)
                        {
                            Status("已取消启动。可以在玩家账户区域重新登录微软正版。");
                            return;
                        }
                        await LoginMicrosoftAsync();
                        if (!GetMicrosoftService().HasSession)
                        {
                            Status("微软正版重新登录未完成，游戏尚未启动。");
                            return;
                        }
                        session = await GetMicrosoftService().GetSessionAsync();
                    }
                    started = await minecraft.LaunchMicrosoftAsync(profile, session, serverAddress);
                }
                else if (settings.AccountMode == "offline")
                    started = await minecraft.LaunchOfflineAsync(profile, serverAddress);
                else throw new InvalidOperationException("请先登录微软正版账号或设置离线玩家名。");

                // Launch succeeded even if OS process telemetry becomes
                // unavailable. Do not misreport a running game as launch failure.
                try
                {
                    gameProcesses.Track(profile, started);
                    Status("Minecraft 已启动：" + profile.Name + "（PID " + started.Id + "）");
                }
                catch (Exception trackingError)
                {
                    Log("游戏已启动，但无法跟踪进程状态：" + trackingError.GetType().Name);
                    Status("Minecraft 已启动，但无法获取进程状态。请以游戏窗口为准。");
                }
            }
            catch (Exception ex) { ShowError("启动失败", ex); }
            finally
            {
                launchingProfiles.Remove(profile);
                RefreshGameProcessStatus();
            }
        }
        void ImportLocalGame()
        {
            var dialog = new OpenFileDialog { Filter = "Minecraft 版本 JSON (*.json)|*.json", Title = "选择版本文件（versions\\版本号\\版本号.json）" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                // Keep the real root of the selected external installation,
                // rather than silently registering everything under settings.Root.
                var profile = MinecraftInstanceLocator.ImportVersionMetadata(dialog.FileName);
                var existing = settings.Profiles.FirstOrDefault(p =>
                    string.Equals(string.IsNullOrWhiteSpace(p.LaunchVersion) ? p.BaseVersion : p.LaunchVersion,
                        profile.LaunchVersion, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals((p.Directory ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        profile.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    activeProfile = existing;
                    Navigate("首页");
                    Status("此版本已在实例列表中，已切换到：" + existing.Name);
                    return;
                }

                settings.Profiles.Add(profile);
                try { store.Save(settings); }
                catch { settings.Profiles.Remove(profile); throw; }
                activeProfile = profile;
                Navigate("首页");
                Status("已导入本地版本：" + profile.LaunchVersion + "（" + profile.Directory + "）");
            }
            catch (Exception ex) { ShowError("导入实例失败", ex); }
        }
        UIElement NoVersionSelected(string title)
        {
            return Card(Col(Txt(title, 16, true),
                Txt("请先选择一个已安装的游戏版本。", 12, false, true),
                Row(Btn("版本选择", () => Navigate("版本选择"), true),
                    Btn("下载游戏", () => Navigate("游戏版本")))));
        }

        UIElement BuildInstanceOverview()
        {
            var profile = activeProfile;
            if (profile == null) return NoVersionSelected("版本设置");
            return Col(PageHeading(profile.Name, ProfileVersion(profile)),
                Card(Col(Txt("版本信息", 16, true),
                    Txt("启动版本  " + (string.IsNullOrWhiteSpace(profile.LaunchVersion)
                        ? profile.BaseVersion : profile.LaunchVersion), 12),
                    Txt("游戏目录", 12, false, true),
                    Txt(string.IsNullOrWhiteSpace(profile.Directory) ? settings.Root : profile.Directory, 13),
                    Row(Btn("打开游戏目录", OpenGameFolder),
                        Btn("检查版本文件", CheckInstance)))),
                Card(Col(Txt("管理此版本", 16, true),
                    Row(Btn("模组与资源", () => Navigate("模组更新")),
                        Btn("管理存档", () => Navigate("存档管理"))),
                    Txt("从启动器列表移除时，游戏目录和存档会保留在原位置。", 12, false, true),
                    Row(Btn("从列表移除", () => RemoveInstance(profile))))));
        }

        UIElement BuildInstanceSaves()
        {
            var profile = activeProfile;
            if (profile == null) return NoVersionSelected("存档管理");
            var result = Txt("", 12, false, true);
            return Col(PageHeading("存档管理", "当前版本  " + profile.Name),
                Card(Col(Txt("备份与恢复", 16, true),
                    Txt("将世界备份为 ZIP。恢复时只导入新世界，遇到同名世界会取消本次恢复。", 12, false, true),
                    Row(Btn("备份存档", async () => await BackupSavesAsync(profile, result), true),
                        Btn("恢复存档", async () => await RestoreSavesAsync(profile, result)),
                        Btn("打开游戏目录", OpenGameFolder)),
                    result)));
        }

        async Task BackupSavesAsync(GameProfile profile, TextBlock result)
        {
            var file = new SaveFileDialog
            {
                FileName = "saves-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                Filter = "ZIP 压缩包|*.zip"
            };
            if (file.ShowDialog(this) != true) return;
            try
            {
                result.Text = "正在备份存档…";
                string path = await Task.Run(() => LocalInstanceTools.BackupSaves(profile, file.FileName));
                result.Text = "备份已保存  " + path;
                Status("存档备份完成：" + profile.Name);
            }
            catch (Exception ex)
            {
                result.Text = "备份失败。";
                ShowError("备份存档失败", ex);
            }
        }

        async Task RestoreSavesAsync(GameProfile profile, TextBlock result)
        {
            var file = new OpenFileDialog
            {
                Filter = "ZIP 存档备份 (*.zip)|*.zip", Title = "选择存档备份"
            };
            if (file.ShowDialog(this) != true) return;
            if (MessageBox.Show(this, "将把备份中的新世界恢复到“" + profile.Name +
                "”。发现同名世界时将取消。继续恢复？", "恢复存档",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                result.Text = "正在检查备份并恢复存档…";
                int worlds = await Task.Run(() => LocalInstanceTools.RestoreSaves(profile, file.FileName));
                result.Text = "已恢复 " + worlds + " 个世界。";
                Status("存档恢复完成：" + profile.Name);
            }
            catch (Exception ex)
            {
                result.Text = "恢复失败。";
                ShowError("恢复存档失败", ex);
            }
        }

        void RemoveInstance(GameProfile profile)
        {
            if (MessageBox.Show(this, "从启动器列表中移除“" + profile.Name +
                "”？游戏和存档文件会保留在原目录。", "移除版本",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            int index = settings.Profiles.IndexOf(profile);
            if (index < 0) return;
            settings.Profiles.RemoveAt(index);
            try { store.Save(settings); }
            catch (Exception ex)
            {
                settings.Profiles.Insert(index, profile);
                ShowError("移除版本失败", ex);
                return;
            }
            activeProfile = settings.Profiles.FirstOrDefault();
            Navigate("版本选择");
            Status("已从启动器列表移除：" + profile.Name);
        }

        void CheckInstance()
        {
            if (activeProfile == null)
            {
                MessageBox.Show(this, "请先选择已经安装的游戏实例。", "检测本地文件");
                return;
            }
            try
            {
                var location = MinecraftInstanceLocator.Resolve(activeProfile, settings);
                if (location.MetadataFound)
                {
                    string source = location.UsesInstanceDirectory
                        ? "当前实例目录" : "启动器公共游戏目录";
                    Status("本地版本检测成功：" + location.VersionId + "（" + source + "）");
                    MessageBox.Show(this,
                        "已找到 Minecraft 版本元数据。\n\n" +
                        "游戏版本：" + location.VersionId + "\n" +
                        "检测位置：" + source + "\n" +
                        "实际启动目录：" + location.GameRoot + "\n" +
                        "版本文件：" + location.MetadataPath + "\n\n" +
                        "此检查确认启动器能定位版本 JSON；不代表所有游戏资源、Java 运行环境或模组都已完整校验。\n" +
                        "你已能正常启动游戏时，不需要重新下载或安装该版本。",
                        "检测本地文件 · 已找到游戏版本",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    Status("本地版本检测未找到：" + location.VersionId);
                    string paths = location.InstanceMetadataPath == location.SharedMetadataPath
                        ? location.SharedMetadataPath
                        : "当前实例：" + location.InstanceMetadataPath + "\n" +
                          "公共目录：" + location.SharedMetadataPath;
                    var repair = MessageBox.Show(this,
                        "没有找到与当前实例版本 ID 对应的本地 JSON 文件。\n\n" +
                        "版本 ID：" + location.VersionId + "\n" +
                        "已检查路径：\n" + paths + "\n\n" +
                        "游戏能正常启动不代表这里登记的实例路径正确。\n" +
                        "是否选择已有的 " + location.VersionId + ".json 来修复当前实例目录？\n" +
                        "只更新启动器配置，不移动、删除或重新下载游戏文件。",
                        "检测本地文件 · 修复实例路径",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (repair != MessageBoxResult.Yes) return;

                    var picker = new OpenFileDialog
                    {
                        Filter = "Minecraft 版本 JSON (*.json)|*.json",
                        Title = "选择 versions\\" + location.VersionId + "\\" + location.VersionId + ".json"
                    };
                    if (picker.ShowDialog(this) != true) return;
                    var imported = MinecraftInstanceLocator.ImportVersionMetadata(picker.FileName);
                    if (!string.Equals(imported.LaunchVersion, location.VersionId,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show(this,
                            "选中的版本为 " + imported.LaunchVersion + "，但当前实例要求 " +
                            location.VersionId + "。请选相同版本的 JSON，或改用“导入本地实例”添加新版本。",
                            "版本不匹配", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    string previousDirectory = activeProfile.Directory;
                    activeProfile.Directory = imported.Directory;
                    try { store.Save(settings); }
                    catch { activeProfile.Directory = previousDirectory; throw; }
                    Status("已修复实例目录：" + imported.Directory);
                    CheckInstance();
                }
            }
            catch (Exception ex)
            {
                ShowError("检测本地游戏版本失败", ex);
            }
        }
        void OpenGameFolder()
        {
            try
            {
                if (activeProfile != null) LocalInstanceTools.OpenFolder(activeProfile);
                else { Directory.CreateDirectory(settings.Root); System.Diagnostics.Process.Start("explorer.exe", "\"" + settings.Root + "\""); }
            }
            catch (Exception ex) { ShowError("打开游戏目录失败", ex); }
        }
        UIElement BuildAbout()
        {
            return Col(PageHeading("关于", "BlockCraft Launcher  ·  Minecraft Java 启动器"),
                Card(Col(Txt(LauncherAutoUpdateService.CurrentTag, 17, true),
                    Txt("Windows x64  ·  .NET Framework 4.8  ·  " +
                        LauncherAutoUpdateService.BuildChannelLabel, 12, false, true),
                    Row(Btn("检查更新", () => Navigate("启动器更新")),
                        Btn("查看发布记录", () => LauncherReleaseService.OpenOfficialRelease(
                            "https://github.com/minecraftlu/BlockCraftLauncher/releases"))))),
                Card(Col(Txt("本版更新", 16, true),
                    Txt("启动页分为账号操作与游戏卡片，版本选择和设置使用独立页面。"),
                    Txt("下载页统一筛选和图文列表，查看项目详情后可返回原搜索结果。"),
                    Txt("设置按用途分组，多人游戏、皮肤编辑、更新和日志各有独立入口。"))));
        }

        void ShowError(string title, Exception ex) { Log(title + "：" + ex); MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
