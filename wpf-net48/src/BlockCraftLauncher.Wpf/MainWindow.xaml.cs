using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
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

        readonly SettingsStore store = new SettingsStore();
        readonly LauncherConfig settings;
        readonly LauncherAutoUpdateService autoUpdater;
        readonly CancellationTokenSource updateCancellation = new CancellationTokenSource();
        LauncherUpdateOffer offeredUpdate;
        PreparedLauncherUpdate preparedUpdate;
        bool updateChecking;
        bool updatePreparing;
        TextBlock updateStatusText, updateNotesText, homeUpdateText;
        Border homeUpdateCard;
        ProgressBar updateProgressBar;
        Button updateInstallButton, updateCheckButton, updateDirectOpenButton;
        CheckBox settingsAutoCheck, settingsAutoDownload;
        ComboBox settingsUpdateChannel;
        readonly MinecraftBackend minecraft;
        MicrosoftAccountService microsoft;
        MicrosoftAccountService GetMicrosoftService() => microsoft ?? (microsoft = new MicrosoftAccountService());
        readonly ModLoaderInstaller modLoaders;
        readonly ObservableCollection<MinecraftVersionEntry> gameVersions = new ObservableCollection<MinecraftVersionEntry>();
        readonly DownloadTaskCenter downloads = new DownloadTaskCenter(3);
        DownloadJob versionInstallJob;
        bool installingGame;
        ComboBox versionCategory, settingsNetworkMode;
        TextBox settingsDownloadThreads, settingsDownloadRetries;
        ListBox downloadList;
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
        ComboBox localResourceKind;
        ListBox localResourceList, serverList, lanWorldList;
        TextBox serverName, serverAddress;
        TextBox packSearchQuery;
        ListBox packSearchResults;
        ListBox modUpgradeResults;
        TextBox cfSearchQuery;
        ListBox cfSearchResults;
        TextBlock cfSearchStatus;
        CancellationTokenSource cfSearchCancellation;
        CheckBox settingsMcim;
        GameProfile activeProfile;
        string currentPage = "首页";
        ComboBox homeProfile, versionLoader, settingsTheme;
        Button versionInstallButton, versionCancelButton;
        TextBox versionQuery, resourceQuery, settingsRoot, settingsProxy, settingsJava, settingsPlayer, settingsMemory;
        ListBox versionList, resourceList;
        TextBlock accountText, zoomValue;
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
            TopSettingsButton.Click += (sender, args) => Navigate("设置");
            TopMoreButton.Click += (sender, args) => Navigate("模组更新");
            ApplyTheme(); ApplyZoom(); BuildSidebar(); Navigate("首页");
            Log("已读取旧版本配置；WPF 预览设置将单独保存，不覆盖原 Python 启动器配置。");
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
            Closed += (sender, args) =>
            {
                gameProcessTimer.Stop();
                updateCancellation.Cancel();
                gameProcesses.Dispose(); // Does not terminate the Minecraft game.
            };
        }

        static TextBlock Txt(string text, double size = 14, bool bold = false, bool muted = false)
        {
            var label = new TextBlock
            {
                Text = text,
                FontSize = size >= 22 ? size - 1 : size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size >= 20 ? size + 9 : size + 6,
                Margin = new Thickness(0, 0, 0, size >= 20 ? 12 : 9)
            };
            if (muted) label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return label;
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
            // Consistent Segoe MDL2 icons replace mixed emoji, box symbols and
            // font-dependent characters across all generated feature pages.
            var text = (caption ?? "").Trim();
            while (text.Length > 0 && "▶⬇◇▤☼⇩⚙▣⟳ ".IndexOf(text[0]) >= 0)
                text = text.Substring(1).TrimStart();
            var b = new Button { MinHeight = 37 };
            var content = new StackPanel { Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            string glyph = ActionGlyph(text);
            if (glyph.Length > 0)
            {
                var symbol = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
                symbol.SetBinding(TextBlock.ForegroundProperty,
                    new Binding("Foreground") { Source = b, Mode = BindingMode.OneWay });
                content.Children.Add(symbol);
            }
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap };
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
            // Wrap narrow toolbars and translated text instead of pushing
            // their buttons underneath the right-hand scrollbar.
            var p = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in children) p.Children.Add(c); return p;
        }
        static void AddNavSection(Panel sidebar, string heading, IEnumerable<string> names, Action<string> navigate, Dictionary<string, Button> buttonMap)
        {
            var title = Txt(heading, 11, true, true);
            title.Margin = new Thickness(13, 5, 0, 8);
            sidebar.Children.Add(title);
            foreach (var name in names)
            {
                var btn = new Button();
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
                btn.Click += (sender, args) => navigate(name);
                btn.SetResourceReference(FrameworkElement.StyleProperty, "SidebarItem");
                sidebar.Children.Add(btn);
                buttonMap[name] = btn;
            }
            sidebar.Children.Add(new Separator { Margin = new Thickness(10, 8, 10, 13) });
        }
        static string NavLabel(string page)
        {
            switch (page)
            {
                case "游戏版本": return "原版游戏";
                case "资源中心": return "Mod";
                case "光影引擎": return "光影包";
                case "资源包": return "资源包";
                case "整合包": return "整合包";
                case "CurseForge": return "CurseForge";
                default: return page;
            }
        }

        static string NavGlyph(string page)
        {
            switch (page)
            {
                case "首页": return "\uE80F";
                case "游戏版本": return "\uE8A5";
                case "下载管理": return "\uE896";
                case "资源中心": return "\uE8B7";
                case "光影引擎": return "\uE790";
                case "资源包": return "\uE8A7";
                case "整合包": return "\uE8F1";
                case "CurseForge": return "\uE753";
                case "模组更新": return "\uE72C";
                case "多人联机": return "\uE902";
                case "启动器更新": return "\uE777";
                case "设置": return "\uE713";
                case "运行日志": return "\uE8A5";
                default: return "\uE8A7";
            }
        }
        void BuildSidebar()
        {
            SideMenu.Children.Clear(); sidebarButtons.Clear();
            AddNavSection(SideMenu, "游戏", new[] { "首页", "游戏版本", "下载管理" }, Navigate, sidebarButtons);
            AddNavSection(SideMenu, "社区资源", new[] { "资源中心", "整合包", "资源包", "光影引擎", "CurseForge" }, Navigate, sidebarButtons);
            AddNavSection(SideMenu, "管理与工具", new[] { "模组更新", "多人联机", "启动器更新", "设置", "运行日志" }, Navigate, sidebarButtons);
            SideMenu.Children.Add(Txt("当前账号", 12, true, true));
            SideMenu.Children.Add(Txt(settings.AccountMode == "offline" ? "离线：" + settings.OfflineName :
                (settings.AccountMode == "microsoft" ? "微软正版：" + settings.AccountName : "未登录"), 13));
            SideMenu.Children.Add(Btn("切换到离线模式", EditOfflineAccount));
            SideMenu.Children.Add(Btn("微软正版登录", async () => await LoginMicrosoftAsync()));
            SideMenu.Children.Add(Btn("退出账号", async () => await SignOutAsync()));
        }
        void Navigate(string page)
        {
            // Cancel searches before replacing the page to avoid a completed
            // response rendering into a different resource page.
            resourceSearchCancellation?.Cancel();
            resourceSearchCancellation = null;
            cfSearchCancellation?.Cancel();
            cfSearchCancellation = null;
            currentPage = page;
            gameProcessText = null;
            homeLaunchButton = null;
            updateStatusText = null;
            updateNotesText = null;
            updateInstallButton = null;
            updateCheckButton = null;
            updateDirectOpenButton = null;
            updateProgressBar = null;
            homeUpdateText = null;
            homeUpdateCard = null;
            SideBar.Visibility = page == "首页" ? Visibility.Collapsed : Visibility.Visible;
            SideColumn.Width = page == "首页" ? new GridLength(0) : new GridLength(188);
            foreach (var pair in sidebarButtons)
                pair.Value.SetResourceReference(FrameworkElement.StyleProperty,
                    pair.Key == page ? "SidebarItemActive" : "SidebarItem");
            string primary = page == "首页" ? "启动"
                : (page == "游戏版本" || page == "下载管理" || page == "资源中心"
                    || page == "光影引擎" || page == "资源包" || page == "整合包"
                    || page == "CurseForge" ? "下载" : page == "设置" ? "设置" : "更多");
            TopLaunchButton.SetResourceReference(FrameworkElement.StyleProperty,
                primary == "启动" ? "HeaderTabActive" : "HeaderTab");
            TopDownloadButton.SetResourceReference(FrameworkElement.StyleProperty,
                primary == "下载" ? "HeaderTabActive" : "HeaderTab");
            TopSettingsButton.SetResourceReference(FrameworkElement.StyleProperty,
                primary == "设置" ? "HeaderTabActive" : "HeaderTab");
            TopMoreButton.SetResourceReference(FrameworkElement.StyleProperty,
                primary == "更多" ? "HeaderTabActive" : "HeaderTab");
            switch (page)
            {
                case "首页": PageHost.Content = BuildHome(); break;
                case "游戏版本": PageHost.Content = BuildVersions(); break;
                case "下载管理": PageHost.Content = BuildDownloadCenter(); break;
                case "资源中心": PageHost.Content = BuildResources("mod"); break;
                case "光影引擎": PageHost.Content = BuildResources("shader"); break;
                case "资源包": PageHost.Content = BuildResources("resourcepack"); break;
                case "整合包": PageHost.Content = BuildModpacks(); break;
                case "CurseForge": PageHost.Content = BuildCurseForge(); break;
                case "模组更新": PageHost.Content = BuildModUpdates(); break;
                case "多人联机": PageHost.Content = BuildMultiplayer(); break;
                case "启动器更新": PageHost.Content = BuildLauncherUpdates(); break;
                case "设置": PageHost.Content = BuildSettings(); break;
                case "运行日志": PageHost.Content = BuildLogs(); break;
                default: PageHost.Content = Txt("页面不存在"); break;
            }
            PageScroll.ScrollToTop(); Status("当前页面：" + page);
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
            // XAML Freezable resources may be frozen after loading. Replace the brush so
            // DynamicResource consumers update without mutating a frozen object.
            void Set(string key, string value) { Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
            Set("WindowBrush", light ? "#F1F5FA" : "#151D2B");
            Set("PageBrush", light ? "#EAF3FC" : "#111827");
            Set("PanelBrush", light ? "#FFFFFF" : "#1D2939");
            Set("PanelAltBrush", light ? "#F5F9FE" : "#26364B");
            Set("TextBrush", light ? "#22374D" : "#EFF5FF");
            Set("MutedBrush", light ? "#667E95" : "#ACBCD0");
            Set("BorderBrushTheme", light ? "#D8E5F3" : "#33475D");
            Set("HoverBrush", light ? "#EAF3FF" : "#2E425A");
            Set("SelectedBrush", light ? "#E0EFFF" : "#274968");
            Set("BadgeBrush", light ? "#E8F3FF" : "#314C67");
            Set("AccentBrush", IsHexColor(settings.Accent) ? settings.Accent : "#1977D2");
            Set("AccentTextBrush", "#FFFFFF");
            Set("HeaderBrush", light ? "#176DBF" : "#145C9C");
            Set("HeaderTextBrush", "#FFFFFF");
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
                Shell.Width = Math.Max(840, (WindowViewport.ActualWidth - 2) / scale);
            if (WindowViewport.ActualHeight > 0)
                Shell.Height = Math.Max(580, (WindowViewport.ActualHeight - 2) / scale);
        }
        void WindowViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateShellSize();

        Grid DashboardGrid()
        {
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66, GridUnitType.Star) });
            for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            return grid;
        }
        static void Place(Grid grid, UIElement element, int row, int col)
        {
            Grid.SetRow(element, row); Grid.SetColumn(element, col); grid.Children.Add(element);
        }
        UIElement BuildHome()
        {
            // Prominent selected instance and one clear launch action,
            // followed by compact download and account management cards.
            var root = Col(Txt("启动 Minecraft", 24, true),
                Txt("选择已经安装的游戏实例，然后开始你的方块世界。", 13, false, true));

            var updateNotice = Col(Txt("发现启动器新版本", 17, true));
            homeUpdateText = Txt("", 12, false, true);
            updateNotice.Children.Add(homeUpdateText);
            updateNotice.Children.Add(Row(
                Btn("查看新版本与更新内容", () => Navigate("启动器更新"), true),
                Btn("查看更新设置", () => Navigate("设置"))));
            homeUpdateCard = Card(updateNotice);
            root.Children.Add(homeUpdateCard);
            RefreshLauncherUpdatePanel();

            var hero = new Grid { Margin = new Thickness(0, 12, 0, 4) };
            hero.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(63, GridUnitType.Star) });
            hero.ColumnDefinitions.Add(new ColumnDefinition {
                Width = new GridLength(37, GridUnitType.Star) });

            var launchPanel = Col(Txt("启动游戏", 19, true),
                Txt("选择实例", 12, false, true));
            var selectionCaption = Txt(activeProfile == null
                ? "未选择游戏版本，请先前往下载页面。"
                : "当前：" + activeProfile.Name + " · " + activeProfile.BaseVersion, 12, false, true);
            homeProfile = new ComboBox
            {
                ItemsSource = settings.Profiles,
                SelectedItem = activeProfile,
                DisplayMemberPath = "Name",
                MaxWidth = 430,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            homeProfile.SelectionChanged += (sender, args) =>
            {
                activeProfile = homeProfile.SelectedItem as GameProfile;
                selectionCaption.Text = activeProfile == null
                    ? "未选择游戏版本，请先前往下载页面。"
                    : "当前：" + activeProfile.Name + " · " + activeProfile.BaseVersion;
                RefreshGameProcessStatus();
            };
            launchPanel.Children.Add(homeProfile);
            launchPanel.Children.Add(selectionCaption);
            var launch = Btn("▶   启动游戏", async () => await LaunchAsync(), true);
            launch.Height = 62;
            launch.FontSize = 19;
            launch.HorizontalAlignment = HorizontalAlignment.Stretch;
            launch.Margin = new Thickness(0, 12, 0, 12);
            homeLaunchButton = launch;
            launchPanel.Children.Add(launch);
            gameProcessText = Txt("", 12, false, true);
            gameProcessText.Margin = new Thickness(0, 0, 0, 8);
            launchPanel.Children.Add(gameProcessText);
            RefreshGameProcessStatus();
            launchPanel.Children.Add(Row(
                Btn("切换 / 管理实例", ManageInstance),
                Btn("检查版本文件", CheckInstance),
                Btn("游戏目录", OpenGameFolder)));
            var left = Card(launchPanel);
            left.Margin = new Thickness(0, 0, 13, 12);
            Grid.SetColumn(left, 0);
            hero.Children.Add(left);

            var account = Col(Txt("玩家账号", 19, true));
            accountText = Txt(settings.AccountMode == "offline" ?
                "离线玩家：" + settings.OfflineName :
                (settings.AccountMode == "microsoft" ?
                 ((microsoft != null && microsoft.HasSession) ?
                     "微软正版：" + settings.AccountName :
                     "微软正版：请重新验证登录") : "尚未登录"));
            account.Children.Add(accountText);
            account.Children.Add(Txt("可以使用正版账号，也可以切换到离线模式进行单人游戏。", 12, false, true));
            account.Children.Add(Btn("微软正版登录", async () => await LoginMicrosoftAsync(), true));
            account.Children.Add(Btn("使用离线账号", EditOfflineAccount));
            account.Children.Add(Btn("退出当前账号", async () => await SignOutAsync()));
            var right = Card(account);
            right.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumn(right, 1);
            hero.Children.Add(right);
            root.Children.Add(hero);

            var shortcuts = Col(Txt("快捷操作", 18, true),
                Txt("下载游戏、管理资源和调整设置", 12, false, true));
            shortcuts.Children.Add(Row(
                Btn("⬇  下载游戏版本", () => Navigate("游戏版本"), true),
                Btn("◇  模组与资源", () => Navigate("资源中心")),
                Btn("▤  整合包", () => Navigate("整合包")),
                Btn("☼  光影与材质", () => Navigate("光影引擎")),
                Btn("⇩  下载任务", () => Navigate("下载管理")),
                Btn("⚙  启动器设置", () => Navigate("设置"))));
            root.Children.Add(Card(shortcuts));

            root.Children.Add(Card(Col(Txt("使用提示", 16, true),
                Txt("当前版本包含独立的 Minecraft 版本管理、下载与联机入口。"
                    + "尚未完成真实设备验收的功能将继续标为预览，不会覆盖旧版启动器。", 12, false, true),
                Btn("查看本次更新说明", ShowReleaseNotes))));
            return root;
        }
        UIElement BuildVersions()
        {
            var root = Col(Txt("Minecraft 游戏版本", 24, true),
                Txt("选择版本和加载器，安装任务在后台运行。请勿同时使用旧版安装同一游戏版本。", 13, false, true));
            var toolbar = Row();
            versionQuery = new TextBox { Width = 260, ToolTip = "搜索版本号" };
            versionQuery.TextChanged += (s, e) => FilterVersions();
            toolbar.Children.Add(versionQuery);
            versionCategory = new ComboBox
            {
                Width = 145,
                ItemsSource = new[] { "全部版本", "正式版", "快照版", "远古 Beta", "远古 Alpha" },
                SelectedIndex = 0,
                Margin = new Thickness(8, 0, 0, 0)
            };
            versionCategory.SelectionChanged += (sender, e) => FilterVersions();
            toolbar.Children.Add(versionCategory);
            toolbar.Children.Add(Btn("刷新在线版本", async () => await LoadVersionsAsync(), true));
            toolbar.Children.Add(Btn("下载任务", () => Navigate("下载管理")));
            root.Children.Add(toolbar);
            versionList = new ListBox { ItemsSource = gameVersions,
                ItemTemplate = (DataTemplate)FindResource("VersionItemTemplate"),
                Height = 350, MinWidth = 300, Margin = new Thickness(0, 10, 0, 12) };
            root.Children.Add(Card(versionList));
            var options = Col(Txt("选择加载器", 18, true));
            versionLoader = new ComboBox { ItemsSource = new[] { "原版", "Fabric", "Forge", "NeoForge", "Quilt" }, SelectedIndex = 0, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
            options.Children.Add(versionLoader);
            options.Children.Add(Txt("原版 / Fabric / Forge / NeoForge / Quilt：通过各自安装组件获取匹配的版本文件。部分新版本可能暂不兼容。", 12, false, true));
            versionInstallButton = Btn("安装所选版本", async () => await InstallSelectedVersionAsync(), true);
            versionCancelButton = Btn("取消安装", () => versionInstallJob?.Cancel());
            versionCancelButton.IsEnabled = false;
            options.Children.Add(Row(versionInstallButton, versionCancelButton,
                Btn("导入本地实例", ImportLocalGame), Btn("打开游戏目录", OpenGameFolder)));
            root.Children.Add(Card(options)); return root;
        }
        void FilterVersions()
        {
            if (versionList == null || versionQuery == null) return;
            string category = Convert.ToString(versionCategory?.SelectedItem) ?? "全部版本";
            string search = (versionQuery.Text ?? "").Trim();
            versionList.Items.Filter = raw =>
            {
                var item = raw as MinecraftVersionEntry;
                return item != null &&
                    (category == "全部版本" || item.Category == category) &&
                    (search.Length == 0 || item.Id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            };
        }
        async Task LoadVersionsAsync()
        {
            if (loadingVersions) return;
            loadingVersions = true; Status("正在获取版本列表...");
            try
            {
                var versions = await new MinecraftVersionCatalog(settings).LoadAsync(true, CancellationToken.None);
                gameVersions.Clear(); foreach (var v in versions) gameVersions.Add(v);
                FilterVersions(); Status("已加载版本数量：" + gameVersions.Count);
            }
            catch (Exception ex) { ShowError("获取版本列表失败", ex); }
            finally { loadingVersions = false; }
        }
        async Task InstallSelectedVersionAsync()
        {
            if (!(versionList?.SelectedItem is MinecraftVersionEntry selected))
            {
                MessageBox.Show("请先选择 Minecraft 版本。"); return;
            }
            if (installingGame)
            {
                MessageBox.Show("已有游戏安装任务正在运行。请前往下载管理查看进度。"); return;
            }
            string version = selected.Id;
            string loader = Convert.ToString(versionLoader?.SelectedItem) ?? "原版";
            installingGame = true;
            if (versionInstallButton != null) versionInstallButton.IsEnabled = false;
            if (versionCancelButton != null) versionCancelButton.IsEnabled = true;

            versionInstallJob = downloads.Enqueue("游戏", version + " · " + loader, async (token, progress, bytes) =>
            {
                try
                {
                    progress.Report("开始安装游戏及依赖：" + version + " / " + loader);
                    var profile = await modLoaders.InstallAsync(version, loader, null, progress, bytes, token);
                    token.ThrowIfCancellationRequested();
                    if (!settings.Profiles.Any(p => p.LaunchVersion == profile.LaunchVersion))
                        settings.Profiles.Add(profile);
                    activeProfile = settings.Profiles.First(p => p.LaunchVersion == profile.LaunchVersion);
                    store.Save(settings);
                    progress.Report("安装完成：" + activeProfile.Name);
                    Status("安装完成：" + activeProfile.Name);
                }
                finally
                {
                    installingGame = false;
                    if (versionInstallButton != null) versionInstallButton.IsEnabled = true;
                    if (versionCancelButton != null) versionCancelButton.IsEnabled = false;
                }
            });
            Status("已添加下载任务：" + version + " / " + loader + "，可到“下载管理”查看。");
        }

        UIElement BuildDownloadCenter()
        {
            var root = Col(Txt("下载管理", 24, true),
                Txt("Minecraft、加载器、Modrinth、整合包与 CurseForge 任务集中查看。支持进度、取消和失败重试。", 12, false, true));
            root.Children.Add(Txt("下载任务", 17, true));
            downloadList = new ListBox { ItemsSource = downloads.Jobs, Height = 285,
                ItemTemplate = (DataTemplate)FindResource("DownloadItemTemplate"),
                Margin = new Thickness(0, 6, 0, 10) };
            root.Children.Add(Card(downloadList));

            var heading = Txt("选中任务详情", 17, true);
            var status = Txt("", 14);
            status.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Status")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = "等待任务"
            });
            var detail = Txt("", 12, false, true);
            detail.SetBinding(TextBlock.TextProperty, new Binding("SelectedItem.Detail")
            {
                Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = "请选择任务"
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
            root.Children.Add(Card(Col(heading, status, detail, downloadProgressBar,
                Row(bytesLabel, speedLabel, percentLabel),
                Row(Btn("取消选中任务", () => downloads.Cancel(downloadList.SelectedItem as DownloadJob)),
                    Btn("重试失败任务", () => downloads.Retry(downloadList.SelectedItem as DownloadJob), true),
                    Btn("返回游戏下载", () => Navigate("游戏版本"))),
                Txt("速度为当前进度的估算，可能包含缓存和校验步骤；部分加载器安装步骤无法即时取消。", 12, false, true))));
            if (downloads.Jobs.Count > 0)
                downloadList.SelectedIndex = 0;
            return root;
        }

        /// <summary>
        /// Offline PCL-style layout regression. Exercise real WPF styles and
        /// navigation on the normal window and on the single-EXE payload.
        /// Never fetches online manifests, installs mods or opens sign-in.
        /// </summary>
        internal void VerifyPclLayout()
        {
            if (TopLaunchButton == null || TopDownloadButton == null ||
                TopSettingsButton == null || TopMoreButton == null ||
                PageHost == null || SideMenu == null)
                throw new InvalidOperationException("PCL 顶部导航或侧边栏控件缺失");
            if (PageScroll.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                throw new InvalidOperationException("内容页出现不允许的横向滚动");
            if (FindResource("ResourceItemTemplate") == null ||
                FindResource("VersionItemTemplate") == null ||
                FindResource("DownloadItemTemplate") == null ||
                FindResource("ServerItemTemplate") == null ||
                FindResource("LocalResourceItemTemplate") == null ||
                FindResource("CurseForgeItemTemplate") == null)
                throw new InvalidOperationException("资源、游戏版本或下载任务缺少卡片布局模板");

            Navigate("首页");
            if (homeProfile == null || PageHost.Content == null ||
                SideBar.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("首页实例选择或顶部导航未显示");

            Navigate("游戏版本");
            if (versionList == null || versionList.ItemTemplate == null ||
                SideBar.Visibility != Visibility.Visible)
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
            PageHost.Measure(new Size(900, 2200));
            PageHost.Arrange(new Rect(0, 0, 900, 1100));
            PageHost.UpdateLayout();
            if (resourceList.Items.Count != 1 ||
                resourceSource.SelectedIndex != 0 ||
                resourceMatchVersion == null)
                throw new InvalidOperationException("Modrinth 图片和多项筛选控件回归");

            Navigate("光影引擎");
            Navigate("资源包");
            Navigate("整合包");
            if (packSearchResults == null || packSearchResults.ItemTemplate == null)
                throw new InvalidOperationException("整合包列表未应用统一卡片模板");

            Navigate("下载管理");
            if (downloadList == null || downloadList.ItemTemplate == null ||
                downloadProgressBar == null)
                throw new InvalidOperationException("下载任务布局不完整");

            Navigate("多人联机");
            if (serverList == null || serverList.ItemTemplate == null ||
                lanWorldList == null || lanWorldList.ItemTemplate == null)
                throw new InvalidOperationException("服务器与局域网列表没有统一布局模板");

            Navigate("模组更新");
            if (PageHost.Content == null)
                throw new InvalidOperationException("本地资源管理页面无法打开");

            Navigate("CurseForge");
            if (PageHost.Content == null)
                throw new InvalidOperationException("CurseForge 提示与搜索入口无法打开");

            Navigate("启动器更新");
            if (PageHost.Content == null)
                throw new InvalidOperationException("启动器更新页无法打开");

            Navigate("运行日志");
            if (PageHost.Content == null)
                throw new InvalidOperationException("运行日志页无法打开");

            Navigate("设置");
            if (settingsTheme == null || settingsZoom == null ||
                settingsRoot == null || settingsNetworkMode == null ||
                settingsDownloadThreads == null || settingsMemory == null ||
                settingsUpdateChannel == null || settingsAutoCheck == null ||
                settingsAutoDownload == null)
                throw new InvalidOperationException("设置页面缺少字段或布局");

            Navigate("首页");
            if (currentPage != "首页" || PageHost.Content == null)
                throw new InvalidOperationException("导航返回首页失败");
        }

        /// <summary>
        /// Windows CI checks the real navigation/binding path that previously
        /// crashed when selecting a completed download (read-only Percent).
        /// No network or game files are touched.
        /// </summary>
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

        UIElement BuildResources(string type)
        {
            resourceType = type;
            resourceResults.Clear();
            resourcePage = 0;

            string title = type == "mod" ? "搜索 Mod" :
                type == "shader" ? "搜索光影包" : "搜索资源包";
            var root = Col(Txt(title, 24, true),
                Txt("搜索 Minecraft 社区资源，按版本与分类筛选；选择后使用原有 SHA-1 安全下载流程。", 12, false, true));

            resourceQuery = new TextBox {
                ToolTip = "输入资源名称；留空时搜索热门项目"
            };
            resourceQuery.KeyDown += async (sender, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    resourcePage = 0;
                    await SearchResourcesAsync();
                    args.Handled = true;
                }
            };

            resourceVersion = new ComboBox {
                IsEditable = true, IsTextSearchEnabled = true,
                ItemsSource = (new[] { "全部（也可自行输入）", activeProfile?.BaseVersion,
                    "1.21.11", "1.21.10", "1.21.8", "1.21.1", "1.20.1", "1.19.4" })
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray(),
                SelectedIndex = 0,
                ToolTip = "选择或输入 Minecraft Java 版本，例如 1.21.1"
            };
            resourceSource = new ComboBox {
                ItemsSource = new[] { "Modrinth", "CurseForge（独立搜索）" },
                SelectedIndex = 0,
                ToolTip = "CurseForge 使用单独的可选 MCIM 服务，并非自动合并的搜索来源"
            };
            resourceCategory = new ComboBox {
                ItemsSource = new[] {
                    "全部类型", "性能优化", "科技", "冒险", "装饰", "实用工具",
                    "世界生成", "生物", "支持库", "玩法", "装备", "存储", "管理"
                },
                SelectedIndex = 0,
                IsEnabled = type == "mod"
            };
            resourceMatchVersion = new CheckBox {
                Content = "仅显示当前游戏实例兼容资源",
                IsChecked = false,
                IsEnabled = activeProfile != null,
                ToolTip = "启用后按当前实例的 Minecraft 版本和加载器搜索"
            };
            resourceLoader = new ComboBox {
                ItemsSource = new[] { "全部加载器", "Fabric", "Forge", "NeoForge", "Quilt" },
                SelectedIndex = 0, Width = 130, IsEnabled = type == "mod",
                ToolTip = "按加载器类别筛选；勾选当前实例兼容时优先使用实例的加载器"
            };
            resourceSort = new ComboBox {
                ItemsSource = new[] { "相关度", "下载量", "最近更新", "最新发布" },
                SelectedIndex = 0, Width = 132
            };

            // 4-column search card: two rows of Name/Source and Version/Type,
            // matching the reference information hierarchy without borrowing
            // proprietary artwork or performing unsafe multi-provider merging.
            var form = new Grid { Margin = new Thickness(3, 7, 3, 5) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var labelName = Txt("名称", 13);
            labelName.Margin = new Thickness(0, 9, 5, 0);
            var labelSource = Txt("来源", 13);
            labelSource.Margin = new Thickness(4, 9, 5, 0);
            var labelVersion = Txt("版本", 13);
            labelVersion.Margin = new Thickness(0, 9, 5, 0);
            var labelCategory = Txt("类型", 13);
            labelCategory.Margin = new Thickness(4, 9, 5, 0);
            Place(form, labelName, 0, 0);
            Place(form, resourceQuery, 0, 1);
            Place(form, labelSource, 0, 2);
            Place(form, resourceSource, 0, 3);
            Place(form, labelVersion, 1, 0);
            Place(form, resourceVersion, 1, 1);
            Place(form, labelCategory, 1, 2);
            Place(form, resourceCategory, 1, 3);

            resourceSearchButton = Btn("搜索", async () =>
            {
                resourcePage = 0;
                await SearchResourcesAsync();
            }, true);
            resourceSearchButton.MinWidth = 142;
            var clearButton = Btn("重置条件", () =>
            {
                resourceSearchCancellation?.Cancel();
                resourceQuery.Text = "";
                resourceVersion.SelectedIndex = 0;
                resourceSource.SelectedIndex = 0;
                resourceCategory.SelectedIndex = 0;
                resourceLoader.SelectedIndex = 0;
                resourceSort.SelectedIndex = 0;
                resourceMatchVersion.IsChecked = false;
                resourcePage = 0;
                resourceResults.Clear();
                if (resourceCounter != null)
                    resourceCounter.Text = "条件已重置，点击搜索查看资源";
                if (resourcePrevButton != null) resourcePrevButton.IsEnabled = false;
                if (resourceNextButton != null) resourceNextButton.IsEnabled = false;
            });
            clearButton.MinWidth = 140;
            var filters = Row(resourceMatchVersion, Txt("加载器", 12, false, true),
                resourceLoader, Txt("排序", 12, false, true), resourceSort);
            var searchCard = Col(Txt(title, 17, true), form, filters,
                Row(resourceSearchButton, clearButton));
            root.Children.Add(Card(searchCard));

            resourceList = new ListBox {
                ItemsSource = resourceResults,
                ItemTemplate = (DataTemplate)FindResource("ResourceItemTemplate"),
                Height = 470, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(resourceList, ScrollBarVisibility.Disabled);
            resourceList.MouseDoubleClick += (sender, args) =>
            {
                if (resourceList.SelectedItem is ResourceHit hit)
                    ShowResourceDetails(hit);
            };
            resourceCounter = Txt("请输入名称或直接点击搜索，查看 Modrinth 项目", 12, false, true);
            resourcePrevButton = Btn("上一页", async () =>
            {
                if (resourcePage <= 0) return;
                resourcePage--;
                await SearchResourcesAsync();
            });
            resourcePrevButton.IsEnabled = false;
            resourceNextButton = Btn("下一页", async () =>
            {
                resourcePage++;
                await SearchResourcesAsync();
            });
            resourceNextButton.IsEnabled = false;
            var results = Col(resourceCounter, resourceList,
                Row(resourcePrevButton, resourceNextButton));
            root.Children.Add(Card(results));

            if (type == "shader")
                root.Children.Add(Card(Col(Txt("光影运行组件", 17, true),
                    Txt("Iris 与 Sodium 将按当前游戏实例的 Minecraft 版本及加载器匹配。"
                        + "OptiFine 必须由用户访问其官方网站下载。", 12, false, true),
                    Row(Btn("安装 Iris", async () =>
                            await InstallGraphicsModAsync("YL57xq9U", "Iris Shaders"), true),
                        Btn("安装 Sodium", async () =>
                            await InstallGraphicsModAsync("AANobbMI", "Sodium")),
                        Btn("OptiFine 官方下载", OpenOfficialOptiFine)))));

            root.Children.Add(Card(Col(Txt("资源安装", 17, true),
                Txt("单击选择资源；双击查看详情，再决定是否安装。安装流程仍会校验版本、依赖及 SHA-1。", 12, false, true),
                Row(Btn("安装选中资源", async () => await InstallResourceAsync(), true),
                    Btn("查看资源详情", () => ShowResourceDetails(resourceList?.SelectedItem as ResourceHit)),
                    Btn("打开下载管理", () => Navigate("下载管理")),
                    Btn("打开实例目录", OpenGameFolder)))));
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
                string query = resourceQuery?.Text ?? "";
                Navigate("CurseForge");
                if (cfSearchQuery != null) cfSearchQuery.Text = query;
                if (settings.McimEnabled && !string.IsNullOrWhiteSpace(query))
                    await SearchCurseForgeAsync();
                return;
            }

            var oldRequest = resourceSearchCancellation;
            oldRequest?.Cancel();
            using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            {
                resourceSearchCancellation = request;
                if (resourceSearchButton != null) resourceSearchButton.IsEnabled = false;
                if (resourceCounter != null)
                    resourceCounter.Text = "正在向 Modrinth 搜索项目...";
                Status("正在搜索 Modrinth...");
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
                    string loader = compatible ? activeProfile.Loader :
                        resourceLoader?.SelectedIndex > 0 ?
                        (resourceLoader.SelectedItem as string)?.ToLowerInvariant() : null;
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
                    using (var client = new ModrinthClient(settings))
                    {
                        var result = await client.SearchPageAsync(resourceQuery.Text,
                            resourceType, request.Token, gameVersion, loader, sort,
                            resourcePage * 20, category);
                        if (request.IsCancellationRequested ||
                            !ReferenceEquals(resourceSearchCancellation, request))
                            return;
                        resourceResults.Clear();
                        foreach (var item in result.Hits) resourceResults.Add(item);
                        int total = Math.Min(2000, result.TotalHits);
                        if (resourceCounter != null)
                            resourceCounter.Text = total == 0
                                ? "没有找到匹配的项目，请尝试清除筛选条件"
                                : "Modrinth 搜索结果  ·  第 " + (resourcePage + 1) +
                                  " 页  ·  共 " + result.TotalHits + " 项";
                        if (resourcePrevButton != null)
                            resourcePrevButton.IsEnabled = resourcePage > 0;
                        if (resourceNextButton != null)
                            resourceNextButton.IsEnabled = result.Hits.Count == 20 &&
                                (resourcePage + 1) * 20 < total;
                        Status("Modrinth 第 " + (resourcePage + 1) + " 页，"
                            + result.Hits.Count + " 个资源");
                    }
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
                    ShowError("Modrinth 搜索失败", ex);
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
            if (hit == null)
            {
                MessageBox.Show(this, "请先在搜索结果中选择一个项目。", "资源详情");
                return;
            }

            var dialog = new Window {
                Owner = this, Title = hit.Name + " · Modrinth 项目详情",
                Width = 660, Height = 560, MinWidth = 490, MinHeight = 370,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var description = Txt(hit.Description ?? "暂无项目介绍", 13, false, true);
            description.TextWrapping = TextWrapping.Wrap;
            var author = Txt("作者：" + (string.IsNullOrWhiteSpace(hit.Author)
                ? "未提供" : hit.Author), 12, false, true);
            var panel = Col(
                Txt(hit.Name, 22, true),
                Txt(hit.KindLabel + " · " + hit.SourceLabel, 12, false, true),
                description, new Separator { Margin = new Thickness(0, 12, 0, 12) },
                author,
                Txt("分类：" + (hit.CategoryLabel ?? "未知"), 12),
                Txt("加载器：" + (hit.LoaderLabel ?? "未指定"), 12),
                Txt("游戏版本：" + (hit.VersionsLabel ?? "未指定"), 12),
                Txt("下载量：" + (hit.DownloadsLabel ?? "未知"), 12),
                Txt("更新时间：" + (hit.UpdatedLabel ?? "未知"), 12));
            panel.Margin = new Thickness(21);
            var actions = Row(
                Btn("安装到当前游戏实例", async () =>
                {
                    dialog.Close();
                    await InstallResourceAsync();
                }, true),
                Btn("打开 Modrinth 项目网页", () =>
                {
                    if (string.IsNullOrWhiteSpace(hit.ProjectId) ||
                        hit.ProjectId.Length > 64 ||
                        hit.ProjectId.Any(c => !char.IsLetterOrDigit(c) &&
                            c != '-' && c != '_'))
                    {
                        MessageBox.Show(dialog, "项目 ID 不合法，无法打开官方页面。");
                        return;
                    }
                    string path = hit.Kind == "shader" ? "shader" :
                        hit.Kind == "resourcepack" ? "resourcepack" :
                        hit.Kind == "modpack" ? "modpack" : "mod";
                    try
                    {
                        Process.Start(new ProcessStartInfo(
                            "https://modrinth.com/" + path + "/" +
                            Uri.EscapeDataString(hit.ProjectId)) { UseShellExecute = true });
                    }
                    catch (Exception ex) { ShowError("打开 Modrinth 官方项目页失败", ex); }
                }),
                Btn("关闭", dialog.Close));
            panel.Children.Add(actions);
            dialog.Content = new ScrollViewer {
                Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            dialog.ShowDialog();
        }

        async Task InstallResourceAsync()
        {
            var hit = resourceList?.SelectedItem as ResourceHit;
            var profile = activeProfile;
            if (profile == null || hit == null) { MessageBox.Show("请先选择实例和资源。"); return; }
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
        UIElement MigrationPanel(string title, string details)
        {
            return Col(Txt(title, 24, true), Card(Col(Txt("迁移状态：尚未完成", 18, true),
                Txt(details, 14, false, true), Txt("为保证存档和账户安全，此处不会运行未经迁移验证的下载/安装逻辑。", 12, false, true))));
        }
        UIElement BuildCurseForge()
        {
            var root = Col(Txt("CurseForge 模组", 24, true),
                Txt("通过已授权的 MCIM 镜像查询 Minecraft 模组；列表可显示项目封面、作者、下载量与截图。"
                    + " 安装时仍会检查版本、依赖、下载权限和 SHA-1。", 12, false, true));
            if (!settings.McimEnabled)
                return Col(
                    Txt("CurseForge 需要主动启用 MCIM 社区镜像。启用后将从该镜像获取项目图片与模组信息，"
                        + "没有自动使用任何其他启动器的私有 API Key。", 13),
                    Row(Btn("确认启用 CurseForge 图文搜索", () =>
                    {
                        if (MessageBox.Show(this,
                            "MCIM 是第三方社区镜像，可能暂时不可用，缓存信息也可能晚于官方。\n" +
                            "启用后从 MCIM 查询模组；安装使用受允许的下载 URL 和 SHA-1 校验，链接不可用时停止。\n\n" +
                            "是否主动启用并保存这一设置？",
                            "启用社区模组镜像",
                            MessageBoxButton.YesNo, MessageBoxImage.Question)
                            != MessageBoxResult.Yes) return;
                        settings.McimEnabled = true;
                        try { store.Save(settings); }
                        catch (Exception ex)
                        {
                            settings.McimEnabled = false;
                            ShowError("保存 MCIM 设置失败", ex);
                            return;
                        }
                        Navigate("CurseForge");
                        Status("已启用 MCIM 图文搜索，可查询 CurseForge 模组封面和截图。");
                    }, true),
                    Btn("前往设置", () => Navigate("设置"))));

            cfSearchQuery = new TextBox { Width = 325,
                ToolTip = "输入模组名称（支持中英文），留空可查询热门模组" };
            cfSearchQuery.KeyDown += async (sender, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    args.Handled = true;
                    await SearchCurseForgeAsync();
                }
            };
            cfSearchStatus = Txt("搜索模组后，点击列表可预览模组封面和相关截图。", 12, false, true);
            cfSearchStatus.Margin = new Thickness(0, 4, 0, 4);
            root.Children.Add(Card(Col(
                Txt("搜索 Minecraft 模组", 18, true),
                Row(cfSearchQuery, Btn("搜索模组", async () => await SearchCurseForgeAsync(), true),
                    Btn("清空关键词", () => cfSearchQuery.Text = "")),
                cfSearchStatus)));

            cfSearchResults = new ListBox
            {
                Height = 390,
                ItemTemplate = (DataTemplate)FindResource("CurseForgeItemTemplate"),
                ToolTip = "双击模组查看原作者提供的游戏截图"
            };
            cfSearchResults.MouseDoubleClick += (sender, args) => ShowCurseForgeDetails();
            root.Children.Add(Card(Col(Txt("搜索结果 · 图文列表", 18, true), cfSearchResults)));

            root.Children.Add(Card(Col(Txt("模组详情与安装", 18, true),
                Row(Btn("查看模组封面与截图", ShowCurseForgeDetails),
                    Btn("安装选中模组", async () => await InstallCurseForgeAsync(), true),
                    Btn("打开下载任务", () => Navigate("下载管理"))),
                Txt("截图来自模组原始项目元数据，不会伪造模组实际游戏画面；部分项目未上传图片。"
                    + " 具体兼容性由安装步骤再次检查。", 12, false, true))));
            return root;
        }

        void ShowCurseForgeDetails()
        {
            var hit = cfSearchResults?.SelectedItem as CurseForgeHit;
            if (hit == null)
            {
                Status("请先从 CurseForge 搜索列表中选择一个模组。");
                return;
            }
            var page = Col(Txt(hit.Name, 23, true),
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
                page.Children.Add(cover);
            }
            page.Children.Add(Txt("作者提供的模组截图", 17, true));
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
            if (gallery.Children.Count == 0)
                page.Children.Add(Txt("项目暂未提供可用的安全截图链接。", 12, false, true));
            else page.Children.Add(gallery);

            if (!string.IsNullOrEmpty(hit.OfficialUrl))
                page.Children.Add(Btn("在 CurseForge 官方页面查看完整详情", () =>
                    Process.Start(new ProcessStartInfo(hit.OfficialUrl) { UseShellExecute = true })));
            page.Children.Add(Txt("所显示的图标与截图来自项目元数据，不代表兼容性已验证。", 12, false, true));
            var dialog = new Window
            {
                Owner = this, Title = "模组预览 · " + hit.Name,
                Width = 820, Height = 630, MinWidth = 540, MinHeight = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new ScrollViewer
                {
                    Padding = new Thickness(19),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = page
                }
            };
            dialog.ShowDialog();
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
                results.ItemsSource = found;
                if (label != null)
                    label.Text = found.Count == 0 ? "未找到符合条件的模组，试试其他关键词。" :
                        "找到 " + found.Count + " 个模组；点击选择，双击或按详情按钮查看截图。";
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

        async Task InstallCurseForgeAsync()
        {
            var hit = cfSearchResults?.SelectedItem as CurseForgeHit;
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
            var root = Col(Txt("Modrinth 整合包", 24, true),
                Txt("支持搜索、下载和导入 .mrpack；使用独立实例，验证路径与 SHA-1 / SHA-512。", 12, false, true));
            packSearchQuery = new TextBox { Width = 320 };
            root.Children.Add(Card(Col(Txt("在线搜索", 18, true),
                Row(packSearchQuery, Btn("搜索 Modrinth", async () => await SearchModpacksAsync(), true)))));
            packSearchResults = new ListBox { Height = 330,
                ItemTemplate = (DataTemplate)FindResource("ResourceItemTemplate") };
            root.Children.Add(Card(packSearchResults));
            root.Children.Add(Card(Col(Txt("整合包安装", 18, true),
                Row(Btn("安装选中整合包", async () => await InstallSelectedModpackAsync(), true),
                    Btn("导入本地 .mrpack", ImportMrpackFromDisk),
                    Btn("下载管理", () => Navigate("下载管理")),
                    Btn("取消本地导入", () => currentOperation?.Cancel())),
                Txt("安装新实例不会覆盖已存在的存档；如果加载器版本或下载文件不可用，安装会停止并移除未完成的新实例。", 12, false, true),
                Txt("CurseForge 整合包直装仍未迁移。", 12, false, true))));
            return root;
        }
        async Task SearchModpacksAsync()
        {
            try
            {
                Status("搜索 Modrinth 整合包...");
                using (var client = new ModrinthClient(settings))
                    packSearchResults.ItemsSource = await client.SearchAsync(packSearchQuery.Text, "modpack", CancellationToken.None);
                Status("已获取整合包搜索结果");
            }
            catch (Exception ex) { ShowError("整合包搜索失败", ex); }
        }
        async Task InstallSelectedModpackAsync()
        {
            var hit = packSearchResults?.SelectedItem as ResourceHit;
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
                    activeProfile = profile;
                    store.Save(settings);
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
            activeProfile = profile;
            store.Save(settings);
            Status("已创建整合包实例：" + profile.Name);
            Navigate("首页");
        }


        UIElement BuildModUpdates()
        {
            var root = Col(Txt("本地模组与资源管理", 24, true),
                Txt("管理当前实例的模组、光影和资源包；仅操作用户选择的文件，不自动修改其他 JAR。在线模组版本更新仍在迁移中。", 12, false, true));
            if (activeProfile == null) return Col(Txt("请先在首页选择游戏实例。"), Btn("返回首页", () => Navigate("首页")));
            root.Children.Add(Txt("当前实例：" + activeProfile.Name));
            localResourceKind = new ComboBox { ItemsSource = new[] { "模组", "光影", "资源包" }, SelectedIndex = 0,
                Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
            localResourceKind.SelectionChanged += (sender, e) => RefreshLocalResourceList();
            root.Children.Add(Row(Txt("类型"), localResourceKind));
            localResourceList = new ListBox { Height = 370, Margin = new Thickness(0, 8, 0, 14),
                ItemTemplate = (DataTemplate)FindResource("LocalResourceItemTemplate") };
            root.Children.Add(Card(localResourceList));
            root.Children.Add(Card(Col(Txt("本地操作", 18, true),
                Row(Btn("导入文件", ImportLocalResource, true),
                    Btn("停用选中", () => ToggleLocalResource(false)),
                    Btn("启用选中", () => ToggleLocalResource(true))),
                Row(Btn("刷新列表", RefreshLocalResourceList),
                    Btn("打开实例目录", OpenGameFolder)),
                Txt("离线管理不等同于在线更新：自动版本匹配和更新前备份仍待完成。", 12, false, true))));
            modUpgradeResults = new ListBox { Height = 210, Margin = new Thickness(0, 8, 0, 10) };
            root.Children.Add(Card(Col(Txt("Modrinth 已登记资源更新", 18, true),
                Btn("检查 Modrinth 更新", async () => await CheckManagedUpdatesAsync(), true),
                modUpgradeResults,
                Row(Btn("更新选中", async () => await ApplyManagedUpdatesAsync(false)),
                    Btn("更新全部兼容资源", async () => await ApplyManagedUpdatesAsync(true))),
                Txt("更新前备份；跳过旧文件哈希不符或未登记的资源。", 12, false, true))));
            RefreshLocalResourceList();
            return root;
        }
        async Task CheckManagedUpdatesAsync()
        {
            if (activeProfile == null || modUpgradeResults == null) return;
            if (currentOperation != null) { MessageBox.Show("已有安装操作进行中"); return; }
            currentOperation = new CancellationTokenSource();
            try
            {
                Status("正在检查已登记的 Modrinth 资源更新...");
                var updates = await new ManagedModUpdateService(settings).CheckAsync(activeProfile, currentOperation.Token);
                modUpgradeResults.ItemsSource = updates;
                Status("发现兼容更新：" + updates.Count + " 项");
            }
            catch (OperationCanceledException) { Status("更新检查已取消"); }
            catch (Exception ex) { ShowError("Modrinth 更新检查失败", ex); }
            finally { currentOperation.Dispose(); currentOperation = null; }
        }
        async Task ApplyManagedUpdatesAsync(bool all)
        {
            if (activeProfile == null || modUpgradeResults == null) return;
            if (currentOperation != null) { MessageBox.Show("已有安装操作进行中"); return; }
            var updates = all
                ? modUpgradeResults.Items.OfType<ResourceUpgrade>().ToList()
                : new[] { modUpgradeResults.SelectedItem as ResourceUpgrade }.Where(x => x != null).ToList();
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
                    await manager.ApplyAsync(activeProfile, item, currentOperation.Token);
                }
                Status("Modrinth 资源更新完成：" + updates.Count + " 项");
                modUpgradeResults.ItemsSource = null;
                RefreshLocalResourceList();
            }
            catch (OperationCanceledException) { Status("模组更新已取消"); }
            catch (Exception ex) { ShowError("资源更新失败", ex); }
            finally { currentOperation.Dispose(); currentOperation = null; }
        }
        void RefreshLocalResourceList()
        {
            if (localResourceList == null || localResourceKind == null || activeProfile == null) return;
            try { localResourceList.ItemsSource = InstanceResourceService.List(activeProfile, Convert.ToString(localResourceKind.SelectedItem)); }
            catch (Exception ex) { ShowError("读取本地资源失败", ex); }
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
                Status("导入完成：" + Path.GetFileName(added)); RefreshLocalResourceList();
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
                Status((enabled ? "已启用：" : "已停用：") + item.Name); RefreshLocalResourceList();
            }
            catch (Exception ex) { ShowError("操作本地资源失败", ex); }
        }

        UIElement BuildMultiplayer()
        {
            var root = Col(Txt("多人联机", 24, true), Txt("服务器收藏 · 可选 TCP 连通性检查 · 复制地址。游戏内仍需选择多人游戏连接。", 12, false, true));
            serverList = new ListBox { Height = 270, Margin = new Thickness(0, 8, 0, 10),
                ItemTemplate = (DataTemplate)FindResource("ServerItemTemplate") };
            root.Children.Add(Card(serverList));
            serverName = new TextBox { Width = 170, ToolTip = "收藏名称" };
            serverAddress = new TextBox { Width = 310, ToolTip = "域名或 IP:端口" };
            root.Children.Add(Card(Col(Txt("添加或编辑服务器", 17, true),
                Row(Txt("名称"), serverName, Txt("地址"), serverAddress),
                Row(Btn("保存服务器", SaveServer, true),
                    Btn("删除选中", DeleteServer),
                    Btn("复制地址", CopyServerAddress)),
                Row(Btn("检查连通性", async () => await CheckServerAsync()),
                    Btn("连接服务器", async () => await JoinServerAsync(), true),
                    Btn("启动当前实例", async () => await LaunchAsync())),
                Txt("连通性仅检查 TCP 端口，不能保证游戏版本/模组/正版要求匹配。", 12, false, true))));
            lanWorldList = new ListBox { Height = 165,
                ItemTemplate = (DataTemplate)FindResource("ServerItemTemplate") };
            root.Children.Add(Card(Col(Txt("局域网世界", 18, true),
                Txt("接收本地网络的 Minecraft UDP 多播公告（224.0.2.60:4445），不扫描其他设备端口。", 12, false, true),
                Row(Btn("扫描局域网", async () => await ScanLanWorldsAsync()),
                    Btn("连接局域网世界", async () => await JoinLanWorldAsync(), true)),
                lanWorldList)));
            serverList.SelectionChanged += (sender, args) =>
            {
                if (serverList.SelectedItem is ServerEntry selected)
                {
                    serverName.Text = selected.Name; serverAddress.Text = selected.Address;
                }
            };
            RefreshServers();
            return root;
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
            try
            {
                Status("正在扫描 Minecraft 局域网公告...");
                var worlds = await multiplayer.ScanLanAsync(CancellationToken.None);
                if (lanWorldList != null) lanWorldList.ItemsSource = worlds;
                Status("局域网发现 " + worlds.Count + " 个世界；如无结果，请检查路由器和防火墙。");
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
            var root = Col(Txt("启动器更新", 24, true),
                Txt("PCL 风格：自动检测、校验后安装，或直接打开已下载的新版本试用。"
                    + "稳定版和测试版独立，默认不强制下载或安装。", 12, false, true));

            root.Children.Add(Card(Col(
                Txt("当前版本 · " + LauncherAutoUpdateService.CurrentTag, 18, true),
                Txt("Windows x64 · C# / WPF · .NET Framework 4.8 · " +
                    (LauncherAutoUpdateService.InstallMode == "onefile" ?
                        "单文件 EXE 模式" : "完整 ZIP 文件夹模式"), 12, false, true),
                Txt("检查来源：github.com/minecraftlu/BlockCraftLauncher。"
                    + " 更新内容和校验清单必须来自同一个官方 GitHub Release。", 12, false, true))));

            updateStatusText = Txt("", 14, true);
            updateProgressBar = new ProgressBar
            {
                Height = 9, Minimum = 0, Maximum = 100,
                Margin = new Thickness(0, 6, 0, 13),
                Visibility = Visibility.Collapsed
            };
            updateProgressBar.SetResourceReference(ProgressBar.ForegroundProperty, "AccentBrush");
            updateNotesText = Txt("", 12, false, true);
            updateNotesText.MaxHeight = 190;
            updateNotesText.TextWrapping = TextWrapping.Wrap;
            updateCheckButton = Btn("立即检查新版本", async () =>
                await CheckLauncherUpdatesAsync(true), true);
            updateInstallButton = Btn("下载并安装 · 完成后自动打开新版", async () =>
                await InstallLauncherUpdateAsync(), true);
            updateDirectOpenButton = Btn("直接打开新版（不覆盖旧版）", async () =>
                await OpenDownloadedUpdateDirectlyAsync());
            root.Children.Add(Card(Col(
                Txt("更新状态", 18, true),
                updateStatusText,
                updateProgressBar,
                updateNotesText,
                Row(updateCheckButton, updateInstallButton,
                    updateDirectOpenButton,
                    Btn("打开官方发布页", () => LauncherReleaseService.OpenOfficialRelease(
                        "https://github.com/minecraftlu/BlockCraftLauncher/releases"))),
                Txt("“直接打开”会先下载并校验新版，从临时目录试用；旧安装保留。"
                    + "正式覆盖更新请使用“下载并安装”，完成后会自动打开新版窗口。", 12, false, true))));
            root.Children.Add(Card(Col(Txt("自动更新设置", 18, true),
                Txt("设置 → 自动更新，可切换稳定版/预览版通道，开启后台检查或自动下载。"
                    + "安装前会要求退出当前启动器，Minecraft 游戏进程不会被强制关闭。", 12, false, true),
                Row(Btn("修改自动更新设置", () => Navigate("设置")),
                    Btn("查看构建记录", () => LauncherReleaseService.OpenOfficialRelease(
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
                        " · " + (settings.UpdateChannel == "preview" ? "预览测试渠道" : "稳定渠道") +
                        "。更新会校验完整性，安装前确认退出启动器。";
            }
            if (currentPage == "启动器更新" && updateStatusText != null)
            {
                updateStatusText.Text = updatePreparing ?
                    "正在下载并校验更新..." :
                    updateChecking ? "正在连接 GitHub 检查更新..." :
                    preparedUpdate != null ? "更新已下载并通过启动校验，准备安装：" +
                        preparedUpdate.Offer.Tag :
                    available ? "发现可用更新：" + offeredUpdate.Tag :
                        "当前通道没有已确认的更高版本。";

                updateNotesText.Text = available ?
                    "通道：" + (settings.UpdateChannel == "preview" ? "预览版" : "正式稳定版") +
                    "  ·  来源：GitHub Releases\n" +
                    (offeredUpdate.Description ?? "") :
                    "当前通道：" + (settings.UpdateChannel == "preview" ?
                        "预览版（只接收 BlockCraft WPF RC 测试包）" :
                        "稳定版（不自动安装 Alpha、RC 或网易联机实验包）") +
                    "。\n若只有测试版发布，可以在设置中主动切换更新通道。";
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
            RefreshLauncherUpdatePanel();
            try
            {
                var candidate = await autoUpdater.CheckAsync(updateCancellation.Token);
                if (updateCancellation.IsCancellationRequested) return;
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
                "\n下次仍从旧安装位置启动。需要永久升级请点击“下载并安装”。",
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
                Status("正在安装已校验的 " + preparedUpdate.Offer.Tag +
                    "；旧窗口即将关闭，完成后自动打开新版。");
                Application.Current.Shutdown();
            }
            catch (Exception ex) { ShowError("无法启动独立更新程序", ex); }
        }

        // Shared two-column settings rows, matching the PCL2-style label /
        // value rhythm. Kept in WPF so it works across light/dark themes.
        static Grid SettingsForm()
        {
            var form = new Grid { Margin = new Thickness(2, 7, 0, 0) };
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(164) });
            form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return form;
        }

        static void SettingRow(Grid form, string name, FrameworkElement editor, string description = null)
        {
            int row = form.RowDefinitions.Count;
            form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = Txt(name, 13);
            title.VerticalAlignment = VerticalAlignment.Top;
            title.Margin = new Thickness(0, 9, 13, 14);
            Grid.SetRow(title, row);
            Grid.SetColumn(title, 0);
            form.Children.Add(title);

            var right = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
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
            Grid.SetRow(right, row);
            Grid.SetColumn(right, 1);
            form.Children.Add(right);
        }

        UIElement BuildSettings()
        {
            var root = Col(Txt("启动器设置", 24, true),
                Txt("个人设置保存在 BlockCraft 独立配置文件中，不覆盖原 Python 启动器。", 12, false, true));

            var appearance = SettingsForm();
            settingsTheme = new ComboBox { ItemsSource = new[] { "深色", "浅色" },
                SelectedItem = settings.Theme, MaxWidth = 240 };
            settingsTheme.SelectionChanged += (sender, args) =>
            {
                settings.Theme = Convert.ToString(settingsTheme.SelectedItem);
                ApplyTheme();
            };
            SettingRow(appearance, "界面主题", settingsTheme, "使用系统原生字体、按钮和统一导航符号");

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
            SettingRow(appearance, "界面缩放", zoomRow, "80%～140%；调整后主页面和侧边栏同步缩放");
            root.Children.Add(Card(Col(Txt("外观", 18, true), appearance)));

            var game = SettingsForm();
            settingsRoot = new TextBox { Text = settings.Root, ToolTip = "Minecraft 根目录（含 versions、libraries 等）" };
            SettingRow(game, "游戏根目录", settingsRoot, "独立实例可使用自己的游戏文件夹");
            settingsJava = new TextBox { Text = settings.JavaPath, ToolTip = "留空时由启动组件自动选择 Java" };
            SettingRow(game, "Java 路径", settingsJava, "留空则由 Minecraft 启动组件自动查找运行环境");
            settingsPlayer = new TextBox { Text = settings.OfflineName, MaxWidth = 240 };
            SettingRow(game, "离线玩家名", settingsPlayer, "3～16 位字母、数字或下划线；正版账号不受影响");
            settingsMemory = new TextBox { Text = settings.MemoryMb.ToString(), MaxWidth = 180 };
            SettingRow(game, "游戏内存（MB）", settingsMemory, "例如 4096，启动时至少分配 512 MB");
            root.Children.Add(Card(Col(Txt("游戏与账号", 18, true), game)));

            var network = SettingsForm();
            var routes = new[]
            {
                new KeyValuePair<string, string>("auto", "自动测速 · 智能择快"),
                new KeyValuePair<string, string>("china", "中国大陆优先 · 镜像失败回官方"),
                new KeyValuePair<string, string>("official", "仅官方 · 不使用镜像"),
                new KeyValuePair<string, string>("mirror", "镜像优先 · 保留官方回退")
            };
            settingsNetworkMode = new ComboBox {
                ItemsSource = routes,
                DisplayMemberPath = "Value",
                SelectedItem = routes.FirstOrDefault(x => x.Key == settings.NetworkMode),
                MaxWidth = 340 };
            if (settingsNetworkMode.SelectedIndex < 0) settingsNetworkMode.SelectedIndex = 0;
            SettingRow(network, "下载线路", settingsNetworkMode,
                "国内优先支持 Mojang/BMCLAPI；可选 MCIM 仅用于获准的社区资源，不会更改微软登录通道。");
            settingsDownloadThreads = new TextBox { Text = settings.DownloadThreads.ToString(), MaxWidth = 130 };
            SettingRow(network, "并发连接", settingsDownloadThreads, "2～32 个连接");
            settingsDownloadRetries = new TextBox { Text = settings.DownloadRetries.ToString(), MaxWidth = 130 };
            SettingRow(network, "失败重试", settingsDownloadRetries, "1～6 次");
            settingsProxy = new TextBox { Text = settings.Proxy, ToolTip = "HTTP/HTTPS 代理地址，可留空" };
            SettingRow(network, "HTTP 代理", settingsProxy);
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
                "MCIM 非官方服务，启用前请确认接入权限并遵守作者许可");
            root.Children.Add(Card(Col(Txt("下载与网络", 18, true), network)));

            var updates = SettingsForm();
            var channels = new[]
            {
                new KeyValuePair<string, string>("stable", "稳定版 · 仅正式 GitHub Releases"),
                new KeyValuePair<string, string>("preview", "预览版 · BlockCraft WPF RC 测试包")
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
                "默认稳定版；预览版可能包含尚未完成实机测试的新功能");
            settingsAutoCheck = new CheckBox
            {
                IsChecked = settings.AutoCheckUpdates,
                Content = "启动后后台自动检查新版本（最多每 6 小时一次）"
            };
            SettingRow(updates, "自动检测", settingsAutoCheck,
                "仅从官方 GitHub Releases 读取公开版本信息，不上传账号数据");
            settingsAutoDownload = new CheckBox
            {
                IsChecked = settings.AutoDownloadUpdates,
                Content = "发现新版本时自动下载并校验，等我确认后再安装"
            };
            SettingRow(updates, "自动下载", settingsAutoDownload,
                "关闭为默认设置。即使启用自动下载，也不会自动结束游戏或强制重启。");
            root.Children.Add(Card(Col(Txt("启动器自动更新", 18, true),
                updates,
                Row(Btn("前往更新中心", () => Navigate("启动器更新"))))));

            root.Children.Add(Row(
                Btn("保存设置", SaveSettings, true),
                Btn("恢复 100% 缩放", () => { settingsZoom.Value = 100; }),
                Btn("打开游戏目录", OpenGameFolder)));
            return root;
        }
        void SaveSettings()
        {
            settings.Root = settingsRoot?.Text ?? settings.Root;
            settings.Proxy = settingsProxy?.Text ?? settings.Proxy;
            if (settingsNetworkMode?.SelectedItem is KeyValuePair<string, string> selectedRoute)
                settings.NetworkMode = selectedRoute.Key;
            if (int.TryParse(settingsDownloadThreads?.Text, out var threads))
                settings.DownloadThreads = Math.Max(2, Math.Min(32, threads));
            if (int.TryParse(settingsDownloadRetries?.Text, out var retries))
                settings.DownloadRetries = Math.Max(1, Math.Min(6, retries));
            settings.McimEnabled = settingsMcim?.IsChecked == true;
            settings.AutoCheckUpdates = settingsAutoCheck?.IsChecked == true;
            settings.AutoDownloadUpdates = settingsAutoDownload?.IsChecked == true;
            if (settingsUpdateChannel?.SelectedItem is KeyValuePair<string, string> selectedUpdateChannel &&
                settings.UpdateChannel != selectedUpdateChannel.Key)
            {
                settings.UpdateChannel = selectedUpdateChannel.Key;
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
            return Col(Txt("运行日志", 24, true), Card(box), Btn("刷新日志", () => Navigate("运行日志")));
        }
        void EditOfflineAccount()
        {
            var dialog = new Window { Owner = this, Title = "离线玩家设置", Width = 380, Height = 210, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = (Brush)Application.Current.Resources["PanelBrush"] };
            var body = Col(Txt("离线玩家名（3–16 位字母/数字/下划线）"));
            var entry = new TextBox { Text = settings.OfflineName }; body.Children.Add(entry);
            body.Children.Add(Btn("确认", () => { if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Text, "^[A-Za-z0-9_]{3,16}$")) { MessageBox.Show("玩家名不合法"); return; }
                settings.OfflineName = entry.Text; settings.AccountMode = "offline"; store.Save(settings); dialog.Close(); BuildSidebar(); Navigate("首页"); }, true));
            dialog.Content = new Border { Padding = new Thickness(18), Child = body }; dialog.ShowDialog();
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
                store.Save(settings); BuildSidebar(); Navigate("首页");
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
                store.Save(settings); BuildSidebar(); Navigate("首页");
            }
            catch (Exception ex) { ShowError("退出账户失败", ex); }
        }
        void RefreshGameProcessStatus()
        {
            if (currentPage != "首页" || gameProcessText == null) return;
            var profile = activeProfile;
            if (profile == null)
            {
                gameProcessText.Text = "运行状态：未选择游戏实例";
                if (homeLaunchButton != null) homeLaunchButton.IsEnabled = false;
                return;
            }

            var state = gameProcesses.GetState(profile);
            bool launching = launchingProfiles.Contains(profile);
            bool running = state.Status == GameProcessStatus.Running;
            if (homeLaunchButton != null) homeLaunchButton.IsEnabled = !launching && !running;
            if (launching)
                gameProcessText.Text = "运行状态：正在准备或启动游戏...";
            else if (running)
                gameProcessText.Text = "运行状态：游戏正在运行（本启动器启动，PID " +
                    state.ProcessId + "）";
            else if (state.Status == GameProcessStatus.Exited)
                gameProcessText.Text = "运行状态：游戏已退出（PID " + state.ProcessId
                    + "，退出码 " + (state.ExitCode?.ToString() ?? "未知") + "）";
            else if (state.Status == GameProcessStatus.Unavailable)
                gameProcessText.Text = "运行状态：进程信息暂不可读取，无法判断是否退出";
            else
                gameProcessText.Text = "运行状态：尚未由本次启动器会话启动；外部游戏运行状态未知";
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
        void ManageInstance()
        {
            if (activeProfile == null)
            {
                MessageBox.Show("请先选择游戏实例");
                return;
            }
            var selected = activeProfile;
            var window = new Window {
                Owner = this, Title = "游戏实例与存档管理",
                Width = 525, Height = 430, MinWidth = 460, MinHeight = 350,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var actions = new StackPanel { Margin = new Thickness(20) };
            actions.Children.Add(Txt("实例管理 · " + selected.Name, 19, true));
            actions.Children.Add(Txt("以下操作只针对当前选择的 Minecraft 游戏实例。"
                + "恢复备份不会覆盖已有世界，移除实例仅修改列表，不删除文件。", 12, false, true));
            actions.Children.Add(Btn("打开实例目录", () => LocalInstanceTools.OpenFolder(selected)));
            actions.Children.Add(Btn("备份当前实例的存档", async () =>
            {
                var file = new SaveFileDialog {
                    FileName = "saves-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
                    Filter = "ZIP 压缩包|*.zip"
                };
                if (file.ShowDialog(window) != true) return;
                try
                {
                    Status("正在备份存档，请勿关闭启动器...");
                    string path = await Task.Run(() => LocalInstanceTools.BackupSaves(selected, file.FileName));
                    MessageBox.Show(window, "存档备份完成：\n" + path, "备份成功");
                    Status("存档备份完成");
                }
                catch (Exception ex) { ShowError("备份失败", ex); }
            }, true));
            actions.Children.Add(Btn("从 ZIP 安全恢复存档（不覆盖已有世界）", async () =>
            {
                var file = new OpenFileDialog {
                    Filter = "ZIP 存档备份 (*.zip)|*.zip",
                    Title = "选择从 saves 文件夹创建的备份 ZIP"
                };
                if (file.ShowDialog(window) != true) return;
                if (MessageBox.Show(window,
                    "将把 ZIP 中的新世界恢复到当前实例。\n"
                    + "已有同名世界将被保留，恢复会直接拒绝覆盖。\n\n确认继续？",
                    "安全恢复存档", MessageBoxButton.YesNo, MessageBoxImage.Question)
                    != MessageBoxResult.Yes) return;
                try
                {
                    Status("正在检查 ZIP 并恢复新世界...");
                    int worlds = await Task.Run(() => LocalInstanceTools.RestoreSaves(selected, file.FileName));
                    MessageBox.Show(window, "成功恢复 " + worlds + " 个世界。\n已有存档未被覆盖。",
                        "恢复完成");
                    Status("已安全恢复存档：" + worlds + " 个世界");
                }
                catch (Exception ex) { ShowError("恢复失败", ex); }
            }));
            actions.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 12) });
            actions.Children.Add(Btn("仅从列表移除实例（保留游戏与存档文件）", () =>
            {
                if (MessageBox.Show(window,
                    "仅从 BlockCraft 启动器列表中移除“" + selected.Name + "”？\n\n"
                    + "不会删除 Minecraft 游戏目录、已安装模组或任何存档。",
                    "确认移除实例", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return;
                settings.Profiles.Remove(selected);
                activeProfile = settings.Profiles.FirstOrDefault();
                try
                {
                    store.Save(settings);
                    window.Close();
                    BuildSidebar();
                    Navigate("首页");
                    Status("已从列表移除实例（没有删除任何游戏文件）");
                }
                catch (Exception ex)
                {
                    settings.Profiles.Add(selected);
                    activeProfile = selected;
                    ShowError("更新实例列表失败", ex);
                }
            }));
            window.Content = new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = actions
            };
            window.ShowDialog();
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
        void ShowReleaseNotes()
        {
            MessageBox.Show("BlockCraft Launcher v0.9.30 · 自动更新后打开新版窗口\n\n"
                + "· 继续保留每六小时自动检查、预览/正式版通道及 SHA-256 校验\n"
                + "· 用户确认安装后，旧启动器自动退出并交给独立更新助手\n"
                + "· 成功替换并通过启动自检后，自动运行真正的新版本 EXE\n"
                + "· 新版 WPF 主窗口完成渲染后向更新助手发送一次性确认\n"
                + "· 自动将新版窗口带到前台，并在状态栏提示更新完成\n"
                + "· 单 EXE 支持从外层解包器等待到内层 WPF 真实窗口显示\n"
                + "· 更新中心新增“直接打开新版”，已校验后从临时目录试用，不覆盖现有安装\n"
                + "· 如果不能确认窗口已经显示，会说明原因和手动打开路径\n"
                + "· 维持更新失败回滚，保留游戏、账号与存档不变\n\n"
                + "本次窗口自动显示已加入 Windows 离线界面回归。"
                + " 正式 Stable 发布仍需真实在线升级的终端验收。",
                "BlockCraft Launcher 更新说明");
        }
        void ShowError(string title, Exception ex) { Log(title + "：" + ex); MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
