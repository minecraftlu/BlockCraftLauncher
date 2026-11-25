using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;
using Microsoft.Win32;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        QwenConversationStore localQwenHistory;
        ComboBox localQwenSessions;
        FrameworkElement localQwenChatToolbar;
        StackPanel localQwenTranscript;
        ScrollViewer localQwenScroll;
        Image localQwenSourceImage, localQwenResultImage;
        TextBlock localQwenInputLabel, localQwenOutputLabel;
        string localQwenInputPath, localQwenOutputPath, localQwenOutputSource;

        FrameworkElement BuildLocalConversation()
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(store.FilePath));
            localQwenHistory = new QwenConversationStore(Path.Combine(folder, "qwen-local-history.json"));
            localQwenSessions = new ComboBox { DisplayMemberPath = "Title", MinWidth = 128, MaxWidth = 220 };
            ApplySearchInputStyle(localQwenSessions, "切换本地对话");
            AutomationProperties.SetName(localQwenSessions, "本地对话列表");
            localQwenSessions.SelectionChanged += (sender, args) =>
            {
                var item = localQwenSessions.SelectedItem as QwenConversation;
                if (item == null || item.Id == localQwenHistory.Current.Id) return;
                localQwenHistory.Select(item.Id);
                localQwenPrompt.Clear();
                RenderLocalChat();
            };
            var retain = new CheckBox { Content = "在本机保存对话", IsChecked = localQwenHistory.HistoryEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "关闭后清除已保存的本地模型对话，本次窗口中的对话可继续使用。" };
            retain.Click += (s, e) =>
            {
                localQwenHistory.SetHistoryEnabled(retain.IsChecked == true);
                retain.IsChecked = localQwenHistory.HistoryEnabled;
                localQwenStatus.Text = localQwenHistory.PersistenceError;
            };
            localQwenChatToolbar = Row(localQwenSessions, Btn("新对话", () =>
            {
                localQwenHistory.NewSession();
                localQwenPrompt.Clear();
                RefreshLocalChatSessions();
                RenderLocalChat();
            }), retain);
            localQwenTranscript = new StackPanel();
            localQwenScroll = new ScrollViewer { Content = localQwenTranscript, MaxHeight = 270,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            AdjustQwenTranscriptHeight(PageScroll.ActualHeight);
            RefreshLocalChatSessions();
            RenderLocalChat();
            return Col(localQwenChatToolbar, localQwenScroll);
        }

        void RefreshLocalChatSessions()
        {
            localQwenSessions.ItemsSource = null;
            localQwenSessions.ItemsSource = localQwenHistory.Sessions;
            localQwenSessions.SelectedItem = localQwenHistory.Current;
        }

        void RenderLocalChat()
        {
            localQwenTranscript.Children.Clear();
            foreach (var turn in localQwenHistory.Current.Turns)
            {
                localQwenTranscript.Children.Add(ChatBubble(true, QwenSelectableText(turn.Question)));
                localQwenTranscript.Children.Add(ChatBubble(false, QwenSelectableText(turn.Answer)));
            }
            localQwenScroll.ScrollToEnd();
        }

        async Task SubmitLocalChatAsync(string question, IProgress<string> progress, CancellationToken token)
        {
            var session = localQwenHistory.Current;
            var messages = session.BuildChatTurns(question);
            localQwenTranscript.Children.Add(ChatBubble(true, QwenSelectableText(question)));
            var answerBox = QwenSelectableText("");
            localQwenTranscript.Children.Add(ChatBubble(false, answerBox));
            localQwenScroll.ScrollToEnd();
            var buffer = new StringBuilder();
            var clock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(65) };
            clock.Tick += (s, e) =>
            {
                string delta;
                lock (buffer) { if (buffer.Length == 0) return; delta = buffer.ToString(); buffer.Clear(); }
                bool follow = localQwenScroll.ScrollableHeight - localQwenScroll.VerticalOffset < 30;
                answerBox.AppendText(delta);
                if (follow) localQwenScroll.ScrollToEnd();
            };
            clock.Start();
            try
            {
                string answer = await TryRunLocalQwenExactCommandAsync(question, token);
                if (answer == null)
                {
                    Uri endpoint = null;
                    try { endpoint = await PrepareLocalModelAsync(LocalQwenMode.Chat, false, progress, token); }
                    catch (LocalModelNotInstalledException)
                    {
                        string project = KnownQwenInstallProject(question, out var command);
                        if (project == null) throw;
                        answer = await InstallKnownQwenProjectAsync(project, command, token);
                    }
                    if (endpoint != null)
                    {
                        token.ThrowIfCancellationRequested();
                        Action<string> append = delta => { lock (buffer) buffer.Append(delta); };
                        var reply = localQwenReply ?? RunLocalQwenAsync;
                        answer = await reply(endpoint, messages, append, token);
                    }
                }
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("模型没有返回回答，请重试。");
                localQwenHistory.CommitTurn(session, question, new QwenSearchResult(answer, null), false);
                localQwenPrompt.Clear();
                RefreshLocalChatSessions();
                localQwenStatus.Text = localQwenHistory.PersistenceError;
            }
            finally
            {
                clock.Stop();
                if (!localQwenClosed) RenderLocalChat();
            }
        }

        static Image LocalQwenPreview(double height)
        {
            return new Image { MaxHeight = height, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 5) };
        }

        FrameworkElement BuildLocalImageInput()
        {
            localQwenSourceImage = LocalQwenPreview(100);
            localQwenInputLabel = Txt("PNG 或 JPEG，最大 32 MiB，最长边 8192 像素，总像素不超过 1677 万。", 12, false, true);
            return Col(Row(Btn("选择原图", () =>
            {
                var picker = new OpenFileDialog { Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true };
                if (picker.ShowDialog(this) == true)
                {
                    try { SetLocalQwenSourceImage(picker.FileName); }
                    catch (Exception ex) { localQwenStatus.Text = "无法读取原图：" + ex.Message; }
                }
            }), localQwenInputLabel), localQwenSourceImage);
        }

        FrameworkElement BuildLocalImageOutput()
        {
            localQwenResultImage = LocalQwenPreview(300);
            localQwenOutputLabel = Txt("", 12, false, true);
            return Card(Col(localQwenResultImage, localQwenOutputLabel, Row(Btn("另存为 PNG", SaveLocalQwenImage))));
        }

        static BitmapSource ReadLocalQwenPreview(string path, bool checkInput)
        {
            using (var stream = File.OpenRead(path))
            {
                if (checkInput)
                {
                    if (stream.Length > 32L * 1024 * 1024) throw new InvalidDataException("文件超过 32 MiB。");
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (!(decoder is PngBitmapDecoder) && !(decoder is JpegBitmapDecoder)) throw new InvalidDataException("请选择 PNG 或 JPEG。");
                    var frame = decoder.Frames[0];
                    if (frame.PixelWidth > 8192 || frame.PixelHeight > 8192 || (long)frame.PixelWidth * frame.PixelHeight > 16777216)
                        throw new InvalidDataException("图片像素超出限制。");
                    stream.Position = 0;
                }
                var preview = new BitmapImage();
                preview.BeginInit();
                preview.CacheOption = BitmapCacheOption.OnLoad;
                preview.DecodePixelWidth = checkInput ? 240 : 480;
                preview.StreamSource = stream;
                preview.EndInit();
                preview.Freeze();
                return preview;
            }
        }

        void SetLocalQwenSourceImage(string path)
        {
            var preview = ReadLocalQwenPreview(path, true);
            localQwenInputPath = path;
            localQwenSourceImage.Source = preview;
            localQwenInputLabel.Text = Path.GetFileName(path);
            localQwenInputLabel.ToolTip = path;
        }

        async Task SubmitLocalImageAsync(string prompt, IProgress<string> progress, CancellationToken token)
        {
            bool edit = activeLocalMode == LocalQwenMode.Edit;
            Uri endpoint = await PrepareLocalModelAsync(activeLocalMode, false, progress, token);
            token.ThrowIfCancellationRequested();
            string folder = Path.Combine(LocalQwenRoot, "outputs");
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, "Qwen-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") +
                "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            string source = edit ? localQwenInputPath : null;
            if (localQwenGenerate != null) await localQwenGenerate(endpoint, prompt, source, destination, progress, token);
            else
            {
                using (var service = new LocalQwenImageService(endpoint))
                    await service.GenerateAsync(prompt, source, destination, progress, token);
            }
            token.ThrowIfCancellationRequested();
            localQwenResultImage.Source = ReadLocalQwenPreview(destination, false);
            localQwenOutputPath = destination;
            localQwenOutputSource = source;
            localQwenOutputLabel.Text = "已保存到 " + destination;
            localQwenStatus.Text = edit ? "修改完成。" : "图片已生成。";
            localQwenImageOutput.Visibility = Visibility.Visible;
        }

        void SaveLocalQwenImage()
        {
            if (localQwenOutputPath == null) return;
            var save = new SaveFileDialog { Filter = "PNG 图片 (*.png)|*.png", FileName = Path.GetFileName(localQwenOutputPath),
                DefaultExt = ".png", AddExtension = true, OverwritePrompt = true };
            if (save.ShowDialog(this) != true) return;
            try
            {
                CopyLocalQwenOutput(save.FileName);
                localQwenStatus.Text = "图片已保存到 " + save.FileName;
            }
            catch (Exception ex) { localQwenStatus.Text = "保存失败：" + ex.Message; }
        }

        void CopyLocalQwenOutput(string destination)
        {
            string target = Path.GetFullPath(destination);
            if (string.Equals(target, localQwenOutputPath, StringComparison.OrdinalIgnoreCase)) return;
            if (string.Equals(target, localQwenOutputSource, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择新的文件名，以保留原图。");
            string temp = Path.Combine(Path.GetDirectoryName(target), ".blockcraft-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(localQwenOutputPath, temp);
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
