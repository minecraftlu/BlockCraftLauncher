using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        const int QwenChatInputLimit = 8000;
        QwenConversationStore qwenConversations;
        string qwenHistoryPath;
        ComboBox qwenSessions;
        CheckBox qwenRetainHistory, qwenWebSearch;
        Button qwenNewChat, qwenDeleteChat, qwenClearChats, qwenConfirmDelete, qwenRegenerate;
        enum QwenAssistantTab { Web, Chat, Media, Local }
        QwenAssistantTab qwenSelectedTab = QwenAssistantTab.Web;
        Button qwenWebTab, qwenChatTab, qwenMediaTab, qwenLocalTab;
        Grid qwenWebBody;
        Func<UIElement> qwenWebPageFactory;
        StackPanel qwenChatBody, qwenTranscript, qwenMediaBody, qwenLocalBody;
        ScrollViewer qwenChatScroll;
        FrameworkElement qwenDeletePrompt, qwenAnswerActions;
        TextBlock qwenDeleteLabel, qwenChatIntro;
        bool qwenDeleteAll, qwenRefreshingSessions;
        Func<IReadOnlyList<QwenChatTurn>, string, string, string, bool, Action<string>,
            CancellationToken, Task<QwenSearchResult>> qwenRunChat;

        UIElement BuildQwenSearchPage()
        {
            if (qwenPage == null)
            {
                qwenWebTab = Btn("千问官网", () => SwitchQwenTab(QwenAssistantTab.Web));
                qwenChatTab = Btn("聊天与指令", () => SwitchQwenTab(QwenAssistantTab.Chat));
                qwenMediaTab = Btn("百炼创作", SelectQwenMediaTab);
                qwenLocalTab = Btn("本地 AI", () => SwitchQwenTab(QwenAssistantTab.Local));
                qwenChatTab.ToolTip = "搜索、安装游戏和资源，或通过百炼 API 对话";
                qwenMediaTab.ToolTip = "通过百炼 API 生成和编辑图片、生成视频";
                var heading = (FrameworkElement)PageHeading("千问 AI 助手", null);
                heading.Margin = new Thickness(0, 0, 12, 0);
                var header = Row(heading, qwenWebTab, qwenChatTab, qwenMediaTab, qwenLocalTab);

                qwenWebBody = new Grid();
                qwenChatBody = new StackPanel { Visibility = Visibility.Collapsed };
                qwenMediaBody = new StackPanel { Visibility = Visibility.Collapsed };
                qwenLocalBody = new StackPanel { Visibility = Visibility.Collapsed };
                qwenPage = new Grid();
                qwenPage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                qwenPage.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                qwenPage.Children.Add(header);
                foreach (var body in new FrameworkElement[] { qwenWebBody, qwenChatBody, qwenMediaBody, qwenLocalBody })
                {
                    Grid.SetRow(body, 1);
                    qwenPage.Children.Add(body);
                }
                PageScroll.SizeChanged += (sender, args) => AdjustQwenTranscriptHeight(args.NewSize.Height);
            }
            SwitchQwenTab(qwenSelectedTab);
            return qwenPage;
        }

        void BuildQwenChatBody()
        {
            BuildQwenComposer();
            string path = qwenHistoryPath;
            if (path == null && !string.IsNullOrWhiteSpace(qwenSettingsPath))
                path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(qwenSettingsPath)), "chat-history.json");
            qwenConversations = new QwenConversationStore(path);
            qwenSessions = new ComboBox { DisplayMemberPath = "Title", MinWidth = 125, MaxWidth = 210 };
            ApplySearchInputStyle(qwenSessions, "选择对话");
            AutomationProperties.SetName(qwenSessions, "选择对话");
            qwenSessions.SelectionChanged += (sender, args) =>
            {
                if (qwenRefreshingSessions || !(qwenSessions.SelectedItem is QwenConversation next) ||
                    next.Id == qwenConversations.Current.Id) return;
                SwitchQwenSession(() => qwenConversations.Select(next.Id));
            };
            qwenNewChat = Btn("新对话", () => SwitchQwenSession(() => qwenConversations.NewSession()));
            qwenDeleteChat = Btn("删除对话", () => AskToDeleteChat(false));
            qwenClearChats = Btn("清空全部对话", () => AskToDeleteChat(true));
            qwenDeleteLabel = Txt("", 13);
            qwenConfirmDelete = Btn("确认删除", () =>
            {
                if (qwenDeletePrompt.Visibility != Visibility.Visible) return;
                SwitchQwenSession(() =>
                {
                    if (qwenDeleteAll) qwenConversations.Clear();
                    else qwenConversations.Delete(qwenConversations.Current.Id);
                });
            });
            qwenDeletePrompt = Row(qwenDeleteLabel, qwenConfirmDelete,
                Btn("保留对话", () => qwenDeletePrompt.Visibility = Visibility.Collapsed));
            qwenDeletePrompt.Visibility = Visibility.Collapsed;

            qwenTranscript = new StackPanel();
            qwenSources = new StackPanel();
            qwenAnswer = QwenSelectableText("");
            AutomationProperties.SetName(qwenAnswer, "千问回答");
            qwenRegenerate = Btn("重新回答", async () =>
            {
                var last = qwenConversations.Current.Turns.LastOrDefault();
                if (last != null) await SubmitQwenAsync(last.Question, true);
            });
            qwenAnswerActions = Row(Btn("复制回答", () => CopyQwenAnswer(qwenAnswer.Text)), qwenRegenerate);
            qwenResult = ChatBubble(false, Col(qwenAnswer, qwenSources, qwenAnswerActions));
            qwenChatScroll = new ScrollViewer {
                Content = Col(qwenTranscript, qwenResult), MaxHeight = 270,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 4, 0, 4)
            };
            qwenWebSearch = new CheckBox { Content = "联网搜索", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            qwenRetainHistory = new CheckBox {
                Content = "在本机保存对话", IsChecked = qwenConversations.HistoryEnabled,
                ToolTip = "关闭后将清除本机保存的历史，当前窗口中的对话仍可继续。"
            };
            qwenRetainHistory.Click += (sender, args) =>
            {
                qwenConversations.SetHistoryEnabled(qwenRetainHistory.IsChecked == true);
                qwenRetainHistory.IsChecked = qwenConversations.HistoryEnabled;
                SetQwenSearchStatus(qwenConversations.PersistenceError);
            };
            var historyOptions = new Expander {
                Header = QwenSectionCaption("对话设置"),
                Content = Row(qwenRetainHistory, qwenClearChats), Margin = new Thickness(0, 2, 0, 8)
            };
            historyOptions.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            var shortcuts = Txt("Ctrl + Enter 发送", 12, false, true);
            shortcuts.Margin = new Thickness(14, 0, 0, 0);
            shortcuts.VerticalAlignment = VerticalAlignment.Center;
            qwenChatIntro = Txt("试试「查找钠模组」「给当前实例装钠」或「下载 Minecraft 1.21.1」。", 14, false, true);
            qwenChatIntro.Margin = new Thickness(2, 12, 0, 10);
            qwenChatBody.Children.Add(Col(Row(qwenSessions, qwenNewChat, qwenDeleteChat), qwenDeletePrompt, qwenChatIntro, qwenChatScroll,
                Card(Col(qwenQuery, Row(qwenSubmit, qwenCancel, qwenWebSearch, shortcuts), qwenSearchStatus)),
                historyOptions, qwenConnection));
            AdjustQwenTranscriptHeight(PageScroll.ActualHeight);
            RefreshQwenSessions();
            RenderQwenConversation();
            SetQwenSearchStatus(qwenConversations.PersistenceError);
        }

        void AdjustQwenTranscriptHeight(double height)
        {
            if (qwenChatScroll != null && height > 0)
                qwenChatScroll.MaxHeight = Math.Max(150, Math.Min(600, height - 275));
            if (localQwenScroll != null && height > 0)
                localQwenScroll.MaxHeight = Math.Max(115, Math.Min(450, height - 340));
        }

        static TextBlock QwenSectionCaption(string text)
        {
            var heading = Txt(text, 14, true);
            heading.Margin = new Thickness(0);
            heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return heading;
        }

        void SelectQwenMediaTab() => SwitchQwenTab(QwenAssistantTab.Media);

        bool TryOpenQwenCreativeDraft(string question)
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            string request = Regex.Replace(question, @"^(?:(?:请|帮我|给我|麻烦你|千问[，,：:]?|please\b)\s*)+", "", options);
            if (Regex.IsMatch(request, @"^(?:怎么|怎样|如何|为什么|能否|可以|要不要|how\b|can\b)|[?？]$", options))
                return false;
            string kind;
            if (Regex.IsMatch(request, @"^(?:图生视频|(?:让|把|将)(?:这张|这幅|我的|上传的)?(?:图片|照片|图像|图).{0,20}?(?:动起来|(?:做成|生成|变成|转成)(?:一个|一段)?(?:短视频|视频|动画))|animate\s+(?:this\s+|the\s+)?(?:image|photo)\b)", options))
                kind = "image-video";
            else if (Regex.IsMatch(request, @"^(?:编辑(?:这张)?(?:图片|图像|照片|图)|(?:把|将)(?:这张|这幅|我的|上传的)?(?:图片|照片|图像|图).{0,24}?(?:改|变|换|调|去掉|加)|edit\s+(?:this\s+|the\s+|an?\s+)?(?:image|photo)\b)", options))
                kind = "edit";
            else if (Regex.IsMatch(request, @"^(?:(?:生成|制作|做)(?:一个|一段|个|段)?(?:.{0,20}的)?(?:短视频|视频|动画)(?!教程|脚本)|(?:generate|create|make)\s+(?:an?\s+)?(?:short\s+)?video\b)", options))
                kind = "video";
            else if (Regex.IsMatch(request, @"^(?:画(?:一张|一幅|一个|个|张|幅)|(?:生成|制作)(?:一张|一幅|个|张)?(?:.{1,20}的)?(?:图片|图像|插画|图)(?!表|教程)|(?:generate|create|draw)\s+(?:an?\s+)?(?:image|picture|illustration)\b)", options))
                kind = "image";
            else return false;
            SelectQwenMediaTab();
            if (SetQwenMediaDraft(kind, question)) qwenQuery.Clear();
            return true;
        }

        void SetQwenWebLayout(bool web)
        {
            // The embedded browser scrolls its own document. Give it the finite
            // viewport, while ordinary launcher pages retain the outer scrollbar.
            PageScroll.VerticalScrollBarVisibility = web ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            PageHost.VerticalContentAlignment = web ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        }

        void SwitchQwenTab(QwenAssistantTab tab)
        {
            bool web = tab == QwenAssistantTab.Web, media = tab == QwenAssistantTab.Media;
            bool local = tab == QwenAssistantTab.Local;
            if (!web && !local && qwenChatBody.Children.Count == 0) BuildQwenChatBody();
            if (web && qwenWebBody.Children.Count == 0)
                qwenWebBody.Children.Add(qwenWebPageFactory?.Invoke() ?? BuildQwenWebPage());
            if (media && qwenMediaBody.Children.Count == 0)
                qwenMediaBody.Children.Add(BuildQwenMediaPage());
            if (local && qwenLocalBody.Children.Count == 0)
                qwenLocalBody.Children.Add(BuildLocalQwenPage());
            if (tab != QwenAssistantTab.Chat) CancelQwenSearch();
            if (!media && qwenMediaBody.Visibility == Visibility.Visible) CloseQwenMediaPage();
            qwenSelectedTab = tab;
            SetQwenWebLayout(web);
            qwenWebBody.Visibility = web ? Visibility.Visible : Visibility.Collapsed;
            qwenChatBody.Visibility = tab == QwenAssistantTab.Chat ? Visibility.Visible : Visibility.Collapsed;
            qwenMediaBody.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
            qwenLocalBody.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
            qwenWebTab.SetResourceReference(StyleProperty, web ? (object)"AccentButton" : typeof(Button));
            qwenChatTab.SetResourceReference(StyleProperty, tab == QwenAssistantTab.Chat ? (object)"AccentButton" : typeof(Button));
            qwenMediaTab.SetResourceReference(StyleProperty, media ? (object)"AccentButton" : typeof(Button));
            qwenLocalTab.SetResourceReference(StyleProperty, local ? (object)"AccentButton" : typeof(Button));
            PageScroll.ScrollToTop();
        }

        void OpenQwenConnectionSettings()
        {
            SwitchQwenTab(QwenAssistantTab.Chat);
            qwenConnection.IsExpanded = true;
            qwenConnection.BringIntoView();
        }

        void SwitchQwenSession(Action change)
        {
            CancelQwenSearch();
            change();
            qwenDeletePrompt.Visibility = Visibility.Collapsed;
            qwenQuery.Clear();
            RefreshQwenSessions();
            RenderQwenConversation();
            SetQwenSearchStatus(qwenConversations.PersistenceError);
            qwenQuery.Focus();
        }

        void RefreshQwenSessions()
        {
            qwenRefreshingSessions = true;
            try
            {
                qwenSessions.ItemsSource = null;
                qwenSessions.ItemsSource = qwenConversations.Sessions;
                qwenSessions.SelectedItem = qwenConversations.Current;
            }
            finally { qwenRefreshingSessions = false; }
        }

        void AskToDeleteChat(bool all)
        {
            qwenDeleteAll = all;
            qwenDeleteLabel.Text = all ? "删除全部对话？" : "删除当前对话？";
            qwenDeletePrompt.Visibility = Visibility.Visible;
        }

        void CopyQwenAnswer(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); SetQwenSearchStatus("已复制回答。"); }
            catch (System.Runtime.InteropServices.ExternalException) { SetQwenSearchStatus("剪贴板正被占用，请重试。"); }
        }

        static TextBox QwenSelectableText(string text)
        {
            var box = new TextBox {
                Text = text, FontSize = 14, IsReadOnly = true, IsUndoEnabled = false,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return box;
        }

        static Border ChatBubble(bool user, UIElement body)
        {
            var label = Txt(user ? "你" : "千问", 12, true, true);
            var bubble = new Border {
                Padding = new Thickness(12, 8, 12, 6), Margin = new Thickness(user ? 38 : 0, 2, user ? 0 : 30, 3),
                CornerRadius = new CornerRadius(8), Child = Col(label, body)
            };
            bubble.SetResourceReference(Border.BackgroundProperty, user ? "SelectedBrush" : "PanelBrush");
            return bubble;
        }

        void RenderQwenConversation(string pending = null, bool regenerate = false)
        {
            var turns = qwenConversations.Current.Turns;
            qwenTranscript.Children.Clear();
            qwenAnswer.Text = "";
            qwenSources.Children.Clear();
            qwenAnswerActions.Visibility = pending == null ? Visibility.Visible : Visibility.Collapsed;
            qwenResult.Visibility = Visibility.Collapsed;
            int previous = pending == null || regenerate ? Math.Max(0, turns.Count - 1) : turns.Count;
            for (int i = 0; i < previous; i++)
            {
                var turn = turns[i];
                qwenTranscript.Children.Add(ChatBubble(true, QwenSelectableText(turn.Question)));
                var sources = new StackPanel();
                RenderQwenSources(sources, turn.Sources);
                qwenTranscript.Children.Add(ChatBubble(false, Col(QwenSelectableText(turn.Answer), sources,
                    Row(Btn("复制", () => CopyQwenAnswer(turn.Answer))))));
            }
            if (pending != null)
            {
                qwenTranscript.Children.Add(ChatBubble(true, QwenSelectableText(pending)));
                qwenResult.Visibility = Visibility.Visible;
            }
            else if (turns.Count > 0)
            {
                var last = turns[turns.Count - 1];
                qwenTranscript.Children.Add(ChatBubble(true, QwenSelectableText(last.Question)));
                qwenAnswer.Text = last.Answer;
                RenderQwenSources(qwenSources, last.Sources);
                qwenResult.Visibility = Visibility.Visible;
            }
            qwenRegenerate.IsEnabled = qwenRequest == null && turns.Count != 0;
            bool hasMessages = turns.Count > 0 || pending != null;
            qwenChatScroll.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
            qwenChatIntro.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            qwenChatScroll.ScrollToEnd();
        }

        async Task RunQwenChatAsync(string question, bool regenerate, string key,
            QwenConnectionSettings connection, CancellationTokenSource operation)
        {
            var session = qwenConversations.Current;
            var messages = session.BuildChatTurns(question, regenerate);
            bool web = qwenWebSearch.IsChecked == true;
            RenderQwenConversation(question, regenerate);
            var pending = new StringBuilder();
            object sync = new object();
            Action<string> append = delta => { lock (sync) pending.Append(delta); };
            var clock = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) {
                Interval = TimeSpan.FromMilliseconds(65)
            };
            clock.Tick += (sender, args) =>
            {
                if (!ReferenceEquals(qwenRequest, operation)) return;
                string delta;
                lock (sync) { if (pending.Length == 0) return; delta = pending.ToString(); pending.Clear(); }
                bool follow = qwenChatScroll.ScrollableHeight - qwenChatScroll.VerticalOffset < 30;
                qwenAnswer.AppendText(delta);
                SetQwenSearchStatus("正在回答…");
                if (follow) qwenChatScroll.ScrollToEnd();
            };
            clock.Start();
            try
            {
                QwenSearchResult answer;
                if (qwenRunChat != null)
                    answer = await qwenRunChat(messages, key, connection.Endpoint, connection.Model, web, append, operation.Token);
                else
                    using (var chat = new QwenChatService(settings))
                        answer = await chat.ChatAsync(messages, key, connection.Endpoint, connection.Model, web, append, operation.Token);
                if (!ReferenceEquals(qwenRequest, operation)) return;
                qwenConversations.CommitTurn(session, question, answer, regenerate);
                if (!regenerate && string.Equals(qwenQuery.Text.Trim(), question, StringComparison.Ordinal))
                    qwenQuery.Clear();
                RefreshQwenSessions();
                SetQwenSearchStatus(qwenConversations.PersistenceError);
            }
            finally { clock.Stop(); }
        }
    }
}
