using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class LocalQwenChatService : IDisposable
    {
        const int MaxResponseBytes = 4 * 1024 * 1024, MaxLineBytes = 96 * 1024, MaxAnswerChars = 64000;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        readonly Uri address;
        readonly HttpClient client;

        public LocalQwenChatService(Uri endpoint) : this(endpoint, new HttpClientHandler {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, UseDefaultCredentials = false
        }) { }

        internal LocalQwenChatService(Uri endpoint, HttpMessageHandler handler)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "http" ||
                endpoint.Host != "127.0.0.1" || endpoint.Port <= 0 || endpoint.PathAndQuery != "/" ||
                endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
                throw new QwenSearchException("本地千问需要连接到本机 Ollama 服务。");
            address = new Uri(endpoint, "api/chat");
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public Task<string> ChatAsync(IReadOnlyList<QwenChatTurn> history, Action<string> append, CancellationToken token)
        {
            var messages = new List<object> { new { role = "system", content =
                "你是 BlockCraft 启动器中的本地千问助手，用用户的语言回答。你可以解释 Minecraft 和其他问题。"
                + "查找和安装由启动器单独执行。没有收到实际执行结果时，不要声称已下载、安装或搜索。" } };
            messages.AddRange(QwenChatService.RequestMessages(history));
            return SendAsync(messages, null, append, token);
        }

        public async Task<string> SuggestModAsync(string question, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(question) || question.Length > 2000)
                throw new QwenSearchException("请用一句话描述需要的模组。");
            var messages = new object[] {
                new { role = "system", content = "根据玩家描述的用途和加载器，推荐一个真实的 Minecraft Java 模组。"
                    + "输出 JSON，其中 keyword 是所选目录中一个项目的英文 slug。未指定目录时优先推荐 Modrinth 上的项目。"
                    + "不确定时 keyword 填 null。" },
                new { role = "user", content = question }
            };
            var schema = new {
                type = "object", properties = new { keyword = new { type = new[] { "string", "null" } } },
                required = new[] { "keyword" }, additionalProperties = false
            };
            string answer = await SendAsync(messages, schema, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (QwenModRecommendationService.TryReadKeyword(answer, out string slug)) return slug;
            throw new QwenSearchException("本地千问未能确定模组名。请换个描述，或输入「搜索模组」和项目名。");
        }

        async Task<string> SendAsync(IReadOnlyList<object> messages, object schema, Action<string> append, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var json = new JavaScriptSerializer { MaxJsonLength = 2 * MaxLineBytes, RecursionLimit = 32 };
            var payload = new Dictionary<string, object> {
                { "model", LocalModelRuntime.Model }, { "messages", messages }, { "stream", true }, { "think", false }
            };
            if (schema != null)
            {
                payload["format"] = schema;
                payload["options"] = new { temperature = 0, num_predict = 96 };
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, address))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // Loading a cold 4B model on a CPU can take several minutes.
                deadline.CancelAfter(TimeSpan.FromMinutes(15));
                request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode == 404)
                            throw new QwenSearchException("Ollama 没有找到 Qwen3.5-4B。请在本地模型页检查或下载模型。");
                        if (!response.IsSuccessStatusCode)
                            throw new QwenSearchException("本地 Ollama 返回 HTTP " + (int)response.StatusCode + "，请检查模型状态和可用内存。");
                        if (response.Content == null || response.Content.Headers.ContentLength > MaxResponseBytes)
                            throw new QwenSearchException("Ollama 返回的数据为空或过大。");
                        using (deadline.Token.Register(response.Dispose))
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            return await ReadAnswerAsync(stream, json, append, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (deadline.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                    throw new QwenSearchException("本地千问回答超时，请检查模型状态后重试。");
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is DecoderFallbackException)
                {
                    throw new QwenSearchException("与本机 Ollama 的连接中断，请检查服务后重试。");
                }
            }
        }

        static async Task<string> ReadAnswerAsync(Stream stream, JavaScriptSerializer json, Action<string> append, CancellationToken token)
        {
            var answer = new StringBuilder();
            var bytes = new byte[8192];
            int total = 0;
            using (var line = new MemoryStream())
            {
                while (true)
                {
                    int count = await stream.ReadAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    total += count;
                    if (total > MaxResponseBytes) throw new QwenSearchException("本地千问回答过长。");
                    for (int i = 0; i < count; i++)
                    {
                        if (bytes[i] != '\n')
                        {
                            if (line.Length >= MaxLineBytes) throw new QwenSearchException("本地千问回答片段过长。");
                            line.WriteByte(bytes[i]);
                            continue;
                        }
                        if (TakeEvent(line, json, answer, append)) return answer.ToString();
                        line.SetLength(0);
                    }
                    if (count == 0) break;
                }
                if (TakeEvent(line, json, answer, append)) return answer.ToString();
            }
            throw new QwenSearchException("本地千问在回答完成前中断，请重试。");
        }

        static bool TakeEvent(MemoryStream line, JavaScriptSerializer json, StringBuilder answer, Action<string> append)
        {
            string value = Utf8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
            if (value.Length == 0) return false;
            var evt = QwenSearchService.ReadJson(json, value);
            if (QwenSearchService.Field(evt, "error") != null)
                throw new QwenSearchException("本地模型运行失败，请检查可用内存或重新加载模型。");
            var msg = QwenSearchService.Field(evt, "message");
            if (!(QwenSearchService.Field(evt, "done") is bool done) ||
                !(QwenSearchService.Field(msg, "content") is string text) ||
                !Equals(QwenSearchService.Field(msg, "role"), "assistant"))
                throw new QwenSearchException("Ollama 返回了无法识别的回答格式。");
            if (answer.Length + text.Length > MaxAnswerChars) throw new QwenSearchException("本地千问回答过长。");
            if (text.Length != 0) { answer.Append(text); append?.Invoke(text); }
            if (done && string.IsNullOrWhiteSpace(answer.ToString())) throw new QwenSearchException("本地千问没有返回回答。");
            return done;
        }

        public void Dispose() => client.Dispose();
    }
}
