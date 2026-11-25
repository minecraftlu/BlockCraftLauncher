using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        internal async Task<string> VerifyLocalQwenAsync()
        {
            string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blockcraft-qwen-local-" +
                Guid.NewGuid().ToString("N"))).FullName;
            string models = Directory.CreateDirectory(Path.Combine(folder, "外置模型盘")).FullName;
            var window = new MainWindow(null, null, new SettingsStore(Path.Combine(folder, "settings.json"))) {
                SuppressAutomaticUpdateCheck = true, ShowInTaskbar = false, ShowActivated = false, Width = 1080, Height = 720,
                qwenWebPageFactory = OfflineQwenWebPage
            };
            var engine = new Uri("http://127.0.0.1:55001/");
            var calls = new List<LocalQwenMode>();
            bool paused = true;
            window.localQwenPrepare = async (mode, root, install, progress, token) =>
            {
                CheckQwen(root == models, "本地模型未使用选定的目录。");
                calls.Add(mode);
                if (install && paused)
                {
                    progress.Report("正在下载模型文件…");
                    await Task.Delay(Timeout.Infinite, token);
                }
                return engine;
            };
            const string first = "我把村庄叫作枫桥。", answer = "记住了，村庄叫枫桥。", second = "村庄叫什么？";
            window.localQwenReply = (endpoint, turns, append, token) =>
            {
                CheckQwen(endpoint == engine, "聊天未使用本地服务。");
                if (turns.Count > 1)
                    CheckQwen(turns.Select(t => t.Content).SequenceEqual(new[] { first, answer, second }), "连续对话上下文异常。");
                append(answer);
                return Task.FromResult(answer);
            };
            string latestSource = null;
            window.localQwenGenerate = (endpoint, prompt, source, destination, progress, token) =>
            {
                CheckQwen(endpoint == engine && prompt.Length > 0 && destination.StartsWith(models + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "图片推理未使用选定目录或本地服务。");
                latestSource = source;
                var pixel = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgr24, null, new byte[18], 9);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(pixel));
                using (var file = File.Create(destination)) encoder.Save(file);
                return Task.CompletedTask;
            };
            async Task Settle()
            {
                await WaitForQwenAsync(() => window.localQwenOperation == null, "本地 AI 操作结束");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
            async Task Send(string text)
            {
                window.localQwenPrompt.Text = text;
                ClickFixtureAction(window.localQwenSubmit);
                await Settle();
            }
            try
            {
                window.Show();
                window.Navigate(QwenSearchPage);
                window.ArrangeLayoutProbe(1000, 660);
                CheckQwen(window.qwenSelectedTab == QwenAssistantTab.Web && window.localQwenMode == null,
                    "进入千问时提前启动了本地模型。");
                ClickFixtureAction(window.qwenLocalTab);
                window.UpdateLayout();
                window.SetLocalQwenDirectory(models);
                CheckQwen(window.store.Load().LocalQwenDirectory == models && calls.Count == 0 &&
                    window.localQwenModelInfo.Text.Contains("Qwen3.5-4B"), "选择模型目录时提前下载或未保存位置。");
                var installButton = FixtureButton(window.qwenLocalBody, "准备模型");
                ClickFixtureAction(installButton);
                await WaitForQwenAsync(() => window.localQwenStatus.Text.Contains("正在下载模型"), "模型下载进度");
                window.UpdateLayout();
                CheckQwen(!window.localQwenSettings.IsEnabled && !window.localQwenSubmit.IsEnabled, "下载期间允许重复操作。");
                ClickFixtureAction(window.localQwenStop);
                await Settle();
                CheckQwen(window.localQwenStatus.Text.Contains("已停止") && window.localQwenSettings.IsEnabled, "取消后未恢复。");
                paused = false;
                ClickFixtureAction(installButton);
                await Settle();
                CheckQwen(window.localQwenStatus.Text == "模型已就绪。", "准备模型不可重试。");
                await Send(first);
                await Send(second);
                CheckQwen(window.localQwenHistory.Current.Turns.Count == 2 && !window.localQwenHistory.HistoryEnabled,
                    "实际聊天入口未记录两轮本地会话。");
                window.PageScroll.ScrollToTop();
                var surface = (FrameworkElement)window.Content;
                window.ArrangeLayoutProbe((int)surface.ActualWidth, (int)surface.ActualHeight, "local-qwen");
                window.localQwenPrompt.Text = "聊天草稿";
                window.localQwenMode.SelectedIndex = (int)LocalQwenMode.Generate;
                window.UpdateLayout();
                CheckQwen(window.localQwenModelInfo.Text.Contains("Qwen-Image-2512") && window.localQwenPrompt.Text == "",
                    "画图型号或草稿隔离异常。");
                await Send("画一座村庄");
                string original = window.localQwenOutputPath;
                CheckQwen(File.Exists(original) && latestSource == null && window.localQwenResultImage.Source != null,
                    "文生图未显示生成的 PNG。");
                byte[] before = File.ReadAllBytes(original);
                window.localQwenMode.SelectedIndex = (int)LocalQwenMode.Edit;
                window.UpdateLayout();
                await Send("把白天改成黄昏");
                CheckQwen(window.localQwenStatus.Text.Contains("选择要修改的图片"), "编辑没有要求输入图片。");
                window.SetLocalQwenSourceImage(original);
                await Send("把白天改成黄昏");
                CheckQwen(latestSource == original && File.Exists(window.localQwenOutputPath) &&
                    window.localQwenOutputPath != original && File.ReadAllBytes(original).SequenceEqual(before) &&
                    window.localQwenImageOutput.Visibility == Visibility.Visible && window.localQwenResultImage.Source != null,
                    "编辑未保留输入或未展示新 PNG。");
                string saved = Path.Combine(folder, "另存.png");
                window.CopyLocalQwenOutput(saved);
                CheckQwen(File.ReadAllBytes(saved).SequenceEqual(File.ReadAllBytes(window.localQwenOutputPath)),
                    "另存为 PNG 的内容不匹配。");
                window.localQwenMode.SelectedIndex = (int)LocalQwenMode.Chat;
                CheckQwen(window.localQwenPrompt.Text == "聊天草稿" && window.localQwenHistory.Current.Turns.Count == 2,
                    "切换模式遗失了聊天草稿或上下文。");
                CheckQwen(calls.SequenceEqual(new[] { LocalQwenMode.Chat, LocalQwenMode.Chat, LocalQwenMode.Chat,
                    LocalQwenMode.Chat, LocalQwenMode.Generate, LocalQwenMode.Edit }), "使用了错误的模型或重复发起推理。");
                return "本地模型界面已验证：模型准备、取消重试、目录持久化、连续聊天、文生图、改图与 PNG 另存。";
            }
            finally
            {
                window.Close();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
