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

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class QwenChatTransportChecks
    {
        sealed class SplitStream : MemoryStream
        {
            public SplitStream(string response) : base(Encoding.UTF8.GetBytes(response)) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                base.ReadAsync(buffer, offset, Math.Min(2, count), ct);
        }

        sealed class Reply : HttpMessageHandler
        {
            internal string Text, MediaType = "text/event-stream";
            internal HttpStatusCode Status = HttpStatusCode.OK;
            internal object Request;
            public int Calls;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Calls++;
                string body = await request.Content.ReadAsStringAsync();
                Check(!body.Contains("fixture-test-key") && request.Headers.Authorization?.Parameter == "fixture-test-key",
                    "Credential belongs only in the HTTP authorization header.");
                Request = new JavaScriptSerializer().DeserializeObject(body);
                var content = new StreamContent(new SplitStream(Text));
                content.Headers.ContentType = new MediaTypeHeaderValue(MediaType);
                return new HttpResponseMessage(Status) { Content = content };
            }
        }

        static void Check(bool test, string message)
        {
            if (!test) throw new InvalidDataException("Qwen stream check: " + message);
        }

        public static async Task RunAsync()
        {
            var messages = new List<QwenChatTurn>();
            for (int i = 0; i < 20; i++)
            {
                messages.Add(new QwenChatTurn("user", "q" + i));
                messages.Add(new QwenChatTurn("assistant", "a" + i));
            }
            messages.Add(new QwenChatTurn("user", "继续"));

            const string chunk = "{\"id\":\"same-completion\",\"choices\":[{\"delta\":{\"content\":\"你😀\"}}]}";
            const string event1 = "id: event1\r\ndata: " + chunk + "\r\n\r\n";
            const string jsonAnswer = "{\"choices\":[{\"message\":{\"content\":\"答案\"}}]}";
            var http = new Reply { Text = "\uFEFF: keep-alive\r\n\r\n" + event1 + event1 + "data: " + chunk +
                "\r\n\r\ndata: {\"choices\":[\ndata: {\"delta\":{\"reasoning_content\":\"private\"}}]}\n\n" +
                "data: {\"choices\":[],\"usage\":{}}\n\ndata: [DONE]\n\n" };
            using (var service = new QwenChatService(http))
            {
                var delivered = new StringBuilder();
                async Task<QwenSearchResult> Send(bool search, CancellationToken ct)
                {
                    return await service.ChatAsync(messages, "fixture-test-key", QwenSearchService.DefaultEndpoint,
                        QwenSearchService.DefaultModel, search, part => delivered.Append(part), ct);
                }
                var result = await Send(false, CancellationToken.None);
                var actual = QwenSearchService.Items(http.Request, "messages");
                Check(actual.Length == 33 && (string)QwenSearchService.Field(actual[0], "content") == "q4" &&
                    (string)QwenSearchService.Field(actual[32], "content") == "继续", "Bounded history must retain whole pairs and the last question.");
                Check(Equals(QwenSearchService.Field(http.Request, "stream"), true) &&
                    Equals(QwenSearchService.Field(http.Request, "enable_search"), false) &&
                    QwenSearchService.Field(http.Request, "search_options") == null, "Ordinary chat must not request web search.");
                Check(result.Answer == "你😀你😀" && delivered.ToString() == result.Answer,
                    "Split UTF-8, repeated tokens, explicit SSE replays or reasoning fragments were mishandled.");

                http.MediaType = "application/json";
                http.Text = jsonAnswer;
                delivered.Clear();
                result = await Send(true, CancellationToken.None);
                Check(result.Answer == "答案" && delivered.ToString() == result.Answer &&
                    Equals(QwenSearchService.Field(QwenSearchService.Field(http.Request, "search_options"), "forced_search"), true),
                    "JSON response or explicit web-search request failed.");

                http.MediaType = "text/event-stream";
                http.Text = event1;
                bool truncated = false;
                try { await Send(false, CancellationToken.None); }
                catch (QwenSearchException) { truncated = true; }
                Check(truncated, "Truncated HTTP data must not be committed as a complete answer.");

                http.Text = "data: {\"error\":{\"message\":\"private-provider-text\"}}\n\n";
                bool sanitized = false;
                try { await Send(false, CancellationToken.None); }
                catch (QwenSearchException error) { sanitized = !error.Message.Contains("private-provider-text"); }
                Check(sanitized, "Provider diagnostics must not be copied to the UI.");

                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    int calls = http.Calls;
                    try { await Send(false, canceled.Token); throw new InvalidDataException("Canceled search ran."); }
                    catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
                    Check(http.Calls == calls, "Canceled requests must not be sent or retried.");
                }
            }
        }
    }
}
