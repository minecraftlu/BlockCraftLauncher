using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        enum LocalQwenMode { Chat, Generate, Edit }
        ComboBox localQwenMode;
        TextBlock localQwenDirectory, localQwenModelInfo, localQwenStatus;
        TextBox localQwenPrompt;
        Button localQwenSubmit, localQwenStop;
        FrameworkElement localQwenSettings, localQwenChatPanel, localQwenImageInput, localQwenImageOutput;
        ProgressBar localQwenProgress;
        CancellationTokenSource localQwenOperation;
        LocalModelRuntime localQwenRuntime;
        LocalQwenImageInstaller localQwenInstaller;
        readonly string[] localQwenDrafts = new string[3];
        LocalQwenMode activeLocalMode;
        bool localQwenClosed;

        Func<LocalQwenMode, string, bool, IProgress<string>, CancellationToken, Task<Uri>> localQwenPrepare;
        Func<Uri, IReadOnlyList<QwenChatTurn>, Action<string>, CancellationToken, Task<string>> localQwenReply;
        Func<Uri, string, string, string, IProgress<string>, CancellationToken, Task> localQwenGenerate;

        string LocalQwenRoot => LocalModelRuntime.ManagedPath(settings.LocalQwenDirectory);

        UIElement BuildLocalQwenPage()
        {
            localQwenMode = new ComboBox { MinWidth = 150, Margin = new Thickness(0, 0, 8, 5) };
            foreach (var name in new[] { "聊天与下载指令", "生成图片", "修改图片" }) localQwenMode.Items.Add(name);
            ApplySearchInputStyle(localQwenMode, "选择本地模型用途");
            AutomationProperties.SetName(localQwenMode, "本地模型用途");
            localQwenMode.SelectedIndex = 0;
            localQwenModelInfo = Txt("", 13, false, true);
            localQwenDirectory = Txt("", 12, false, true);
            var folder = new Expander {
                Header = QwenSectionCaption("模型存储位置"),
                Content = Col(localQwenDirectory, Row(Btn("选择文件夹", PickLocalQwenDirectory),
                    Btn("使用默认位置", () => SetLocalQwenDirectory(""))))
            };
            folder.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            localQwenSettings = Col(Row(localQwenMode, Btn("准备模型", async () => await ExecuteLocalQwenAsync(true))),
                localQwenModelInfo, folder);
            localQwenPrompt = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 60, MaxHeight = 145, MaxLength = QwenChatInputLimit, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            ApplySearchInputStyle(localQwenPrompt, "输入内容后按 Ctrl + Enter 发送");
            AutomationProperties.SetName(localQwenPrompt, "本地 AI 提示词或下载指令");
            localQwenPrompt.PreviewKeyDown += async (sender, args) =>
            {
                if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                args.Handled = true;
                await ExecuteLocalQwenAsync(false);
            };
            localQwenSubmit = Btn("发送", async () => await ExecuteLocalQwenAsync(false), true);
            localQwenStop = Btn("停止", () => localQwenOperation?.Cancel());
            localQwenStatus = Txt("", 13);
            AutomationProperties.SetLiveSetting(localQwenStatus, AutomationLiveSetting.Polite);
            localQwenProgress = new ProgressBar { Height = 4, Margin = new Thickness(0, 6, 0, 4) };
            localQwenChatPanel = BuildLocalConversation();
            localQwenImageInput = BuildLocalImageInput();
            localQwenImageOutput = BuildLocalImageOutput();
            localQwenMode.SelectionChanged += (sender, args) =>
            {
                if (localQwenMode.SelectedIndex < 0) return;
                localQwenDrafts[(int)activeLocalMode] = localQwenPrompt.Text;
                activeLocalMode = (LocalQwenMode)localQwenMode.SelectedIndex;
                localQwenPrompt.Text = localQwenDrafts[(int)activeLocalMode] ?? "";
                localQwenStatus.Text = "";
                RefreshLocalQwenControls();
            };
            RefreshLocalQwenControls();
            return Col(Card(localQwenSettings), localQwenChatPanel, localQwenImageInput,
                Card(Col(localQwenPrompt, Row(localQwenSubmit, localQwenStop), localQwenProgress, localQwenStatus)),
                localQwenImageOutput);
        }

        void RefreshLocalQwenControls()
        {
            bool busy = localQwenOperation != null;
            bool chat = activeLocalMode == LocalQwenMode.Chat, edit = activeLocalMode == LocalQwenMode.Edit;
            localQwenSettings.IsEnabled = !busy;
            localQwenSubmit.IsEnabled = !busy;
            localQwenPrompt.IsReadOnly = busy;
            localQwenChatToolbar.IsEnabled = !busy;
            localQwenImageInput.IsEnabled = !busy;
            localQwenStop.Visibility = localQwenProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            localQwenProgress.IsIndeterminate = busy;
            localQwenChatPanel.Visibility = chat ? Visibility.Visible : Visibility.Collapsed;
            localQwenImageInput.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
            localQwenImageOutput.Visibility = !chat && localQwenOutputPath != null ? Visibility.Visible : Visibility.Collapsed;
            string submit = chat ? "发送" : edit ? "修改图片" : "生成图片";
            localQwenSubmit.Content = submit;
            AutomationProperties.SetName(localQwenSubmit, submit);
            localQwenPrompt.ToolTip = chat ? "试试「帮我找一个提升帧率的模组」或「帮我安装一个提升帧率的模组」\nCtrl + Enter 发送" :
                edit ? "例如：把截图里的白天改成黄昏" : "例如：画一座雪夜里的方块小屋";
            try
            {
                string root = LocalQwenRoot;
                localQwenDirectory.Text = root;
                if (chat) localQwenModelInfo.Text = "Qwen3.5-4B · 首次下载约 3.3 GB 模型；需要时另下载约 1.7 GB 的 Ollama。";
                else
                {
                    string model = edit ? "Qwen-Image-Edit-2511" : "Qwen-Image-2512";
                    long bytes = LocalQwenImageInstaller.DownloadBytes(root, edit);
                    string size = LocalQwenImageInstaller.IsInstalled(root, edit) ? "已安装" :
                        "待下载约 " + (bytes / 1000000000d).ToString("0.0") + " GB，解压需要额外空间";
                    localQwenModelInfo.Text = model + " · " + size + "。需要兼容 CUDA 12.6 的 NVIDIA 显卡和驱动。";
                }
            }
            catch (Exception ex) { localQwenModelInfo.Text = "请检查模型存储位置。"; localQwenDirectory.Text = ex.Message; }
        }

        void PickLocalQwenDirectory()
        {
            using (var picker = new System.Windows.Forms.FolderBrowserDialog {
                Description = "选择存放模型的文件夹，可以选择移动硬盘。", ShowNewFolderButton = true
            })
            {
                if (Directory.Exists(settings.LocalQwenDirectory)) picker.SelectedPath = settings.LocalQwenDirectory;
                if (picker.ShowDialog() == System.Windows.Forms.DialogResult.OK) SetLocalQwenDirectory(picker.SelectedPath);
            }
        }

        void SetLocalQwenDirectory(string directory)
        {
            if (localQwenOperation != null || directory == settings.LocalQwenDirectory) return;
            string previous = settings.LocalQwenDirectory;
            try
            {
                if (directory.Length != 0 && !Directory.Exists(directory)) throw new DirectoryNotFoundException("请选择已连接的磁盘或文件夹。");
                settings.LocalQwenDirectory = directory;
                store.Save(settings);
            }
            catch (Exception ex)
            {
                settings.LocalQwenDirectory = previous;
                localQwenStatus.Text = "无法保存模型位置：" + ex.Message;
                return;
            }
            localQwenRuntime?.Dispose();
            localQwenRuntime = null;
            localQwenInstaller?.Dispose();
            localQwenInstaller = null;
            localQwenStatus.Text = "模型位置已切换。选择原目录可继续使用其中的模型。";
            RefreshLocalQwenControls();
        }

        async Task<Uri> PrepareLocalModelAsync(LocalQwenMode mode, bool install, IProgress<string> progress, CancellationToken token)
        {
            string directory = settings.LocalQwenDirectory;
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                throw new DirectoryNotFoundException("模型磁盘未连接。请连接后重试，或选择其他存储位置。");
            if (localQwenPrepare != null) return await localQwenPrepare(mode, directory, install, progress, token);
            if (mode == LocalQwenMode.Chat)
            {
                if (localQwenRuntime == null) localQwenRuntime = new LocalModelRuntime(settings);
                return await localQwenRuntime.EnsureReadyAsync(directory, install, progress, token);
            }
            string root = LocalQwenRoot;
            bool edit = mode == LocalQwenMode.Edit;
            if (!install && !LocalQwenImageInstaller.IsInstalled(root, edit))
                throw new InvalidOperationException("请先点「准备模型」，完成下载后即可创作。");
            if (localQwenInstaller == null) localQwenInstaller = new LocalQwenImageInstaller(root, settings);
            return await localQwenInstaller.EnsureReadyAsync(edit, progress, token);
        }

        async Task ExecuteLocalQwenAsync(bool install)
        {
            if (localQwenClosed || localQwenOperation != null) return;
            string prompt = localQwenPrompt.Text.Trim();
            if (!install && prompt.Length == 0) { localQwenStatus.Text = "先输入问题、下载指令或画面描述。"; localQwenPrompt.Focus(); return; }
            if (!install && activeLocalMode == LocalQwenMode.Edit && !File.Exists(localQwenInputPath))
            { localQwenStatus.Text = "请先选择要修改的图片。"; return; }
            var operation = new CancellationTokenSource();
            localQwenOperation = operation;
            RefreshLocalQwenControls();
            localQwenStatus.Text = install ? "正在准备模型…" : "正在处理…";
            var progress = new Progress<string>(message =>
            {
                if (!localQwenClosed && localQwenOperation == operation && !operation.IsCancellationRequested) localQwenStatus.Text = message;
            });
            try
            {
                if (install)
                {
                    await PrepareLocalModelAsync(activeLocalMode, true, progress, operation.Token);
                    operation.Token.ThrowIfCancellationRequested();
                    localQwenStatus.Text = "模型已就绪。";
                }
                else if (activeLocalMode == LocalQwenMode.Chat) await SubmitLocalChatAsync(prompt, progress, operation.Token);
                else await SubmitLocalImageAsync(prompt, progress, operation.Token);
            }
            catch (OperationCanceledException) { if (!localQwenClosed) localQwenStatus.Text = "已停止。可重新准备模型或再次发送。"; }
            catch (Exception ex) { if (!localQwenClosed) localQwenStatus.Text = "操作未完成：" + ex.Message; }
            finally
            {
                localQwenOperation = null;
                operation.Dispose();
                if (!localQwenClosed) RefreshLocalQwenControls();
            }
        }

        void DisposeLocalQwen()
        {
            localQwenClosed = true;
            localQwenOperation?.Cancel();
            localQwenRuntime?.Dispose();
            localQwenInstaller?.Dispose();
        }
    }
}
