using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;
using Microsoft.Web.WebView2.Core;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        const string WebFixtureHome = "https://www.qianwen.com/";
        const string WebFixturePopup = "https://www.qianwen.com/blockcraft-test-login";

        static async Task<T> QwenWebWithin<T>(Task<T> task, int seconds, string stage)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(seconds))) != task)
                throw new TimeoutException("千问网页自检超时：" + stage);
            return await task;
        }

        static TaskCompletionSource<T> QwenWebSignal<T>() =>
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        static MainWindow OpenQwenWebCheckWindow(string directory, Action<CoreWebView2> configure,
            Func<string, Task<CoreWebView2Environment>> create = null)
        {
            var window = new MainWindow(null, null, new SettingsStore(Path.Combine(directory, "launcher.json"))) {
                qwenWebProfileDirectory = Path.Combine(directory, "browser"),
                qwenSettingsPath = Path.Combine(directory, "assistant.json"),
                qwenCompanionPath = Path.Combine(directory, "companion.json"),
                qwenHistoryPath = Path.Combine(directory, "history.json"),
                qwenMediaTaskPath = Path.Combine(directory, "tasks.json"),
                qwenWebCoreReady = configure, qwenWebEnvironmentFactory = create, SuppressAutomaticUpdateCheck = true,
                ShowInTaskbar = false, ShowActivated = false
            };
            window.Show();
            window.Navigate("首页");
            window.UpdateLayout();
            ClickFixtureAction(FixtureButton(window.PageHost, NavLabel(QwenSearchPage)));
            window.qwenTools.IsExpanded = true;
            window.UpdateLayout();
            ClickFixtureAction(window.qwenWebTab);
            return window;
        }

        static async Task RemoveQwenWebCheckDirectory(string directory)
        {
            // Chromium releases profile file handles asynchronously after the last controller closes.
            for (int attempt = 0; attempt < 16; attempt++)
            {
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); return; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(250);
            }
        }

        static void UseQwenWebFixture(CoreWebView2 core)
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (sender, args) =>
            {
                string url = args.Request.Uri;
                bool home = url == WebFixtureHome, popup = url == WebFixturePopup;
                const string homePage = @"<!doctype html><html lang='zh-CN'><meta charset='utf-8'>
<title>网页自检</title><style>
body{font:16px 'Microsoft YaHei UI',sans-serif;color:#16334a;background:#f5f8fb;padding:30px}
main{max-width:540px;padding:24px;background:white;border:1px solid #cbd9e2;border-radius:10px}
h1{font-size:24px}p{line-height:1.7}a{color:#006285}
</style><main><h1>千问网页连接测试</h1><p>这是在启动器内加载的离线测试页面。</p>
<p>可以在同一窗口切换到聊天、下载指令和创作，再返回网页。</p>
<a id='popup' href='/blockcraft-test-login' target='account'>测试账号弹窗</a></main>
<script>document.title=document.cookie.split('; ').includes('blockcraft_fixture=restorable')?'fixture-restored':'fixture-home';</script></html>";
                const string popupPage = @"<!doctype html><meta charset='utf-8'><title>网页自检</title>
<p>离线弹窗测试</p><script>document.cookie='blockcraft_fixture=restorable; Max-Age=3600; Path=/; Secure; SameSite=Lax';document.title='fixture-popup';</script>";
                byte[] data = Encoding.UTF8.GetBytes(home ? homePage : popup ? popupPage : "");
                args.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(data), home || popup ? 200 : 404,
                    home || popup ? "OK" : "Not Found", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        }

        // HwndHost content is outside the WPF visual tree. Combine the actual browser pixels
        // with the shell using the measured view bounds, including the launcher's layout scale.
        async Task CaptureQwenWebCheck(string captureName)
        {
            string directory = Environment.GetEnvironmentVariable("BLOCKCRAFT_UI_CAPTURE_DIR");
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var root = (FrameworkElement)Content;
            root.UpdateLayout();
            int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
            var shell = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            shell.Render(root);
            var bounds = qwenWebView.TransformToAncestor(root).TransformBounds(new Rect(qwenWebView.RenderSize));
            CheckQwen(bounds.Width > 200 && bounds.Height > 90, "官网浏览器的可见区域太小。");
            using (var stream = new MemoryStream())
            {
                await qwenWebView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                stream.Position = 0;
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var combined = new DrawingVisual();
                using (var dc = combined.RenderOpen())
                {
                    dc.DrawImage(shell, new Rect(0, 0, width, height));
                    dc.DrawImage(frame, bounds);
                }
                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(combined);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.Combine(directory, captureName + ".png"))) encoder.Save(file);
            }
        }

        internal async Task<string> VerifyQwenWebAsync()
        {
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blockcraft-web-" + Guid.NewGuid().ToString("N"))).FullName;
            var home = QwenWebSignal<CoreWebView2>();
            var popup = QwenWebSignal<CoreWebView2>();
            var restored = QwenWebSignal<CoreWebView2>();
            var browserExit = QwenWebSignal<bool>();
            int views = 0;
            var trail = new StringBuilder();
            void Trace(string entry) { if (trail.Length < 2400) trail.Append(" ").Append(entry); }
            void Observe(CoreWebView2 core)
            {
                int view = ++views;
                Trace("core-" + view);
                UseQwenWebFixture(core);
                void Ready()
                {
                    if (core.Source == WebFixtureHome && core.DocumentTitle == "fixture-home") home.TrySetResult(core);
                    if (core.Source == WebFixturePopup && core.DocumentTitle == "fixture-popup") popup.TrySetResult(core);
                    if (core.Source == WebFixtureHome && core.DocumentTitle == "fixture-restored") restored.TrySetResult(core);
                }
                // Source, title and navigation notifications may be delivered in a different order on each runtime.
                core.DocumentTitleChanged += (s, e) => Ready();
                core.SourceChanged += (s, e) => Ready();
                core.NewWindowRequested += (s, e) => Trace("requested-" + view + "-" + e.IsUserInitiated);
                core.NavigationStarting += (s, e) =>
                    Trace("starting-" + view + (e.Uri == WebFixturePopup ? "-popup" : e.Uri == WebFixtureHome ? "-home" : "-other"));
                core.NavigationCompleted += (sender, e) =>
                {
                    Trace("completed-" + view + "-" + e.HttpStatusCode + "-" + e.WebErrorStatus);
                    if (e.IsSuccess) Ready();
                };
            }
            MainWindow first = null, second = null;
            try
            {
                int attempts = 0;
                first = OpenQwenWebCheckWindow(directory, Observe, path =>
                    ++attempts == 1
                        ? Task.FromException<CoreWebView2Environment>(new DllNotFoundException("WebView2Loader"))
                        : CoreWebView2Environment.CreateAsync(null, path));
                await WaitForQwenAsync(() => attempts == 1 && first.qwenWebInstallRuntime?.IsVisible == true &&
                    first.qwenWebRetry?.IsVisible == true && first.qwenWebRetry.IsEnabled, "缺少运行时的恢复入口");
                var commands = FixtureButton(first.qwenWebPage, "启动器下载指令");
                ClickFixtureAction(commands);
                CheckQwen(first.qwenChatBody.IsVisible && first.qwenQuery.IsVisible && first.qwenQuery.IsEnabled &&
                    !first.qwenQuery.IsReadOnly && first.localQwenRuntime == null, "官网入口未打开统一对话框。");
                first.qwenQuery.Text = "给当前实例装钠";
                first.qwenTools.IsExpanded = true;
                first.UpdateLayout();
                ClickFixtureAction(first.qwenWebTab);
                ClickFixtureAction(first.qwenWebRetry);
                var main = await QwenWebWithin(home.Task, 40, "启动真实浏览器并打开官网地址");
                CheckQwen(attempts == 2 && views == 1 && first.qwenWebView.IsVisible, "重试后未打开千问官网。");
                main.Environment.BrowserProcessExited += (s, e) => browserExit.TrySetResult(true);
                await first.CaptureQwenWebCheck("qwen-web");
                ClickFixtureAction(commands);
                CheckQwen(first.qwenQuery.IsVisible && first.qwenQuery.Text == "给当前实例装钠",
                    "返回下载指令时未恢复统一输入框或草稿。");
                CheckQwen(!first.qwenWebView.IsVisible && first.qwenChatBody.IsVisible, "原生下载指令入口不可见。");
                first.qwenTools.IsExpanded = true;
                first.UpdateLayout();
                ClickFixtureAction(first.qwenWebTab);
                CheckQwen(views == 1 && first.qwenWebView.IsVisible, "切换标签后丢失了网页。");

                await first.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                // Click the actual link in viewport coordinates. This goes through Chromium's input pipeline even on a
                // headless Windows runner, without desktop cursor automation or script-click popup heuristics.
                string xy = await main.ExecuteScriptAsync(@"(() => {
                    const a = document.getElementById('popup');
                    a.scrollIntoView({block:'center'});
                    const r = a.getBoundingClientRect(), x = r.x + r.width / 2, y = r.y + r.height / 2;
                    return document.elementFromPoint(x,y) === a ? [Math.floor(x), Math.floor(y)] : [];
                })()");
                var coords = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<int[]>(xy);
                CheckQwen(coords != null && coords.Length == 2, "浏览器未能命中测试页的弹窗链接。");
                foreach (var action in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
                {
                    var input = new { type = action, x = coords[0], y = coords[1],
                        button = action == "mouseMoved" ? "none" : "left", buttons = action == "mousePressed" ? 1 : 0,
                        clickCount = action == "mouseMoved" ? 0 : 1 };
                    await main.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                        new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(input));
                }
                var auth = await QwenWebWithin(popup.Task, 25, "受管账号弹窗");
                CheckQwen(views == 2 && first.OwnedWindows.OfType<Window>().Any(w => w.IsVisible) &&
                    auth.Source == WebFixturePopup, "账号弹窗脱离了启动器。");
                // The cookie is created by the fixture webpage, with an expiration date, exactly like a persistent website cookie.
                first.Close();
                first = null;
                await QwenWebWithin(browserExit.Task, 20, "已关闭浏览器写回账号会话");
                second = OpenQwenWebCheckWindow(directory, Observe);
                await QwenWebWithin(restored.Task, 35, "重新打开后保留网站会话");
                CheckQwen(views == 3, "重开后浏览器或弹窗初始化次数异常。");
                return "PASS · 真实 WebView2、官网内的启动器下载指令入口、原生聊天草稿、内嵌账号弹窗、浏览器完全退出后保留站点 Cookie。页面与 Cookie 由隔离的离线夹具提供。";
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(e.Message + "。隔离浏览器记录：" + trail +
                    "；弹窗阶段：" + (first ?? second)?.qwenWebPopupStage +
                    "；启动器状态：" + (first?.qwenWebStatus?.Text ?? second?.qwenWebStatus?.Text ?? ""), e);
            }
            finally
            {
                second?.Close();
                first?.Close();
                await RemoveQwenWebCheckDirectory(directory);
            }
        }

        internal async Task<string> VerifyQwenWebLiveAsync()
        {
            string directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blockcraft-web-live-" + Guid.NewGuid().ToString("N"))).FullName;
            var ready = QwenWebSignal<bool>();
            var navigation = QwenWebSignal<CoreWebView2NavigationCompletedEventArgs>();
            bool officialRequest = false;
            MainWindow window = null;
            try
            {
                window = OpenQwenWebCheckWindow(directory, core =>
                {
                    ready.TrySetResult(true);
                    core.NavigationStarting += (s, e) => { if (e.Uri == WebFixtureHome) officialRequest = true; };
                    core.NavigationCompleted += (s, e) => { if (officialRequest) navigation.TrySetResult(e); };
                });
                await QwenWebWithin(ready.Task, 35, "启动真实浏览器");
                if (await Task.WhenAny(navigation.Task, Task.Delay(TimeSpan.FromSeconds(50))) != navigation.Task)
                {
                    CheckQwen(officialRequest, "未向官网发起导航。");
                    return "LIVE LIMITED · WebView2 已发起官网导航，测试网络在 50 秒内未完成网页加载。未验证账号登录。";
                }
                var result = await navigation.Task;
                CheckQwen(officialRequest, "未向官网发起导航。");
                var core = window.qwenWebView.CoreWebView2;
                var reached = Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                    (uri.IdnHost == "qianwen.com" || uri.IdnHost.EndsWith(".qianwen.com", StringComparison.OrdinalIgnoreCase));
                if (reached && result.IsSuccess && result.HttpStatusCode >= 200 && result.HttpStatusCode < 400)
                {
                    // Check for visible public controls in an empty profile. Only a boolean marker leaves the page.
                    // No form values, conversation text, account data, or HTTP body are read or recorded.
                    const string visibleControl = @"(() => {
                        if (!(location.protocol === 'https:' && /(^|\.)qianwen\.com$/i.test(location.hostname))) return '';
                        const visible = el => el.getClientRects().length && getComputedStyle(el).visibility !== 'hidden';
                        const brand = /千问|通义|qianwen|qwen/i.test(document.title);
                        if (!brand) return '';
                        if (Array.from(document.querySelectorAll('textarea,[contenteditable=true]')).some(visible)) return 'composer';
                        const actions = document.querySelectorAll('button,a,[role=button],span');
                        if (Array.from(actions).some(el => visible(el) && /^(登录(\s*[\/／]\s*注册)?|立即登录|登录千问|扫码登录|log in|sign in)$/i.test((el.textContent || '').trim()))) return 'login';
                        return '';
                    })()";
                    for (int sample = 0; sample < 16; sample++)
                    {
                        string marker = await core.ExecuteScriptAsync(visibleControl);
                        if (marker == "\"composer\"" || marker == "\"login\"")
                        {
                            await window.CaptureQwenWebCheck("qwen-web-live");
                            return "LIVE · 真实 WebView2 访问千问官网，HTTP " + result.HttpStatusCode + "，已显示" +
                                (marker == "\"composer\"" ? "对话输入区域" : "登录入口") + "。未输入账号或发送消息。";
                        }
                        await Task.Delay(750);
                    }
                    return "LIVE LIMITED · 真实 WebView2 已收到官网 HTTP " + result.HttpStatusCode +
                        "，测试网络中尚未识别到可见的对话输入框或登录入口；未验证账号登录。";
                }
                return "LIVE LIMITED · WebView2 已请求千问官网，本次测试网络未确认成功页面。HTTP " + result.HttpStatusCode +
                    "，浏览器状态 " + result.WebErrorStatus + "。未验证账号登录。";
            }
            finally
            {
                window?.Close();
                await RemoveQwenWebCheckDirectory(directory);
            }
        }
    }
}
