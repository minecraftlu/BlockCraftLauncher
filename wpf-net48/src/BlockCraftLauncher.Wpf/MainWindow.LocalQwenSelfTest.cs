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
                qwenSettingsPath = Path.Combine(folder, "assistant.json"), qwenHistoryPath = Path.Combine(folder, "history.json"),
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
            async Task SendImage(string text)
            {
                window.localQwenPrompt.Text = text;
                ClickFixtureAction(window.localQwenSubmit);
                await Settle();
            }
            async Task SendChat(string text)
            {
                window.qwenQuery.Text = text;
                ClickFixtureAction(window.qwenSubmit);
                await WaitForQwenAsync(() => window.qwenRequest == null, "统一对话完成");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            try
            {
                window.Show();
                window.Navigate(QwenSearchPage);
                window.ArrangeLayoutProbe(1000, 660);
                CheckQwen(window.qwenSelectedTab == QwenAssistantTab.Chat && window.qwenQuery.IsVisible &&
                    window.qwenProvider.SelectedIndex == 0 && calls.Count == 0 && !window.qwenWebAttempted,
                    "默认入口没有显示本地对话，或启动时提前访问了模型或网页。");
                window.qwenTools.IsExpanded = true;
                window.UpdateLayout();
                CheckQwen(window.qwenLocalTab.IsVisible, "本地模型入口无法从工具菜单打开。");
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
                CheckQwen(!window.localQwenPrompt.IsVisible, "准备聊天模型时出现了第二个聊天输入框。");
                ClickFixtureAction(window.qwenChatTab);
                await SendChat(first);
                await SendChat(second);
                var session = window.qwenConversations.Current;
                CheckQwen(session.Turns.Count == 2 && window.qwenAnswer.Text == answer && !window.qwenConversations.HistoryEnabled,
                    "统一输入框没有记录两轮本地对话。");
                const string cloudQuestion = "请用一句话总结上文。", cloudAnswer = "村庄叫枫桥。";
                window.qwenQuery.Text = cloudQuestion;
                window.qwenProvider.SelectedIndex = 1;
                CheckQwen(window.qwenQuery.Text == cloudQuestion && window.qwenAnswer.Text == answer,
                    "切换云端时丢失草稿或上一次回答。");
                window.qwenApiKey.Password = QwenFixtureKey;
                window.qwenRunChat = (turns, key, endpoint, model, web, append, token) =>
                {
                    CheckQwen(turns.Select(t => t.Content).SequenceEqual(new[] { first, answer, second, answer, cloudQuestion }),
                        "云端没有收到当前会话的本地上下文。");
                    append(cloudAnswer);
                    return Task.FromResult(new QwenSearchResult(cloudAnswer, Array.Empty<QwenSearchSource>()));
                };
                await SendChat(cloudQuestion);
                CheckQwen(session.Turns.Count == 3 && window.qwenAnswer.Text == cloudAnswer && ReferenceEquals(session, window.qwenConversations.Current),
                    "本地和云端没有共用同一份对话记录。");
                window.qwenProvider.SelectedIndex = 0;
                window.PageScroll.ScrollToTop();
                var surface = (FrameworkElement)window.Content;
                window.ArrangeLayoutProbe((int)surface.ActualWidth, (int)surface.ActualHeight, "local-qwen");
                window.qwenQuery.Text = "画一张村庄的风景图";
                ClickFixtureAction(window.qwenSubmit);
                window.UpdateLayout();
                CheckQwen(window.qwenLocalBody.IsVisible && window.localQwenModelInfo.Text.Contains("Qwen-Image-2512") &&
                    window.localQwenPrompt.Text.Contains("村庄") && window.localQwenOutputPath == null,
                    "画图指令没有转为本地创作草稿或已提前生成。");
                await SendImage("画一座村庄");
                string original = window.localQwenOutputPath;
                CheckQwen(File.Exists(original) && latestSource == null && window.localQwenResultImage.Source != null,
                    "文生图未显示生成的 PNG。");
                byte[] before = File.ReadAllBytes(original);
                window.localQwenMode.SelectedIndex = (int)LocalQwenMode.Edit;
                window.UpdateLayout();
                await SendImage("把白天改成黄昏");
                CheckQwen(window.localQwenStatus.Text.Contains("选择要修改的图片"), "编辑没有要求输入图片。");
                window.SetLocalQwenSourceImage(original);
                await SendImage("把白天改成黄昏");
                CheckQwen(latestSource == original && File.Exists(window.localQwenOutputPath) &&
                    window.localQwenOutputPath != original && File.ReadAllBytes(original).SequenceEqual(before) &&
                    window.localQwenImageOutput.Visibility == Visibility.Visible && window.localQwenResultImage.Source != null,
                    "编辑未保留输入或未展示新 PNG。");
                string saved = Path.Combine(folder, "另存.png");
                window.CopyLocalQwenOutput(saved);
                CheckQwen(File.ReadAllBytes(saved).SequenceEqual(File.ReadAllBytes(window.localQwenOutputPath)),
                    "另存为 PNG 的内容不匹配。");
                ClickFixtureAction(window.qwenChatTab);
                CheckQwen(window.qwenQuery.IsVisible && ReferenceEquals(session, window.qwenConversations.Current) &&
                    session.Turns.Count == 3 && window.qwenAnswer.Text == cloudAnswer, "返回对话时遗失了上下文。");
                CheckQwen(calls.SequenceEqual(new[] { LocalQwenMode.Chat, LocalQwenMode.Chat, LocalQwenMode.Chat,
                    LocalQwenMode.Chat, LocalQwenMode.Generate, LocalQwenMode.Edit }), "使用了错误的模型或重复发起推理。");
                return "已验证同一输入框的本地与云端对话、共享上下文和草稿、模型准备、取消重试、图片生成、编辑与另存。";
            }
            finally
            {
                window.Close();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
