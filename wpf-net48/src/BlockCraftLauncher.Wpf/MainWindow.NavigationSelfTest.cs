using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
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

                    Navigate("首页");
                    Visit("首页", "home");
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
                    Side("手动安装", "manual-install");
                    Side("资源中心", "mods");
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
                    Side("皮肤库", "skin-library");
                    Visit("下载管理", "tasks", TaskShortcut);
                    Visit("多人联机", "servers", TopMultiplayerButton);
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
                    Side("查找服务器", "server-search");
                    Side("局域网", "lan");
                    var joinWorld = FixtureButton(PageHost, "加入选中世界");
                    if (joinWorld.IsEnabled) throw new InvalidOperationException("未选中世界时可加入局域网。");
                    lanWorldList.ItemsSource = new[] { server };
                    lanWorldList.SelectedItem = server;
                    if (!joinWorld.IsEnabled) throw new InvalidOperationException("选中世界后无法加入局域网。");
                    Visit("设置", "start-settings", TopSettingsButton);
                    Side("下载设置", "download-settings");
                    Side("个性化", "appearance");
                    Side("更新设置", "update-settings");
                    Visit("关于", "about", TopMoreButton);
                    Side("启动器更新", "updates");
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
