using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlockCraftLauncher.Wpf.Services;
using Microsoft.Win32;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        StackPanel qwenMediaPanel, mediaReferenceRow;
        ComboBox mediaMode;
        TextBox mediaPrompt;
        TextBlock mediaStatus, mediaInputName;
        Image mediaInputPreview, mediaImage;
        MediaElement mediaVideo;
        Border mediaOutputCard;
        Button mediaPick, mediaSubmit, mediaStop, mediaResume, mediaDismiss, mediaSave, mediaPlay;
        CancellationTokenSource mediaRequest;
        string mediaInputPath, mediaOutputPath, mediaTempFolder, qwenMediaTaskPath;
        bool mediaOutputIsVideo, mediaPlaying, mediaSaving, mediaDisposed;
        QwenMediaResult mediaPendingOutput;
        MediaPendingTask mediaPendingTask;
        Func<QwenMediaService> qwenMediaClientFactory;

        sealed class MediaPendingTask
        {
            public string Id { get; set; }
            public string Endpoint { get; set; }
            public QwenMediaKind Kind { get; set; }
        }

        QwenMediaKind SelectedMediaKind => mediaMode.SelectedItem is ComboBoxItem item &&
            item.Tag is QwenMediaKind kind ? kind : QwenMediaKind.TextToImage;
        static bool UsesMediaImage(QwenMediaKind kind) =>
            kind == QwenMediaKind.ImageEdit || kind == QwenMediaKind.ImageToVideo;
        string PendingMediaTaskFile => qwenMediaTaskPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher", "qwen-media-task.json");

        UIElement BuildQwenMediaPage()
        {
            if (qwenMediaPanel != null) return qwenMediaPanel;
            mediaMode = new ComboBox { Width = 138, Margin = new Thickness(0, 0, 8, 6) };
            foreach (var mode in new[] {
                (QwenMediaKind.TextToImage, "文字生成图片"), (QwenMediaKind.ImageEdit, "编辑图片"),
                (QwenMediaKind.TextToVideo, "文字生成视频"), (QwenMediaKind.ImageToVideo, "图片生成视频") })
                mediaMode.Items.Add(new ComboBoxItem { Tag = mode.Item1, Content = mode.Item2 });
            mediaMode.SelectedIndex = 0;
            AutomationProperties.SetName(mediaMode, "创作类型");
            mediaPrompt = new TextBox { MinHeight = 55, MaxHeight = 110, MaxLength = QwenMediaService.MaxPrompt, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ApplySearchInputStyle(mediaPrompt, "描述画面、动作或需要修改的地方");
            AutomationProperties.SetName(mediaPrompt, "创作描述");
            mediaInputPreview = new Image { MaxHeight = 84, MaxWidth = 150,
                HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
            mediaInputName = Txt("", 12, false, true);
            mediaPick = Btn("选择原图", PickQwenMediaImage);
            mediaReferenceRow = Col(Row(mediaPick, mediaInputName), mediaInputPreview);
            mediaStatus = Txt("", 12, false, true);
            AutomationProperties.SetLiveSetting(mediaStatus, AutomationLiveSetting.Polite);
            mediaSubmit = Btn("生成作品", async () => await RunQwenMediaAsync(false), true);
            mediaStop = Btn("停止等待", StopQwenMedia);
            mediaResume = Btn("继续获取结果", async () => await RunQwenMediaAsync(true));
            mediaDismiss = Btn("移除待取任务", () =>
            {
                if (mediaRequest != null) return;
                ForgetPendingMediaTask();
                mediaPendingOutput = null;
                SetMediaStatus("已移除本机记录。");
                RefreshMediaActions();
            });
            var options = Card(Col(Row(mediaMode, Btn("连接设置", OpenQwenConnectionSettings)),
                mediaReferenceRow, mediaPrompt, Txt("生成时将描述和选中的原图发送至百炼，按所用模型计费。", 12, false, true),
                Row(mediaSubmit, mediaStop, mediaResume, mediaDismiss), mediaStatus));
            mediaImage = new Image { MaxHeight = 320, Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left };
            mediaVideo = new MediaElement { Height = 220, Stretch = Stretch.Uniform,
                LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close };
            mediaVideo.MediaEnded += (s, e) => { mediaPlaying = false; mediaVideo.Stop(); };
            mediaVideo.MediaFailed += (s, e) =>
            {
                if (mediaDisposed || !mediaOutputIsVideo || mediaVideo.Source == null) return;
                mediaPlaying = false;
                SetMediaStatus("Windows 未能预览这个视频，可以保存 MP4 后用本机播放器打开。");
            };
            mediaPlay = Btn("播放 / 暂停", ToggleQwenMediaPlayback);
            mediaSave = Btn("另存为", async () => await SaveQwenMediaDialogAsync());
            mediaOutputCard = Card(Col(Row(Txt("创作结果", 16, true), mediaSave, mediaPlay), mediaImage, mediaVideo));
            mediaOutputCard.Visibility = Visibility.Collapsed;
            qwenMediaPanel = Col(options, mediaOutputCard);
            mediaPendingTask = ReadPendingMediaTask();
            if (mediaPendingTask != null)
            {
                mediaMode.SelectedIndex = mediaPendingTask.Kind == QwenMediaKind.ImageToVideo ? 3 : 2;
                SetMediaStatus("有一个待取回的视频。可以继续获取结果。");
            }
            mediaMode.SelectionChanged += (s, e) => RefreshMediaActions();
            RefreshMediaActions();
            return qwenMediaPanel;
        }

        void SetMediaStatus(string text)
        {
            mediaStatus.Text = text;
            mediaStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        bool SetQwenMediaDraft(string kind, string prompt)
        {
            if ((prompt ?? "").Trim().Length > QwenMediaService.MaxPrompt)
            {
                SetMediaStatus("创作描述最多 1500 个字符，请缩短后再试。原文已保留在聊天框。");
                return false;
            }
            if (mediaRequest != null || mediaPendingTask != null || mediaPendingOutput != null)
            {
                SetMediaStatus("请先完成当前创作或移除待取任务，再开始新的创作。");
                return false;
            }
            int index;
            switch (kind)
            {
                case "image": index = 0; break;
                case "edit": index = 1; break;
                case "video": index = 2; break;
                case "image-video": index = 3; break;
                default: return false;
            }
            mediaMode.SelectedIndex = index;
            mediaPrompt.Text = prompt ?? "";
            SetMediaStatus(UsesMediaImage(SelectedMediaKind) && mediaInputPath == null ?
                "选择原图，确认描述后点「生成作品」。" : "确认描述后点「生成作品」。");
            mediaPrompt.Focus();
            return true;
        }

        void RefreshMediaActions()
        {
            bool busy = mediaRequest != null;
            bool pending = mediaPendingTask != null || mediaPendingOutput != null;
            mediaReferenceRow.Visibility = UsesMediaImage(SelectedMediaKind) ? Visibility.Visible : Visibility.Collapsed;
            mediaSubmit.IsEnabled = !busy && !pending;
            mediaStop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            mediaResume.Visibility = mediaDismiss.Visibility = !busy && pending ? Visibility.Visible : Visibility.Collapsed;
            mediaMode.IsEnabled = mediaPrompt.IsEnabled = mediaPick.IsEnabled = !busy && !pending;
            mediaSave.IsEnabled = !mediaSaving && mediaOutputPath != null;
            mediaPlay.Visibility = mediaOutputIsVideo ? Visibility.Visible : Visibility.Collapsed;
        }

        void PickQwenMediaImage()
        {
            var picker = new OpenFileDialog { Title = "选择原图", Filter = "PNG 或 JPEG 图片|*.png;*.jpg;*.jpeg", CheckFileExists = true };
            if (picker.ShowDialog(this) != true) return;
            try { SetQwenMediaInput(picker.FileName); SetMediaStatus(""); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is NotSupportedException || ex is ArgumentException ||
                ex is FormatException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            { SetMediaStatus(SelectedMediaKind == QwenMediaKind.ImageToVideo ?
                "请选择不超过 20 MB 的 JPEG 或不透明 PNG 图片，两边应为 240 至 8000 像素。" :
                "请选择不超过 10 MB 的有效 PNG 或 JPEG 图片。"); }
        }

        void SetQwenMediaInput(string path)
        {
            using (var stream = File.OpenRead(path))
            {
                long limit = SelectedMediaKind == QwenMediaKind.ImageToVideo ? 20L << 20 : 10L << 20;
                if (stream.Length <= 0 || stream.Length > limit) throw new InvalidDataException();
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (!(decoder is PngBitmapDecoder || decoder is JpegBitmapDecoder)) throw new InvalidDataException();
                var frame = decoder.Frames[0];
                if (SelectedMediaKind == QwenMediaKind.ImageToVideo &&
                    (frame.PixelWidth < 240 || frame.PixelHeight < 240 || frame.PixelWidth > 8000 || frame.PixelHeight > 8000))
                    throw new InvalidDataException();
            }
            mediaInputPreview.Source = ReadMediaBitmap(path, 180);
            mediaInputName.Text = Path.GetFileName(path);
            mediaInputPath = path;
        }

        static BitmapSource ReadMediaBitmap(string path, int maxEdge)
        {
            int width, height;
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                width = frame.PixelWidth;
                height = frame.PixelHeight;
            }
            using (var file = File.OpenRead(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (width >= height && width > maxEdge) bitmap.DecodePixelWidth = maxEdge;
                else if (height > maxEdge) bitmap.DecodePixelHeight = maxEdge;
                bitmap.StreamSource = file;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        void AcceptMediaTask(QwenMediaKind kind, Uri endpoint, string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !Regex.IsMatch(id, @"\A[a-zA-Z0-9_-]{1,128}\z")) return;
            mediaPendingTask = new MediaPendingTask { Kind = kind, Endpoint = endpoint.AbsoluteUri, Id = id };
            string path = PendingMediaTaskFile, temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(mediaPendingTask), new UTF8Encoding(false));
                try { File.Move(temp, path); }
                catch (IOException) when (File.Exists(path)) { File.Replace(temp, path, null); }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            finally { DeleteMediaTemp(temp); }
        }

        MediaPendingTask ReadPendingMediaTask()
        {
            try
            {
                var f = new FileInfo(PendingMediaTaskFile);
                if (!f.Exists || f.Length > 8192) return null;
                var item = new JavaScriptSerializer().Deserialize<MediaPendingTask>(File.ReadAllText(f.FullName));
                return item != null && (item.Kind == QwenMediaKind.ImageToVideo || item.Kind == QwenMediaKind.TextToVideo) &&
                    Regex.IsMatch(item.Id ?? "", @"\A[a-zA-Z0-9_-]{1,128}\z") &&
                    QwenSearchService.TryGetEndpoint(item.Endpoint, out _) ? item : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                ex is InvalidOperationException || ex is FormatException) { return null; }
        }

        void ForgetPendingMediaTask()
        {
            var task = mediaPendingTask;
            mediaPendingTask = null;
            if (task != null && ReadPendingMediaTask()?.Id == task.Id) DeleteMediaTemp(PendingMediaTaskFile);
        }

        async Task RunQwenMediaAsync(bool resume)
        {
            if (mediaDisposed || mediaRequest != null || currentPage != QwenSearchPage) return;
            string prompt = mediaPrompt.Text.Trim();
            var kind = SelectedMediaKind;
            if (!resume && (mediaPendingTask != null || mediaPendingOutput != null)) return;
            if (resume && mediaPendingTask == null && mediaPendingOutput == null) return;
            if (!resume && (prompt.Length == 0 || UsesMediaImage(kind) && mediaInputPath == null))
            {
                SetMediaStatus(prompt.Length == 0 ? "请填写创作描述。" : "请先选择需要处理的原图。");
                return;
            }
            QwenConnectionSettings connection = null;
            string key = null;
            Uri endpoint = null;
            if (mediaPendingOutput == null)
            {
                if (!TryReadQwenCredentials(out connection, out key, requireChatModel: false)) return;
                if (!QwenSearchService.TryGetEndpoint(connection.Endpoint, out endpoint)) return;
                if (resume && !string.Equals(mediaPendingTask.Endpoint, endpoint.AbsoluteUri, StringComparison.Ordinal))
                {
                    SetMediaStatus("这个视频使用了另一个百炼接口。请恢复提交时的接口地址，再继续获取。");
                    return;
                }
            }
            var operation = new CancellationTokenSource();
            mediaRequest = operation;
            RefreshMediaActions();
            SetMediaStatus(resume ? "正在获取之前的作品…" : "正在提交创作请求…");
            var progress = new Progress<string>(message =>
            {
                if (ReferenceEquals(mediaRequest, operation) && !operation.IsCancellationRequested) SetMediaStatus(message);
            });
            string file = null;
            try
            {
                using var service = qwenMediaClientFactory?.Invoke() ?? new QwenMediaService(settings);
                QwenMediaResult result = mediaPendingOutput;
                if (result == null)
                {
                    result = resume
                        ? await service.ResumeVideoAsync(mediaPendingTask.Kind, mediaPendingTask.Id, key, connection.Endpoint, progress, operation.Token)
                        : await service.GenerateAsync(kind, prompt, UsesMediaImage(kind) ? mediaInputPath : null,
                            key, connection.Endpoint, progress, operation.Token, id =>
                            {
                                if (!Dispatcher.HasShutdownStarted)
                                    Dispatcher.Invoke(() => AcceptMediaTask(kind, endpoint, id));
                            });
                    mediaPendingOutput = result;
                }
                operation.Token.ThrowIfCancellationRequested();
                if (mediaTempFolder == null) mediaTempFolder = Path.Combine(Path.GetTempPath(), "BlockCraft-Qwen-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(mediaTempFolder);
                file = Path.Combine(mediaTempFolder, Guid.NewGuid().ToString("N") + result.Extension);
                SetMediaStatus("正在下载作品…");
                await service.DownloadAsync(result, file, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                SetMediaOutput(file, result.IsVideo);
                file = null;
                mediaPendingOutput = null;
                ForgetPendingMediaTask();
                SetMediaStatus(result.IsVideo ? "视频已生成。点「播放 / 暂停」预览，也可以另存为 MP4。" : "图片已生成，可以另存为 PNG。");
            }
            catch (OperationCanceledException)
            {
                SetMediaStatus(mediaPendingTask == null ?
                    "已停止等待。云端已受理的生成仍可能计费；再次点生成将提交新请求。" :
                    "已停止等待，云端可能继续生成并计费。可点「继续获取结果」取回同一任务。");
            }
            catch (QwenSearchException ex)
            {
                if (Equals(ex.Data["QwenMediaTaskEnded"], true)) ForgetPendingMediaTask();
                SetMediaStatus(ex.Message + (!resume && !QwenMediaService.IsVideoKind(kind) && mediaPendingOutput == null ?
                    " 如请求已受理，云端仍可能计费。" : ""));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is FormatException ||
                ex is NotSupportedException || ex is InvalidOperationException) { SetMediaStatus("作品暂未能下载或预览，请检查网络和本机缓存目录后重试。"); }
            catch (Exception) { SetMediaStatus("创作请求未能完成，请检查连接设置后重试。"); }
            finally
            {
                DeleteMediaTemp(file);
                mediaRequest = null;
                operation.Dispose();
                if (!mediaDisposed) RefreshMediaActions();
                else RemoveMediaTempFolder();
            }
        }

        void SetMediaOutput(string path, bool video)
        {
            var bitmap = video ? null : ReadMediaBitmap(path, 960);
            mediaVideo.Close();
            mediaVideo.Source = null;
            mediaPlaying = false;
            var previous = mediaOutputPath;
            mediaOutputPath = path;
            mediaOutputIsVideo = video;
            mediaImage.Source = bitmap;
            mediaImage.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
            mediaVideo.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            if (video) { mediaVideo.Source = new Uri(path); mediaVideo.ScrubbingEnabled = true; mediaVideo.Pause(); }
            mediaOutputCard.Visibility = Visibility.Visible;
            DeleteMediaTemp(previous);
        }

        void ToggleQwenMediaPlayback()
        {
            if (!mediaOutputIsVideo || mediaVideo.Source == null) return;
            if (mediaPlaying) mediaVideo.Pause();
            else mediaVideo.Play();
            mediaPlaying = !mediaPlaying;
        }

        async Task SaveQwenMediaDialogAsync()
        {
            if (mediaOutputPath == null || mediaSaving) return;
            var save = new SaveFileDialog { Title = "保存作品", OverwritePrompt = true, AddExtension = true,
                DefaultExt = mediaOutputIsVideo ? ".mp4" : ".png",
                Filter = mediaOutputIsVideo ? "MP4 视频|*.mp4" : "PNG 图片|*.png",
                FileName = "BlockCraft-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") };
            if (save.ShowDialog(this) != true) return;
            try
            {
                mediaSaving = true;
                RefreshMediaActions();
                await SaveQwenMediaOutputAsync(save.FileName);
                SetMediaStatus("已保存到 " + save.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { SetMediaStatus("保存失败，请选择可写入的文件夹后重试。"); }
            finally { mediaSaving = false; if (!mediaDisposed) RefreshMediaActions(); }
        }

        async Task SaveQwenMediaOutputAsync(string destination)
        {
            string path = Path.GetFullPath(destination);
            if (string.Equals(path, mediaOutputPath, StringComparison.OrdinalIgnoreCase)) return;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = new FileStream(mediaOutputPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(target);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { DeleteMediaTemp(temporary); }
        }

        void StopQwenMedia()
        {
            mediaRequest?.Cancel();
            if (mediaRequest != null) SetMediaStatus("已请求停止等待。云端已受理的任务可能继续生成并计费。");
        }

        void CloseQwenMediaPage()
        {
            if (qwenMediaPanel == null) return;
            StopQwenMedia();
            if (mediaVideo.Source != null) { mediaVideo.Pause(); mediaPlaying = false; }
        }

        static void DeleteMediaTemp(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void RemoveMediaTempFolder()
        {
            if (mediaTempFolder == null) return;
            try { Directory.Delete(mediaTempFolder, true); mediaTempFolder = null; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        void DisposeQwenMediaPage()
        {
            if (mediaDisposed || qwenMediaPanel == null) return;
            mediaDisposed = true;
            mediaRequest?.Cancel();
            mediaVideo.Close();
            mediaVideo.Source = null;
            RemoveMediaTempFolder();
        }
    }
}
