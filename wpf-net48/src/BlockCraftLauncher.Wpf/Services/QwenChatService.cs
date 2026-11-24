using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;
using static BlockCraftLauncher.Wpf.Services.QwenSearchService;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class QwenChatTurn
    {
        public string Role { get; }
        public string Content { get; }
        public QwenChatTurn(string role, string content) { Role = role; Content = content; }
    }

    internal sealed class QwenChatService : IDisposable
    {
        const int MaxContextChars = 32000, MaxHistoryPairs = 16, MaxResponseBytes = 4 * 1024 * 1024;
        const int MaxAnswerChars = 64000, MaxEventBytes = 128 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly HttpClient client;

        public QwenChatService(LauncherConfig settings) : this(NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false)) { }
        internal QwenChatService(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        static IReadOnlyList<object> RequestMessages(IReadOnlyList<QwenChatTurn> turns)
        {
            if (turns == null || turns.Count == 0 || turns.Count % 2 == 0)
                throw new QwenSearchException("对话记录不完整，请新建对话。");
            QwenChatTurn last = turns[turns.Count - 1];
            if (last?.Role != "user" || string.IsNullOrWhiteSpace(last.Content) || last.Content.Length > MaxContextChars)
                throw new QwenSearchException("请输入 1 至 32000 个字符的消息。");

            int start = turns.Count - 1, used = last.Content.Length;
            for (int pairs = 0; start >= 2 && pairs < MaxHistoryPairs; pairs++)
            {
                var question = turns[start - 2];
                var answer = turns[start - 1];
                if (question?.Role != "user" || answer?.Role != "assistant" ||
                    string.IsNullOrWhiteSpace(question.Content) || string.IsNullOrWhiteSpace(answer.Content))
                    throw new QwenSearchException("对话记录不完整，请新建对话。");
                long total = (long)used + question.Content.Length + answer.Content.Length;
                if (total > MaxContextChars) break;
                used = (int)total;
                start -= 2;
            }
            var messages = new List<object>(turns.Count - start);
            for (int i = start; i < turns.Count; i++)
                messages.Add(new { role = turns[i].Role, content = turns[i].Content });
            return messages;
        }

        public async Task<QwenSearchResult> ChatAsync(IReadOnlyList<QwenChatTurn> messages, string apiKey, string endpoint,
            string model, bool webSearch, Action<string> onDelta, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            apiKey = apiKey?.Trim();
            Uri address = ValidateConnection(apiKey, endpoint, model);
            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxResponseBytes, RecursionLimit = 32 };
            var payload = new Dictionary<string, object> {
                { "model", model.Trim() }, { "messages", RequestMessages(messages) }, { "stream", true },
                { "enable_thinking", false }, { "enable_search", webSearch }, { "max_tokens", 4096 }
            };
            if (webSearch) payload["search_options"] = new { forced_search = true };

            using (var request = new HttpRequestMessage(HttpMethod.Post, address))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new StringContent(serializer.Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!response.IsSuccessStatusCode) throw new QwenSearchException(StatusMessage((int)response.StatusCode));
                        if (response.Content == null) throw new QwenSearchException("千问没有返回回答。");
                        string media = response.Content.Headers.ContentType?.MediaType;
                        if (string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                            return await ReadEventsAsync(response, serializer, onDelta, deadline.Token).ConfigureAwait(false);
                        if (!string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
                            throw new QwenSearchException("千问返回了无法识别的回答格式。");
                        using (var buffer = new MemoryStream())
                        {
                            await NetworkDownloader.CopyBodyAsync(response, buffer, 512 * 1024, 0, response.Content.Headers.ContentLength,
                                null, TimeSpan.FromSeconds(45), deadline.Token).ConfigureAwait(false);
                            var answer = ParseResponse(serializer, Utf8.GetString(buffer.ToArray()));
                            if (answer.Answer.Length > MaxAnswerChars) throw new QwenSearchException("回答超过长度限制。");
                            deadline.Token.ThrowIfCancellationRequested();
                            onDelta?.Invoke(answer.Answer);
                            return answer;
                        }
                    }
                }
                catch (Exception) when (deadline.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new QwenSearchException("千问回答超时，请重试。");
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is DecoderFallbackException || ex is InvalidDataException)
                {
                    throw new QwenSearchException("千问连接中断或回答格式异常，请重试。");
                }
            }
        }

        static async Task<QwenSearchResult> ReadEventsAsync(HttpResponseMessage response, JavaScriptSerializer serializer,
            Action<string> onDelta, CancellationToken token)
        {
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new QwenSearchException("回答超过长度限制。");
            var events = new EventReader(serializer, onDelta);
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (idle.Token.Register(response.Dispose))
            using (var line = new MemoryStream())
            {
                try
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(45));
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        var bytes = new byte[8192];
                        int total = 0;
                        bool afterCr = false, firstLine = true;
                        while (true)
                        {
                            int count = await stream.ReadAsync(bytes, 0, bytes.Length, idle.Token).ConfigureAwait(false);
                            idle.Token.ThrowIfCancellationRequested();
                            if (count == 0) break;
                            idle.CancelAfter(TimeSpan.FromSeconds(45));
                            total += count;
                            if (total > MaxResponseBytes) throw new QwenSearchException("回答超过长度限制。");
                            for (int i = 0; i < count; i++)
                            {
                                byte b = bytes[i];
                                if (b == 10 && afterCr) { afterCr = false; continue; }
                                afterCr = b == 13;
                                if (b != 10 && !afterCr)
                                {
                                    if (line.Length >= MaxEventBytes) throw new QwenSearchException("回答片段超过长度限制。");
                                    line.WriteByte(b);
                                    continue;
                                }
                                string value = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length);
                                line.SetLength(0);
                                if (firstLine) { value = value.TrimStart('\uFEFF'); firstLine = false; }
                                if (events.Line(value))
                                {
                                    token.ThrowIfCancellationRequested();
                                    return events.Result();
                                }
                            }
                        }
                    }
                    // A final sentinel may be immediately followed by EOF rather than another blank line.
                    if (line.Length > 0) events.Line(Utf8.GetString(line.GetBuffer(), 0, (int)line.Length));
                    if (events.Line("")) { token.ThrowIfCancellationRequested(); return events.Result(); }
                    throw new QwenSearchException("连接在回答完成前中断，可以重新生成回答。");
                }
                catch (Exception) when (idle.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    throw new QwenSearchException("千问长时间没有返回内容，请重试。");
                }
            }
        }

        sealed class EventReader
        {
            readonly JavaScriptSerializer serializer;
            readonly Action<string> onDelta;
            readonly StringBuilder data = new StringBuilder(), answer = new StringBuilder();
            readonly List<QwenSearchSource> sources = new List<QwenSearchSource>();
            readonly Dictionary<string, string> received = new Dictionary<string, string>();
            string eventId;

            public EventReader(JavaScriptSerializer serializer, Action<string> onDelta)
            {
                this.serializer = serializer;
                this.onDelta = onDelta;
            }

            public bool Line(string line)
            {
                if (line.Length != 0)
                {
                    if (line.StartsWith("id:", StringComparison.Ordinal)) eventId = line.Substring(3).TrimStart(' ');
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        if (data.Length + line.Length > MaxEventBytes) throw new QwenSearchException("回答片段超过长度限制。");
                        if (data.Length > 0) data.Append('\n');
                        data.Append(line.Substring(line.Length > 5 && line[5] == ' ' ? 6 : 5));
                    }
                    return false;
                }
                string id = eventId;
                eventId = null;
                if (data.Length == 0) return false;
                string value = data.ToString();
                data.Clear();
                if (value.Trim() == "[DONE]") return true;
                if (!string.IsNullOrEmpty(id))
                {
                    // The JSON completion id is shared by all deltas. Only an explicit SSE event id can identify a replay.
                    if (received.TryGetValue(id, out string prior) && prior == value) return false;
                    received[id] = value;
                }
                var chunk = ReadJson(serializer, value);
                if (Field(chunk, "error") != null || !(Field(chunk, "choices") is object[]))
                    throw new QwenSearchException("千问未能完成这条回答。");
                var choices = Items(chunk, "choices");
                object choice = choices.Length == 0 ? null : choices[0];
                object delta = Field(choice, "delta");
                if (Field(choice, "finish_reason") as string == "content_filter")
                    throw new QwenSearchException("千问未能完成这条回答。");
                AppendSources(chunk, delta, sources);
                string text = Field(delta, "content") as string;
                if (!string.IsNullOrEmpty(text))
                {
                    if (answer.Length + text.Length > MaxAnswerChars) throw new QwenSearchException("回答超过长度限制。");
                    answer.Append(text);
                    onDelta?.Invoke(text);
                }
                return false;
            }

            public QwenSearchResult Result()
            {
                string text = answer.ToString();
                if (string.IsNullOrWhiteSpace(text)) throw new QwenSearchException("千问没有返回回答。");
                return new QwenSearchResult(text, sources.AsReadOnly());
            }
        }

        public void Dispose() => client.Dispose();
    }
}
