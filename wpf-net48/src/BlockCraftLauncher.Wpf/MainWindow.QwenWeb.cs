using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BlockCraftLauncher.Wpf.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        const string QwenOfficialHome = "https://www.qianwen.com/";
        Grid qwenWebPage, qwenWebCanvas;
        TextBlock qwenWebStatus, qwenWebOrigin;
        Button qwenWebBack, qwenWebRefresh, qwenWebHome, qwenWebRetry, qwenWebInstallRuntime;
        WebView2 qwenWebView;
        readonly List<Window> qwenWebPopups = new List<Window>();
        bool qwenWebAttempted, qwenWebDisposed;
        string qwenWebProfileDirectory;
        string qwenWebPopupStage = "idle";
        Func<string, Task<CoreWebView2Environment>> qwenWebEnvironmentFactory;
        Action<CoreWebView2> qwenWebCoreReady;

        UIElement BuildQwenWebPage()
        {
            if (qwenWebPage != null) return qwenWebPage;
            qwenWebBack = Btn("后退", () => UseQwenBrowser(core => { if (core.CanGoBack) core.GoBack(); }));
            qwenWebRefresh = Btn("刷新", () => UseQwenBrowser(core => core.Reload()));
            qwenWebHome = Btn("官网首页", () => UseQwenBrowser(core => core.Navigate(QwenOfficialHome)));
            qwenWebBack.IsEnabled = qwenWebRefresh.IsEnabled = qwenWebHome.IsEnabled = false;
            qwenWebOrigin = Txt("", 12, false, true);
            qwenWebOrigin.VerticalAlignment = VerticalAlignment.Center;
            qwenWebOrigin.TextWrapping = TextWrapping.NoWrap;
            qwenWebOrigin.TextTrimming = TextTrimming.CharacterEllipsis;
            AutomationProperties.SetName(qwenWebOrigin, "当前网站域名");
            var downloadCommands = Btn("启动器下载指令", OpenLauncherDownloadCommands);
            downloadCommands.ToolTip = "输入游戏版本、模组名称或用途，在启动器里搜索和安装";
            var buttons = Row(qwenWebBack, qwenWebRefresh, qwenWebHome, downloadCommands);
            DockPanel.SetDock(buttons, Dock.Right);
            var toolbar = new DockPanel();
            toolbar.Children.Add(buttons);
            toolbar.Children.Add(qwenWebOrigin);

            qwenWebStatus = Txt("", 13, false, true);
            AutomationProperties.SetLiveSetting(qwenWebStatus, AutomationLiveSetting.Polite);
            qwenWebRetry = Btn("重试", RetryQwenWeb, true);
            qwenWebInstallRuntime = Btn("获取 WebView2 运行时", OpenQwenRuntimeDownload);
            SetQwenWebStatus("正在打开千问官网…");
            var notice = Col(qwenWebStatus, Row(qwenWebRetry, qwenWebInstallRuntime));
            qwenWebCanvas = new Grid { ClipToBounds = true };
            qwenWebPage = new Grid();
            qwenWebPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            qwenWebPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            qwenWebPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(notice, 1);
            Grid.SetRow(qwenWebCanvas, 2);
            qwenWebPage.Children.Add(toolbar);
            qwenWebPage.Children.Add(notice);
            qwenWebPage.Children.Add(qwenWebCanvas);
            qwenWebPage.Loaded += (sender, args) => BeginVisibleQwenWeb();
            qwenWebPage.IsVisibleChanged += (sender, args) => BeginVisibleQwenWeb();
            return qwenWebPage;
        }

        void OpenLauncherDownloadCommands()
        {
            SwitchQwenTab(QwenAssistantTab.Chat);
            qwenQuery.BringIntoView();
            qwenQuery.Focus();
        }

        void BeginVisibleQwenWeb()
        {
            if (!qwenWebDisposed && !qwenWebAttempted && qwenWebPage.IsLoaded && qwenWebPage.IsVisible)
                _ = StartQwenWebAsync();
        }

        void SetQwenWebStatus(string text, bool retry = false, bool installRuntime = false)
        {
            qwenWebStatus.Text = text;
            qwenWebStatus.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            qwenWebRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
            qwenWebInstallRuntime.Visibility = installRuntime ? Visibility.Visible : Visibility.Collapsed;
        }

        void OpenQwenRuntimeDownload()
        {
            try { Process.Start(new ProcessStartInfo(WebView2RuntimeSupport.OfficialDownloadPage) { UseShellExecute = true }); }
            catch (Exception) { SetQwenWebStatus("请在浏览器访问 " + WebView2RuntimeSupport.OfficialDownloadPage, true, true); }
        }

        void UseQwenBrowser(Action<CoreWebView2> action)
        {
            try
            {
                var core = qwenWebView?.CoreWebView2;
                if (core != null && !qwenWebDisposed) action(core);
            }
            catch (Exception) { QwenWebFailure("网页连接已中断，请重新打开千问官网。"); }
        }

        async Task StartQwenWebAsync()
        {
            qwenWebAttempted = true;
            WebView2 view = null;
            SetQwenWebStatus("正在打开千问官网…");
            try
            {
                string profile = qwenWebProfileDirectory ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher", "QwenWebProfile");
                var environment = await (qwenWebEnvironmentFactory?.Invoke(profile) ??
                    CoreWebView2Environment.CreateAsync(null, profile));
                if (qwenWebDisposed) return;
                view = new WebView2();
                AutomationProperties.SetName(view, "千问网页版");
                qwenWebView = view;
                qwenWebCanvas.Children.Add(view);
                await view.EnsureCoreWebView2Async(environment);
                if (qwenWebDisposed || view != qwenWebView) return;
                var core = view.CoreWebView2;
                ConfigureQwenBrowser(core, environment, null);
                qwenWebCoreReady?.Invoke(core);
                core.HistoryChanged += (s, a) =>
                {
                    if (view == qwenWebView) qwenWebBack.IsEnabled = core.CanGoBack;
                };
                qwenWebHome.IsEnabled = qwenWebRefresh.IsEnabled = true;
                core.Navigate(QwenOfficialHome);
            }
            catch (Exception ex)
            {
                if (qwenWebDisposed || (view != null && view != qwenWebView)) return;
                bool runtime = WebView2RuntimeSupport.IsRuntimeFailure(ex) || ex is NotImplementedException ||
                    ex is NotSupportedException;
                ReleaseQwenBrowser();
                SetQwenWebStatus(runtime
                    ? "请安装或更新 Microsoft Edge WebView2 Runtime，然后点击重试。"
                    : "无法打开内嵌网页。请检查 WebView2 运行环境和本机应用数据目录的写入权限，然后重试。", true, runtime);
            }
        }

        static bool IsQwenWebNavigationAllowed(string address)
        {
            if (string.Equals(address, "about:blank", StringComparison.OrdinalIgnoreCase)) return true;
            // The website can offer a generated attachment through an HTTPS-origin blob URL.
            if (address != null && address.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
                address = address.Substring(5);
            return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
                uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.HostNameType == UriHostNameType.Dns && !uri.IsLoopback;
        }

        static string QwenWebHost(string address)
        {
            if (string.IsNullOrEmpty(address)) return "";
            if (address.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) address = address.Substring(5);
            return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                ? uri.IdnHost : "";
        }

        void ConfigureQwenBrowser(CoreWebView2 core, CoreWebView2Environment environment, Window popup)
        {
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = popup != null;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            ulong navigation = 0;
            core.NavigationStarting += (sender, args) =>
            {
                if (!IsQwenWebNavigationAllowed(args.Uri)) { args.Cancel = true; return; }
                navigation = args.NavigationId;
                if (popup == null) SetQwenWebStatus("正在加载网页…");
            };
            core.SourceChanged += (sender, args) =>
            {
                string host = QwenWebHost(core.Source);
                if (popup == null) qwenWebOrigin.Text = host;
                else popup.Title = host.Length == 0 ? "千问网页" : host + " · 网页";
            };
            core.LaunchingExternalUriScheme += (sender, args) => args.Cancel = true;
            core.PermissionRequested += (sender, args) =>
            {
                string host = QwenWebHost(args.Uri);
                bool website = host == "qianwen.com" || host.EndsWith(".qianwen.com", StringComparison.Ordinal);
                bool interactiveMedia = args.PermissionKind == CoreWebView2PermissionKind.Microphone ||
                    args.PermissionKind == CoreWebView2PermissionKind.Camera ||
                    args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead;
                if (!website || !interactiveMedia) args.State = CoreWebView2PermissionState.Deny;
                // For the website's voice, camera or paste controls, keep the browser's normal permission prompt.
            };
            core.ProcessFailed += (sender, args) =>
            {
                if (qwenWebDisposed || (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.BrowserProcessExited &&
                    args.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited)) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (qwenWebDisposed) return;
                    if (popup != null) { if (popup.IsLoaded) popup.Close(); }
                    else if (qwenWebView?.CoreWebView2 == core) QwenWebFailure("网页进程已退出，可以重新打开千问官网。");
                }));
            };
            core.NavigationCompleted += (sender, args) =>
            {
                if (qwenWebDisposed || args.NavigationId != navigation ||
                    args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled) return;
                if (popup != null)
                {
                    if (!args.IsSuccess) popup.Title = QwenWebHost(core.Source) + " · 网页加载失败";
                    return;
                }
                if (args.IsSuccess) SetQwenWebStatus("");
                else SetQwenWebStatus("网页暂时无法加载。请检查网络后重试。", true);
            };
            core.NewWindowRequested += (sender, args) => OpenQwenWebPopupAsync(core, environment, args);
            if (popup != null) core.WindowCloseRequested += (sender, args) => popup.Close();
        }

        async void OpenQwenWebPopupAsync(CoreWebView2 opener, CoreWebView2Environment environment,
            CoreWebView2NewWindowRequestedEventArgs args)
        {
            qwenWebPopupStage = "requested";
            string target = string.IsNullOrEmpty(args.Uri) ? "about:blank" : args.Uri;
            string from = QwenWebHost(opener.Source);
            if (qwenWebDisposed || !IsQwenWebNavigationAllowed(target) ||
                (!args.IsUserInitiated && from != "qianwen.com" && !from.EndsWith(".qianwen.com", StringComparison.Ordinal)))
            {
                args.Handled = true;
                qwenWebPopupStage = "blocked";
                return;
            }
            using (var deferral = args.GetDeferral())
            {
                Window popup = null;
                try
                {
                    var browser = new WebView2();
                    popup = new Window
                    {
                        Owner = this, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Title = "千问网页", Width = Math.Min(800, SystemParameters.WorkArea.Width),
                        Height = Math.Min(650, SystemParameters.WorkArea.Height), Content = browser
                    };
                    qwenWebPopups.Add(popup);
                    popup.Closed += (sender, eventArgs) => { qwenWebPopups.Remove(popup); browser.Dispose(); };
                    popup.Show();
                    qwenWebPopupStage = "initializing";
                    await browser.EnsureCoreWebView2Async(environment);
                    if (qwenWebDisposed || !qwenWebPopups.Contains(popup))
                    {
                        args.Handled = true;
                        qwenWebPopupStage = "closed";
                        return;
                    }
                    var child = browser.CoreWebView2;
                    qwenWebPopupStage = "configuring";
                    ConfigureQwenBrowser(child, environment, popup);
                    args.NewWindow = child;
                    // Resource handlers must bind to the adopted window's new content before completing the deferral.
                    qwenWebPopupStage = "adopted";
                    qwenWebCoreReady?.Invoke(child);
                }
                catch (Exception ex)
                {
                    args.Handled = true;
                    qwenWebPopupStage += ":" + ex.GetType().Name + ":" + ex.HResult.ToString("X8");
                    if (popup != null && qwenWebPopups.Contains(popup)) popup.Close();
                    if (!qwenWebDisposed) SetQwenWebStatus("网页弹窗未能打开，请重新点击网页中的登录或链接。");
                }
            }
        }

        void RetryQwenWeb()
        {
            if (qwenWebDisposed) return;
            if (qwenWebView != null)
                UseQwenBrowser(core => core.Navigate(QwenOfficialHome));
            else if (qwenWebRetry.Visibility == Visibility.Visible)
                _ = StartQwenWebAsync();
        }

        void QwenWebFailure(string message)
        {
            if (qwenWebDisposed) return;
            ReleaseQwenBrowser();
            SetQwenWebStatus(message, true);
        }

        void ReleaseQwenBrowser()
        {
            foreach (var popup in qwenWebPopups.ToArray()) popup.Close();
            var view = qwenWebView;
            qwenWebView = null;
            if (view != null) { qwenWebCanvas.Children.Remove(view); view.Dispose(); }
            qwenWebBack.IsEnabled = qwenWebHome.IsEnabled = qwenWebRefresh.IsEnabled = false;
            qwenWebOrigin.Text = "";
        }

        void DisposeQwenWebPage()
        {
            if (qwenWebDisposed) return;
            qwenWebDisposed = true;
            if (qwenWebPage != null) ReleaseQwenBrowser();
        }
    }
}
