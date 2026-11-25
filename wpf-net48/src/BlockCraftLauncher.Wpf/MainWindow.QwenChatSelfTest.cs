using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        // Deliver the first SSE event, then wait until the test has observed it in the real window.
        sealed class QwenChatFixtureStream : Stream
        {
            readonly byte[] prefix, suffix;
            readonly TaskCompletionSource<bool> gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int position;
            public QwenChatFixtureStream(string prefix, string suffix, bool paused)
            {
                this.prefix = Encoding.UTF8.GetBytes(prefix);
                this.suffix = Encoding.UTF8.GetBytes(suffix);
                if (!paused) Resume();
            }
            public void Resume() => gate.TrySetResult(true);
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                // Exercise UTF-8 code points and SSE records split across reads.
                if (position >= prefix.Length)
                    using (token.Register(() => gate.TrySetCanceled()))
                        await gate.Task.ConfigureAwait(false);
                bool initial = position < prefix.Length;
                byte[] data = initial ? prefix : suffix;
                int from = initial ? position : position - prefix.Length;
                int n = Math.Min(7, Math.Min(count, data.Length - from));
                Array.Copy(data, from, buffer, offset, n);
                position += n;
                return n;
            }
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Flush() { }
            public override void Write(byte[] bytes, int start, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) gate.TrySetCanceled(); base.Dispose(disposing); }
        }

        sealed class QwenChatFixtureServer : HttpMessageHandler
        {
            public readonly List<Dictionary<string, object>> Requests = new List<Dictionary<string, object>>();
            public QwenChatFixtureStream Stream;
            public void Reply(string beginning, string end = "", bool paused = false, bool truncated = false)
            {
                var json = new JavaScriptSerializer();
                string Event(object delta) => "data: " + json.Serialize(new { choices = new[] { new { index = 0, delta } } }) + "\r\n\r\n";
                var links = new[] {
                    new { type = "url_citation", url_citation = new { title = "Minecraft 官网", url = QwenFixtureSource } },
                    new { type = "url_citation", url_citation = new { title = "本地文件", url = "file:///restricted-file" } }
                };
                Stream = new QwenChatFixtureStream(Event(new { content = beginning }),
                    Event(new { content = end, annotations = links }) + (truncated ? "" : "data: [DONE]\r\n\r\n"), paused);
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                string raw = await req.Content.ReadAsStringAsync().ConfigureAwait(false);
                CheckQwen(req.Method == HttpMethod.Post && req.RequestUri.AbsoluteUri == QwenFixtureEndpoint &&
                    req.Headers.Authorization?.Parameter == QwenFixtureKey && !raw.Contains(QwenFixtureKey),
                    "聊天请求的地域、鉴权或密钥边界异常。");
                var body = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(raw);
                CheckQwen(Equals(body["stream"], true) && Equals(body["model"], QwenSearchService.DefaultModel),
                    "聊天请求未启用流式协议或选择了其他模型。");
                Requests.Add(body);
                var content = new StreamContent(Stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            public void ExpectRequest(bool web, params string[] turns)
            {
                var body = Requests.Last();
                CheckQwen((body.TryGetValue("enable_search", out var search) && Equals(search, true)) == web,
                    "联网搜索开关没有传递到请求。");
                var messages = ((object[])body["messages"]).Cast<Dictionary<string, object>>()
                    .Where(m => !Equals(m["role"], "system")).ToArray();
                CheckQwen(messages.Length == turns.Length, "传给模型的对话长度不符。");
                for (int i = 0; i < turns.Length; i++)
                    CheckQwen(Equals(messages[i]["content"], turns[i]) && Equals(messages[i]["role"], i % 2 == 0 ? "user" : "assistant"),
                        "模型收到的历史消息与当前会话不符。");
            }
        }

        internal async Task<string> VerifyQwenChatAsync()
        {
            string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
                "blockcraft-qwen-chat-" + Guid.NewGuid().ToString("N"))).FullName;
            var fake = new QwenChatFixtureServer();
            using var client = new QwenChatService(fake);
            var window = new MainWindow(null, null, new SettingsStore(Path.Combine(folder, "settings.json"))) {
                qwenSettingsPath = Path.Combine(folder, "qwen.json"), qwenHistoryPath = Path.Combine(folder, "history.json"),
                SuppressAutomaticUpdateCheck = true, ShowInTaskbar = false, ShowActivated = false, qwenRunChat = client.ChatAsync,
                qwenWebPageFactory = OfflineQwenWebPage
            };
            var opened = new List<Uri>();
            window.qwenOpenUri = opened.Add;
            async Task Ui() => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            void SetHistory(bool enabled)
            {
                window.qwenRetainHistory.IsChecked = enabled;
                window.qwenRetainHistory.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            }
            void Send(string prompt)
            {
                window.qwenQuery.Text = prompt;
                ClickFixtureAction(window.qwenSubmit);
            }
            async Task Finished()
            {
                await WaitForQwenAsync(() => window.qwenRequest == null && !window.qwenCancel.IsEnabled, "完成聊天");
                await Ui();
            }
            const string first = "我把存档叫作北极星。", answer = "记住了，名字是北极星。";
            const string follow = "刚才的名字是什么？", reply = "你叫它北极星。", revised = "这个存档叫北极星。";
            try
            {
                QwenConversationSanityChecks.Run(folder);
                await QwenChatTransportChecks.RunAsync();
                window.Show();
                window.Navigate(QwenSearchPage);
                await Ui();
                window.SelectQwenFixtureChat();
                window.qwenProvider.SelectedIndex = 1;
                window.qwenEndpoint.Text = QwenFixtureEndpoint;
                window.qwenModel.Text = QwenSearchService.DefaultModel;
                window.qwenApiKey.Password = QwenFixtureKey;
                CheckQwen(!window.qwenConversations.HistoryEnabled && !File.Exists(window.qwenHistoryPath), "聊天默认写入了历史。");
                string firstId = window.qwenConversations.Current.Id;
                window.qwenWebSearch.IsChecked = false;
                fake.Reply("记住了，", "名字是北极星。", paused: true);
                Send(first);
                await WaitForQwenAsync(() => window.qwenAnswer.Text == "记住了，", "第一个流式片段");
                CheckQwen(window.qwenRequest != null && !window.qwenSubmit.IsEnabled && window.qwenConversations.Current.Turns.Count == 0,
                    "流式回答尚未结束就提前提交了消息。");
                fake.Stream.Resume();
                await Finished();
                fake.ExpectRequest(false, first);
                CheckQwen(window.qwenAnswer.Text == answer && !File.Exists(window.qwenHistoryPath), "会话中回答显示或临时历史异常。");

                window.qwenWebSearch.IsChecked = true;
                fake.Reply(reply);
                Send(follow);
                await Finished();
                fake.ExpectRequest(true, first, answer, follow);
                var links = FixtureVisuals(window.qwenSources).OfType<Button>().ToArray();
                CheckQwen(links.Length == 1 && links[0].ToolTip as string == QwenFixtureSource, "流式来源链接缺失或包含不安全链接。");
                ClickFixtureAction(links[0]);
                CheckQwen(opened.Count == 1 && opened[0].AbsoluteUri == QwenFixtureSource, "打开了错误的引用来源。");
                window.PageScroll.ScrollToTop();
                window.ArrangeLayoutProbe(960, 600, "qwen-chat-result");

                fake.Reply(revised);
                ClickFixtureAction(window.qwenRegenerate);
                await Finished();
                fake.ExpectRequest(true, first, answer, follow);
                CheckQwen(window.qwenConversations.Current.Turns.Count == 2 && window.qwenAnswer.Text == revised,
                    "重新生成追加了重复的问题或未替换旧回答。");
                var historyOptions = FixtureVisuals(window.qwenChatBody).OfType<Expander>()
                    .Single(x => (x.Header as TextBlock)?.Text == "对话设置");
                historyOptions.IsExpanded = true;
                SetHistory(true);
                await Ui();
                var persisted = new QwenConversationStore(window.qwenHistoryPath);
                CheckQwen(persisted.HistoryEnabled && persisted.Sessions.Any(s => s.Id == firstId && s.Turns.Count == 2)
                    && !File.ReadAllText(window.qwenHistoryPath).Contains(QwenFixtureKey), "本地历史未回读或保存了凭证。");

                // A finished-looking prefix without the provider's DONE marker cannot be committed.
                fake.Reply("不完整的片段", truncated: true);
                Send("试试看这一条。");
                await Finished();
                CheckQwen(window.qwenConversations.Current.Turns.Count == 2 && window.qwenAnswer.Text == revised &&
                    !string.IsNullOrWhiteSpace(window.qwenSearchStatus.Text), "截断响应污染了会话。");

                fake.Reply("临时片段", "已取消", paused: true);
                Send("讲一个简短的故事。");
                await WaitForQwenAsync(() => window.qwenAnswer.Text == "临时片段", "等待停止");
                ClickFixtureAction(window.qwenCancel);
                fake.Stream.Resume();
                await Finished();
                CheckQwen(window.qwenConversations.Current.Turns.Count == 2 && window.qwenAnswer.Text == revised,
                    "停止后仍然显示或保存了部分回答。");

                ClickFixtureAction(window.qwenNewChat);
                await Ui();
                CheckQwen(window.qwenConversations.Current.Id != firstId && window.qwenConversations.Current.Turns.Count == 0,
                    "新会话包含前一个会话的记录。");
                string secondId = window.qwenConversations.Current.Id;
                fake.Reply("平坦世界适合测试建筑。");
                Send("什么是超平坦世界？");
                await Finished();
                fake.ExpectRequest(window.qwenWebSearch.IsChecked == true, "什么是超平坦世界？");
                fake.Reply("未完成的追问", "结束", paused: true);
                Send("再举一个例子。");
                await WaitForQwenAsync(() => window.qwenAnswer.Text == "未完成的追问", "切换会话前的片段");
                window.qwenSessions.SelectedItem = window.qwenConversations.Sessions.Single(s => s.Id == firstId);
                fake.Stream.Resume();
                await Finished();
                CheckQwen(window.qwenConversations.Current.Id == firstId && window.qwenAnswer.Text == revised &&
                    window.qwenConversations.Sessions.Single(s => s.Id == secondId).Turns.Count == 1,
                    "切换会话没有恢复上下文或已停止的回答污染了记录。");
                ClickFixtureAction(window.qwenDeleteChat);
                ClickFixtureAction(FixtureButton(window.qwenChatBody, "保留对话"));
                CheckQwen(window.qwenConversations.Current.Id == firstId && window.qwenConversations.Current.Turns.Count == 2,
                    "取消删除后丢失了当前对话。");
                ClickFixtureAction(window.qwenDeleteChat);
                ClickFixtureAction(window.qwenConfirmDelete);
                await Ui();
                var remaining = new QwenConversationStore(window.qwenHistoryPath);
                CheckQwen(!remaining.Sessions.Any(s => s.Id == firstId) && remaining.Sessions.Any(s => s.Id == secondId),
                    "删除会话影响了其他记录或未写入。");
                ClickFixtureAction(window.qwenClearChats);
                ClickFixtureAction(window.qwenConfirmDelete);
                await Ui();
                remaining = new QwenConversationStore(window.qwenHistoryPath);
                CheckQwen(remaining.HistoryEnabled && remaining.Sessions.All(session => session.Turns.Count == 0) &&
                    window.qwenConversations.Sessions.All(session => session.Turns.Count == 0), "清空后仍能读取旧对话。");
                SetHistory(false);
                await Ui();
                CheckQwen(!File.Exists(window.qwenHistoryPath) && !new QwenConversationStore(window.qwenHistoryPath).HistoryEnabled,
                    "关闭历史记录后，先前的记录仍可从磁盘读取。");
                CheckQwen(fake.Requests.Count == 7 && !window.downloads.Jobs.Any(), "聊天操作产生了额外的模型调用或下载。");
                return "PASS: native WPF chat uses streamed HTTP, sends the active conversation, honors web search, " +
                    "handles citations, regeneration, stop and truncated streams, and isolates sessions and opt-in local history.";
            }
            finally
            {
                fake.Stream?.Resume();
                window.Close();
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
    }
}
