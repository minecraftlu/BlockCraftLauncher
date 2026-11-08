using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow : Window
    {
        readonly SettingsStore store = new SettingsStore();
        readonly LauncherConfig settings;
        readonly MinecraftBackend minecraft;
        MicrosoftAccountService microsoft;
        MicrosoftAccountService GetMicrosoftService() => microsoft ?? (microsoft = new MicrosoftAccountService());
        readonly ModLoaderInstaller modLoaders;
        readonly ObservableCollection<string> gameVersions = new ObservableCollection<string>();
        readonly ObservableCollection<ResourceHit> resourceResults = new ObservableCollection<ResourceHit>();
        readonly Dictionary<string, Button> sidebarButtons = new Dictionary<string, Button>();
        readonly List<string> logs = new List<string>();
        readonly MultiplayerService multiplayer = new MultiplayerService();
        ComboBox localResourceKind;
        ListBox localResourceList, serverList;
        TextBox serverName, serverAddress;
        GameProfile activeProfile;
        string currentPage = "首页";
        ComboBox homeProfile, versionLoader, settingsTheme;
        Button versionInstallButton, versionCancelButton;
        TextBox versionQuery, resourceQuery, settingsRoot, settingsProxy, settingsJava, settingsPlayer, settingsMemory;
        ListBox versionList, resourceList;
        TextBlock accountText, zoomValue;
        Slider settingsZoom;
        string resourceType = "mod";
        CancellationTokenSource currentOperation;
        bool loadingVersions;

        public MainWindow()
        {
            InitializeComponent();
            settings = store.Load();
            minecraft = new MinecraftBackend(settings);
            modLoaders = new ModLoaderInstaller(settings);
            activeProfile = settings.Profiles.FirstOrDefault();
            ApplyTheme(); ApplyZoom(); BuildSidebar(); Navigate("首页");
            Log("已读取旧版本配置；WPF 预览设置将单独保存，不覆盖原 Python 启动器配置。");
        }

        static TextBlock Txt(string text, double size = 14, bool bold = false, bool muted = false)
        {
            var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9) };
            if (muted) label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            return label;
        }
        static Button Btn(string caption, Action action, bool accent = false)
        {
            var b = new Button { Content = caption, MinHeight = 38 };
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
        static StackPanel Row(params UIElement[] children)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var c in children) p.Children.Add(c); return p;
        }
        static void AddNavSection(Panel sidebar, string heading, IEnumerable<string> names, Action<string> navigate, Dictionary<string, Button> buttonMap)
        {
            sidebar.Children.Add(Txt(heading, 12, true, true));
            foreach (var name in names)
            {
                var btn = Btn(name, () => navigate(name));
                btn.HorizontalContentAlignment = HorizontalAlignment.Left;
                btn.Margin = new Thickness(0, 0, 0, 5);
                sidebar.Children.Add(btn); buttonMap[name] = btn;
            }
            sidebar.Children.Add(new Separator { Margin = new Thickness(0, 9, 0, 12) });
        }
        void BuildSidebar()
        {
            SideMenu.Children.Clear(); sidebarButtons.Clear();
            AddNavSection(SideMenu, "游戏", new[] { "首页", "游戏版本" }, Navigate, sidebarButtons);
            AddNavSection(SideMenu, "资源下载", new[] { "资源中心", "光影引擎", "整合包" }, Navigate, sidebarButtons);
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
            currentPage = page; SideBar.Visibility = page == "首页" ? Visibility.Collapsed : Visibility.Visible;
            SideColumn.Width = page == "首页" ? new GridLength(0) : new GridLength(218);
            foreach (var pair in sidebarButtons)
            {
                pair.Value.FontWeight = pair.Key == page ? FontWeights.Bold : FontWeights.Normal;
                pair.Value.SetResourceReference(Control.BackgroundProperty, pair.Key == page ? "AccentBrush" : "PanelAltBrush");
                pair.Value.SetResourceReference(Control.ForegroundProperty, pair.Key == page ? "AccentTextBrush" : "TextBrush");
            }
            switch (page)
            {
                case "首页": PageHost.Content = BuildHome(); break;
                case "游戏版本": PageHost.Content = BuildVersions(); break;
                case "资源中心": PageHost.Content = BuildResources("mod"); break;
                case "光影引擎": PageHost.Content = BuildResources("shader"); break;
                case "整合包": PageHost.Content = BuildModpacks(); break;
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
        void Log(string text) { logs.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + text); }
        void ApplyTheme()
        {
            bool light = settings.Theme == "浅色";
            // XAML Freezable resources may be frozen after loading. Replace the brush so
            // DynamicResource consumers update without mutating a frozen object.
            void Set(string key, string value) { Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
            Set("WindowBrush", light ? "#EFF3F8" : "#141414");
            Set("PanelBrush", light ? "#FFFFFF" : "#202020");
            Set("PanelAltBrush", light ? "#F2F7FD" : "#292929");
            Set("TextBrush", light ? "#203348" : "#F7F8FA");
            Set("MutedBrush", light ? "#64788C" : "#A8ADB8");
            Set("BorderBrushTheme", light ? "#D9E4F0" : "#34383E");
            Set("AccentBrush", IsHexColor(settings.Accent) ? settings.Accent : "#1875CE");
            Set("AccentTextBrush", "#FFFFFF");
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
            if (WindowViewport.ActualWidth > 0) Shell.Width = Math.Max(960, (WindowViewport.ActualWidth - 22) / scale);
            if (WindowViewport.ActualHeight > 0) Shell.Height = Math.Max(640, (WindowViewport.ActualHeight - 22) / scale);
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
            var root = Col();
            var nav = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            for (int i = 0; i < 4; i++) nav.ColumnDefinitions.Add(new ColumnDefinition());
            var navItems = new[] { ("▷\n启动", "首页"), ("⇩\n下载", "游戏版本"), ("⚙\n设置", "设置"), ("▦\n更多", "模组更新") };
            for (int i = 0; i < navItems.Length; i++)
            {
                var item = navItems[i]; var b = Btn(item.Item1, () => Navigate(item.Item2), i == 0);
                b.Height = 80; b.FontSize = 17; b.FontWeight = FontWeights.Bold; Grid.SetColumn(b, i); nav.Children.Add(b);
            }
            root.Children.Add(Card(nav));
            var grid = DashboardGrid();
            var account = Col(Txt("♙  玩家账户", 19, true));
            accountText = Txt(settings.AccountMode == "offline" ? "离线：" + settings.OfflineName :
                (settings.AccountMode == "microsoft" ?
                    ((microsoft != null && microsoft.HasSession) ? "微软正版：" + settings.AccountName : "微软正版：需要重新验证登录") : "未登录正版账号"));
            account.Children.Add(accountText);
            account.Children.Add(Txt("正版 / 离线登录", 12, false, true));
            account.Children.Add(Row(Btn("离线登录", EditOfflineAccount), Btn("微软正版", async () => await LoginMicrosoftAsync()),
                Btn("退出账号", async () => await SignOutAsync())));
            Place(grid, Card(account), 0, 0);
            var hero = Col(Txt("Minecraft Java 版", 27, true), Txt("选择版本，开启方块世界", 15, false, true));
            hero.Children.Add(Txt(activeProfile == null ? "未选择游戏实例" : activeProfile.ToString(), 13, false, true));
            var launch = Btn("▷  启动游戏", async () => await LaunchAsync(), true);
            launch.Height = 59; launch.FontSize = 19; launch.Margin = new Thickness(0, 12, 0, 13);
            hero.Children.Add(launch);
            hero.Children.Add(Row(Btn("实例管理与备份", ManageInstance), Btn("检测本地文件", CheckInstance)));
            Place(grid, Card(hero), 0, 1);
            var versions = Col(Txt("游戏版本", 19, true), Txt("选择已安装版本", 12, false, true));
            homeProfile = new ComboBox { ItemsSource = settings.Profiles, SelectedItem = activeProfile, DisplayMemberPath = "Name" };
            homeProfile.SelectionChanged += (s, e) => { activeProfile = homeProfile.SelectedItem as GameProfile; };
            versions.Children.Add(homeProfile);
            versions.Children.Add(Row(Btn("+ 安装版本", () => Navigate("游戏版本")), Btn("导入本地游戏", ImportLocalGame)));
            Place(grid, Card(versions), 1, 0);
            Place(grid, Card(Col(Txt("公告与更新", 18, true), Txt("Minecraft 新闻、启动器公告、更新日志", 14, false, true),
                Txt("WPF 架构迁移预览；尚未完成所有 Python 功能的等价迁移。", 12, false, true),
                Btn("查看更新说明", ShowReleaseNotes))), 1, 1);
            Place(grid, Card(Col(Txt("快捷操作", 18, true), Txt("版本管理 · 游戏目录 · Java 设置", 13, false, true),
                Row(Btn("版本管理", () => Navigate("游戏版本")), Btn("游戏目录", OpenGameFolder),
                    Btn("Java 设置", () => Navigate("设置")), Btn("资源中心", () => Navigate("资源中心"))))), 2, 1);
            root.Children.Add(grid); return root;
        }
        UIElement BuildVersions()
        {
            var root = Col(Txt("Minecraft 游戏版本", 24, true),
                Txt("选择版本和加载器，安装任务在后台运行。请勿同时使用旧版安装同一游戏版本。", 13, false, true));
            var toolbar = Row();
            versionQuery = new TextBox { Width = 260, ToolTip = "搜索版本号" };
            versionQuery.TextChanged += (s, e) => FilterVersions();
            toolbar.Children.Add(versionQuery);
            toolbar.Children.Add(Btn("刷新在线版本", async () => await LoadVersionsAsync(), true));
            root.Children.Add(toolbar);
            versionList = new ListBox { ItemsSource = gameVersions, Height = 350, MinWidth = 380, Margin = new Thickness(0, 10, 0, 12) };
            root.Children.Add(Card(versionList));
            var options = Col(Txt("选择加载器", 18, true));
            versionLoader = new ComboBox { ItemsSource = new[] { "原版", "Fabric", "Forge", "NeoForge", "Quilt" }, SelectedIndex = 0, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
            options.Children.Add(versionLoader);
            options.Children.Add(Txt("原版 / Fabric / Forge / NeoForge / Quilt：通过各自安装组件获取匹配的版本文件。部分新版本可能暂不兼容。", 12, false, true));
            versionInstallButton = Btn("安装所选版本", async () => await InstallSelectedVersionAsync(), true);
            versionCancelButton = Btn("取消安装", () => currentOperation?.Cancel());
            versionCancelButton.IsEnabled = false;
            options.Children.Add(Row(versionInstallButton, versionCancelButton,
                Btn("导入本地实例", ImportLocalGame), Btn("打开游戏目录", OpenGameFolder)));
            root.Children.Add(Card(options)); return root;
        }
        void FilterVersions()
        {
            // This preview shows the downloaded list directly. Network refresh only happens on explicit click.
            if (versionList == null || versionQuery == null) return;
            versionList.Items.Filter = item => item.ToString().IndexOf(versionQuery.Text ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        async Task LoadVersionsAsync()
        {
            if (loadingVersions) return;
            loadingVersions = true; Status("正在获取版本列表...");
            try
            {
                var versions = await minecraft.ListVersionsAsync();
                gameVersions.Clear(); foreach (var v in versions) gameVersions.Add(v);
                FilterVersions(); Status("已加载版本数量：" + gameVersions.Count);
            }
            catch (Exception ex) { ShowError("获取版本列表失败", ex); }
            finally { loadingVersions = false; }
        }
        async Task InstallSelectedVersionAsync()
        {
            if (versionList?.SelectedItem == null) { MessageBox.Show("请先选择版本。"); return; }
            if (currentOperation != null) { MessageBox.Show("已有安装任务进行中。"); return; }
            string version = Convert.ToString(versionList.SelectedItem);
            string loader = Convert.ToString(versionLoader?.SelectedItem) ?? "原版";
            // Snapshot selection before async awaits; the user may change the UI.
            currentOperation = new CancellationTokenSource();
            if (versionInstallButton != null) versionInstallButton.IsEnabled = false;
            if (versionCancelButton != null) versionCancelButton.IsEnabled = true;
            try
            {
                Status("开始安装 " + version + " / " + loader);
                var profile = await modLoaders.InstallAsync(version, loader,
                    new Progress<string>(Status), currentOperation.Token);
                // Persist only if this exact install finished and the version JSON exists.
                if (!settings.Profiles.Any(p => p.LaunchVersion == profile.LaunchVersion))
                    settings.Profiles.Add(profile);
                activeProfile = settings.Profiles.First(p => p.LaunchVersion == profile.LaunchVersion);
                store.Save(settings);
                Status("安装完成：" + activeProfile.Name);
                Navigate("首页");
            }
            catch (OperationCanceledException) { Status("已取消本次安装（已下载文件可能作为缓存保留）。"); }
            catch (Exception ex) { ShowError("安装游戏失败", ex); }
            finally
            {
                currentOperation.Dispose(); currentOperation = null;
                if (versionInstallButton != null) versionInstallButton.IsEnabled = true;
                if (versionCancelButton != null) versionCancelButton.IsEnabled = false;
            }
        }
        UIElement BuildResources(string type)
        {
            resourceType = type; resourceResults.Clear();
            var title = type == "mod" ? "Modrinth 资源中心" : type == "shader" ? "光影引擎" : "资源包";
            var root = Col(Txt(title, 24, true), Txt("根据游戏实例筛选下载资源；文件按 SHA-1 校验，独立重试。", 12, false, true));
            resourceQuery = new TextBox { Width = 320 };
            root.Children.Add(Row(resourceQuery, Btn("搜索", async () => await SearchResourcesAsync(), true)));
            resourceList = new ListBox { ItemsSource = resourceResults, Height = 390 };
            root.Children.Add(Card(resourceList));
            root.Children.Add(Card(Col(Txt("资源安装", 18, true), Txt("选择资源后下载对应游戏版本文件", 12, false, true),
                Row(Btn("安装到当前实例", async () => await InstallResourceAsync(), true), Btn("打开实例目录", OpenGameFolder)))));
            return root;
        }
        async Task SearchResourcesAsync()
        {
            Status("正在搜索 Modrinth...");
            try
            {
                using (var client = new ModrinthClient(settings))
                {
                    var result = await client.SearchAsync(resourceQuery.Text, resourceType, CancellationToken.None);
                    resourceResults.Clear(); foreach (var item in result) resourceResults.Add(item);
                }
                Status("搜索结果：" + resourceResults.Count);
            }
            catch (Exception ex) { ShowError("搜索失败", ex); }
        }
        async Task InstallResourceAsync()
        {
            var hit = resourceList?.SelectedItem as ResourceHit;
            if (activeProfile == null || hit == null) { MessageBox.Show("请先选择实例和资源。"); return; }
            try
            {
                Status("下载资源：" + hit.Name);
                using (var client = new ModrinthClient(settings))
                {
                    var file = await client.DownloadLatestAsync(hit, activeProfile, CancellationToken.None);
                    Status("下载完成：" + file);
                }
            }
            catch (Exception ex) { ShowError("资源下载失败", ex); }
        }
        UIElement MigrationPanel(string title, string details)
        {
            return Col(Txt(title, 24, true), Card(Col(Txt("迁移状态：尚未完成", 18, true),
                Txt(details, 14, false, true), Txt("为保证存档和账户安全，此处不会运行未经迁移验证的下载/安装逻辑。", 12, false, true))));
        }
        UIElement BuildModpacks() => MigrationPanel("整合包", "Modrinth .mrpack 安全解压、依赖索引和 CurseForge 镜像安装仍需移植。");

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
            localResourceList = new ListBox { Height = 370, Margin = new Thickness(0, 8, 0, 14) };
            root.Children.Add(Card(localResourceList));
            root.Children.Add(Card(Col(Txt("本地操作", 18, true),
                Row(Btn("导入文件", ImportLocalResource, true),
                    Btn("停用选中", () => ToggleLocalResource(false)),
                    Btn("启用选中", () => ToggleLocalResource(true))),
                Row(Btn("刷新列表", RefreshLocalResourceList),
                    Btn("打开实例目录", OpenGameFolder)),
                Txt("离线管理不等同于在线更新：自动版本匹配和更新前备份仍待完成。", 12, false, true))));
            RefreshLocalResourceList();
            return root;
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
            serverList = new ListBox { Height = 270, Margin = new Thickness(0, 8, 0, 10) };
            root.Children.Add(Card(serverList));
            serverName = new TextBox { Width = 170, ToolTip = "收藏名称" };
            serverAddress = new TextBox { Width = 310, ToolTip = "域名或 IP:端口" };
            root.Children.Add(Card(Col(Txt("添加或编辑服务器", 17, true),
                Row(Txt("名称"), serverName, Txt("地址"), serverAddress),
                Row(Btn("保存服务器", SaveServer, true),
                    Btn("删除选中", DeleteServer),
                    Btn("复制地址", CopyServerAddress)),
                Row(Btn("检查连通性", async () => await CheckServerAsync()),
                    Btn("启动当前实例", async () => await LaunchAsync())),
                Txt("连通性仅检查 TCP 端口，不能保证游戏版本/模组/正版要求匹配。", 12, false, true))));
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
                Txt("仅从 minecraftlu/BlockCraftLauncher 官方 GitHub Releases 检查版本。不会自动下载、运行或覆盖启动器。", 13, false, true));
            root.Children.Add(Card(Col(Txt("当前版本：v0.9.27 WPF Alpha", 18, true),
                Txt("旧版 v0.9.26 IconFix 与 WPF Alpha 属不同架构，不应直接自动互相覆盖。", 12, false, true),
                Btn("检查 GitHub 最新发布", async () => await CheckLauncherUpdateAsync(), true),
                Btn("打开官方 Releases", () => LauncherReleaseService.OpenOfficialRelease("https://github.com/minecraftlu/BlockCraftLauncher/releases")))));
            return root;
        }
        async Task CheckLauncherUpdateAsync()
        {
            try
            {
                Status("正在查询 GitHub Releases...");
                var release = await new LauncherReleaseService().CheckAsync(settings, CancellationToken.None);
                string message = (release.Newer ? "发现更新：" : "已查询最新发布：") + release.Version +
                    "\n\n不同架构不可直接覆盖安装，请阅读发布说明后手动下载。\n\n" + release.Description;
                Status("GitHub 最新版本：" + release.Version);
                if (MessageBox.Show(message + "\n\n打开官方发布页？", "启动器更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                    LauncherReleaseService.OpenOfficialRelease(release.Url);
            }
            catch (Exception ex) { ShowError("检查启动器更新失败", ex); }
        }

        UIElement BuildSettings()
        {
            var root = Col(Txt("启动器设置", 24, true), Txt("配置单独保存为 WPF 预览文件，不覆盖旧启动器的 settings.json。", 12, false, true));
            var p = Col(Txt("外观与缩放", 18, true), Txt("主题", 13, false, true));
            settingsTheme = new ComboBox { ItemsSource = new[] { "深色", "浅色" }, SelectedItem = settings.Theme, Width = 250, HorizontalAlignment = HorizontalAlignment.Left };
            settingsTheme.SelectionChanged += (s, e) => { settings.Theme = Convert.ToString(settingsTheme.SelectedItem); ApplyTheme(); };
            p.Children.Add(settingsTheme);
            zoomValue = Txt(settings.Zoom + "%", 14, true);
            settingsZoom = new Slider { Minimum = 80, Maximum = 140, TickFrequency = 10, IsSnapToTickEnabled = true, Value = settings.Zoom,
                Width = 300, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 8) };
            settingsZoom.ValueChanged += (s, e) => { settings.Zoom = (int)settingsZoom.Value; ApplyZoom(); if (zoomValue != null) zoomValue.Text = settings.Zoom + "%"; };
            p.Children.Add(Txt("界面缩放（全部控件，布局跟随重排）", 13, false, true)); p.Children.Add(settingsZoom); p.Children.Add(zoomValue);
            root.Children.Add(Card(p));
            var paths = Col(Txt("游戏与下载", 18, true));
            paths.Children.Add(Txt("Minecraft 游戏根目录", 13, false, true));
            settingsRoot = new TextBox { Text = settings.Root };
            paths.Children.Add(settingsRoot);
            paths.Children.Add(Txt("Java 可执行文件（预留，启动库可自行查找运行时）", 13, false, true));
            settingsJava = new TextBox { Text = settings.JavaPath }; paths.Children.Add(settingsJava);
            paths.Children.Add(Txt("HTTP/HTTPS 代理", 13, false, true));
            settingsProxy = new TextBox { Text = settings.Proxy }; paths.Children.Add(settingsProxy);
            paths.Children.Add(Txt("离线玩家名称", 13, false, true));
            settingsPlayer = new TextBox { Text = settings.OfflineName }; paths.Children.Add(settingsPlayer);
            paths.Children.Add(Txt("内存（MB）", 13, false, true));
            settingsMemory = new TextBox { Text = settings.MemoryMb.ToString() }; paths.Children.Add(settingsMemory);
            paths.Children.Add(Row(Btn("保存设置", SaveSettings, true), Btn("恢复 100% 缩放", () => { settingsZoom.Value = 100; })));
            root.Children.Add(Card(paths)); return root;
        }
        void SaveSettings()
        {
            settings.Root = settingsRoot?.Text ?? settings.Root;
            settings.Proxy = settingsProxy?.Text ?? settings.Proxy;
            settings.JavaPath = settingsJava?.Text ?? settings.JavaPath;
            settings.OfflineName = settingsPlayer?.Text ?? settings.OfflineName;
            if (int.TryParse(settingsMemory?.Text, out var mb)) settings.MemoryMb = Math.Max(512, mb);
            try { store.Save(settings); Status("WPF 预览版设置已保存"); }
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
        async Task LaunchAsync()
        {
            if (activeProfile == null) { MessageBox.Show("请先导入或安装游戏实例"); return; }
            try
            {
                Status("准备启动 " + activeProfile.Name);
                if (settings.AccountMode == "microsoft")
                {
                    // Never substitute an offline player for an expired premium session.
                    var session = await GetMicrosoftService().GetSessionAsync();
                    await minecraft.LaunchMicrosoftAsync(activeProfile, session);
                }
                else if (settings.AccountMode == "offline")
                    await minecraft.LaunchOfflineAsync(activeProfile);
                else throw new InvalidOperationException("请先登录微软正版账号或设置离线玩家名。");
                Status("游戏进程已启动");
            }
            catch (Exception ex) { ShowError("启动失败", ex); }
        }
        void ImportLocalGame()
        {
            var dialog = new OpenFileDialog { Filter = "Minecraft 版本 JSON (*.json)|*.json", Title = "选择版本文件（versions\\版本号\\版本号.json）" };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                string version = Path.GetFileNameWithoutExtension(dialog.FileName);
                var profile = new GameProfile { Id = Guid.NewGuid().ToString("N").Substring(0, 12), Name = version,
                    BaseVersion = version, LaunchVersion = version, Loader = "vanilla", Directory = settings.Root };
                settings.Profiles.Add(profile); activeProfile = profile; store.Save(settings); Navigate("首页");
            }
            catch (Exception ex) { ShowError("导入实例失败", ex); }
        }
        void ManageInstance()
        {
            if (activeProfile == null) { MessageBox.Show("请先选择游戏实例"); return; }
            var actions = new StackPanel { Margin = new Thickness(18) };
            actions.Children.Add(Txt("实例：" + activeProfile.Name, 18, true));
            actions.Children.Add(Btn("打开游戏目录", OpenGameFolder));
            actions.Children.Add(Btn("备份存档", () =>
            {
                var file = new SaveFileDialog { FileName = "saves-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", Filter = "ZIP 压缩包|*.zip" };
                if (file.ShowDialog(this) != true) return;
                try { LocalInstanceTools.BackupSaves(activeProfile, file.FileName); MessageBox.Show("备份完成"); }
                catch (Exception ex) { ShowError("备份失败", ex); }
            }));
            var window = new Window { Title = "实例管理与备份", Owner = this, Width = 480, Height = 250,
                Content = actions, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            window.ShowDialog();
        }
        void CheckInstance()
        {
            if (activeProfile == null) { MessageBox.Show("尚未选择实例"); return; }
            var path = Path.Combine(settings.Root, "versions", activeProfile.LaunchVersion, activeProfile.LaunchVersion + ".json");
            MessageBox.Show(File.Exists(path) ? "本地版本元数据存在。完整运行仍取决于 Java、依赖库与资源文件。" : "当前未找到本地版本元数据：\n" + path, "离线检查");
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
            MessageBox.Show("v0.9.27 WPF Alpha · 迁移阶段 2\n新增：Microsoft 正版 OAuth 登录与静默刷新；Fabric / Forge / NeoForge / Quilt 安装流程。\n优化：安装状态与取消入口、已完成实例去重。\n待验证：Windows 编译、真实账号登录、加载器安装及游戏启动。\n仍待迁移：CurseForge、整合包、自动更新及多人联机。\n\nPCL2 风格（设计参考，不使用其专有素材）", "BlockCraft Launcher 更新说明");
        }
        void ShowError(string title, Exception ex) { Log(title + "：" + ex); MessageBox.Show(ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
