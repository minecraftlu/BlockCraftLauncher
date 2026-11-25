using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class LocalQwenChatChecks
    {
        sealed class LocalFixture : HttpMessageHandler
        {
            public string Response;
            public object Body;
            public HttpStatusCode Status = HttpStatusCode.OK;
            public bool WaitForCancel;
            public readonly TaskCompletionSource<bool> Reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Require(request.RequestUri.AbsoluteUri == "http://127.0.0.1:33791/api/chat" &&
                    request.Method == HttpMethod.Post && request.Headers.Authorization == null, "Local endpoint or headers");
                Body = new JavaScriptSerializer().DeserializeObject(await request.Content.ReadAsStringAsync());
                Reached.TrySetResult(true);
                if (WaitForCancel) await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/x-ndjson") };
            }
        }

        static string Event(string content, bool done)
        {
            return new JavaScriptSerializer().Serialize(new { message = new { role = "assistant", content, thinking = "hidden" }, done });
        }
        static void Require(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("Local Qwen check failed: " + what);
        }

        public static async Task RunAsync()
        {
            var local = new Uri("http://127.0.0.1:33791/");
            var question = new QwenChatTurn("user", "帮我找一个提升帧率的模组");
            var history = new List<QwenChatTurn>();
            for (int i = 0; i < 20; i++)
            {
                history.Add(new QwenChatTurn("user", "Q" + i));
                history.Add(new QwenChatTurn("assistant", "A" + i));
            }
            history.Add(question);
            foreach (string invalid in new[] { "https://example.com/", "http://127.0.0.1:33791/api/", "http://user@127.0.0.1/" })
            {
                try { using (new LocalQwenChatService(new Uri(invalid))) { } }
                catch (QwenSearchException) { continue; }
                throw new InvalidOperationException("Non-runtime endpoint accepted.");
            }
            var fixture = new LocalFixture { Response = Event("玩家", false) + "\r\n" + Event("你好", true) };
            using (var chat = new LocalQwenChatService(local, fixture))
            {
                var deltas = new List<string>();
                string answer = await chat.ChatAsync(history, deltas.Add, CancellationToken.None);
                Require(answer == "玩家你好" && deltas.SequenceEqual(new[] { "玩家", "你好" }), "stream content");
                var turns = QwenSearchService.Items(fixture.Body, "messages");
                Require(turns.Length == 34 && Equals(QwenSearchService.Field(turns[1], "content"), "Q4") &&
                    Equals(QwenSearchService.Field(turns[turns.Length - 1], "content"), question.Content) &&
                    Equals(QwenSearchService.Field(fixture.Body, "model"), "qwen3.5:4b") &&
                    Equals(QwenSearchService.Field(fixture.Body, "think"), false) &&
                    QwenSearchService.Field(fixture.Body, "tools") == null && QwenSearchService.Field(fixture.Body, "format") == null,
                    "bounded full-turn history and chat-only payload");
                fixture.Response = Event("{\"keyword\":\"sodium\"}", true);
                Require(await chat.SuggestModAsync(question.Content, CancellationToken.None) == "sodium" &&
                    QwenSearchService.Field(fixture.Body, "format") != null, "constrained catalog keyword");
                foreach (string bad in new[] { "{\"keyword\":\"https://example.com\"}", "{\"keyword\":\"mod\",\"install\":true}",
                    "{\"keyword\":\"../../tmp\"}", "{\"keyword\":null}" })
                {
                    fixture.Response = Event(bad, true);
                    try { await chat.SuggestModAsync(question.Content, CancellationToken.None); }
                    catch (QwenSearchException) { continue; }
                    throw new InvalidOperationException("Unsafe or empty catalog keyword accepted.");
                }
                fixture.Response = Event("未完成", false);
                try { await chat.ChatAsync(history, null, CancellationToken.None); throw new InvalidOperationException("Incomplete answer accepted."); }
                catch (QwenSearchException) { }
                fixture.Status = HttpStatusCode.NotFound;
                try { await chat.ChatAsync(history, null, CancellationToken.None); throw new InvalidOperationException("Missing model accepted."); }
                catch (QwenSearchException error) { Require(error.Message.Contains("Qwen3.5-4B"), "missing model status"); }
            }
            var paused = new LocalFixture { WaitForCancel = true };
            using (var cancel = new CancellationTokenSource())
            using (var chat = new LocalQwenChatService(local, paused))
            {
                var pending = chat.ChatAsync(history, null, cancel.Token);
                await paused.Reached.Task;
                cancel.Cancel();
                try { await pending; throw new InvalidOperationException("Cancellation ignored."); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
