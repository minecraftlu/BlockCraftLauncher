using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        const string QwenFixtureEndpoint = "https://fixture.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions";
        const string QwenFixtureKey = "offline-qwen-test-key";
        const string QwenFixtureQuestion = "如何备份 Minecraft 世界？";
        const string QwenFixtureAnswer = "退出游戏后打开实例的 saves 文件夹，将对应世界文件夹复制到备份目录。[1]";
        const string QwenFixtureSource = "https://www.minecraft.net/en-us";

        static void CheckQwen(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("千问自检：" + message);
        }

        // Every HTTP request stays inside this handler, including requests to the synthetic operator.
        sealed class QwenFixtureHandler : HttpMessageHandler
        {
            public int Requests;
            public HttpStatusCode ResponseCode = HttpStatusCode.OK;
            public string ValidationFailure;
            public bool OmitSources, WaitForCancellation;
            public readonly TaskCompletionSource<bool> Canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Interlocked.Increment(ref Requests);
                var serializer = new JavaScriptSerializer();
                var content = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                try
                {
                    var body = (Dictionary<string, object>)serializer.DeserializeObject(content);
                    var messages = ((object[])body["messages"]).Cast<Dictionary<string, object>>();
                    bool hasQuestion = messages.Any(message => Equals(message["role"], "user") &&
                        Equals(message["content"], QwenFixtureQuestion));
                    CheckQwen(request.Method == HttpMethod.Post && request.RequestUri.AbsoluteUri == QwenFixtureEndpoint &&
                        request.Headers.Authorization?.Scheme == "Bearer" &&
                        request.Headers.Authorization?.Parameter == QwenFixtureKey &&
                        Equals(body["model"], QwenSearchService.DefaultModel) && Equals(body["enable_search"], true) &&
                        Equals(((Dictionary<string, object>)body["search_options"])["forced_search"], true) &&
                        hasQuestion && !content.Contains(QwenFixtureKey), "请求地址、模型、问题、鉴权或联网搜索参数错误。");
                }
                catch (Exception ex) { ValidationFailure = ex.GetType().Name; }

                if (WaitForCancellation)
                {
                    using (token.Register(() => Canceled.TrySetResult(true)))
                        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                string json = ResponseCode == HttpStatusCode.OK ? serializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = QwenFixtureAnswer, annotations = OmitSources ? null : new[]
                    {
                        new { type = "url_citation", url_citation = new { title = "Minecraft 官网", url = QwenFixtureSource } },
                        new { type = "url_citation", url_citation = new { title = "Invalid", url = "file:///local-file" } }
                    } } } }
                }) : serializer.Serialize(new { error = new { message = "private provider details " + QwenFixtureKey } });
                return new HttpResponseMessage(ResponseCode)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
        }

        static async Task WaitForQwenAsync(Func<bool> condition, string stage)
        {
            var timer = Stopwatch.StartNew();
            while (!condition())
            {
                if (timer.Elapsed.TotalSeconds > 8) throw new TimeoutException("千问自检超时：" + stage);
                await Task.Delay(15);
            }
        }

        internal async Task<string> VerifyQwenSearchAsync()
        {
            string folder = Path.Combine(Path.GetTempPath(), "blockcraft-qwen-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            qwenSettingsPath = Path.Combine(folder, "launcher.json");
            qwenCompanionPath = Path.Combine(folder, "companion.json");
            const string environmentKey = "DASHSCOPE_API_KEY";
            string previousKey = Environment.GetEnvironmentVariable(environmentKey);
            var fixture = new QwenFixtureHandler();
            using var client = new QwenSearchService(fixture);
            qwenRunSearch = client.SearchAsync;
            var opened = new List<Uri>();
            qwenOpenUri = opened.Add;
            async Task Layout()
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ArrangeLayoutProbe(960, 600);
            }
            async Task SearchAndWait()
            {
                ClickFixtureAction(qwenSubmit);
                await WaitForQwenAsync(() => qwenSubmit.IsEnabled && !qwenCancel.IsEnabled, "搜索完成");
                await Layout();
            }

            try
            {
                Environment.SetEnvironmentVariable(environmentKey, null);
                ShowActivated = false;
                ShowInTaskbar = false;
                SuppressAutomaticUpdateCheck = true;
                if (!IsVisible) Show();
                Navigate("首页");
                await Layout();
                ClickFixtureAction(FixtureButton(PageHost, QwenSearchPage));
                await Layout();
                VerifyFixtureSidebar(QwenSearchPage);
                CheckQwen(qwenModel.Text == QwenSearchService.DefaultModel &&
                    qwenEndpoint.Text == QwenSearchService.DefaultEndpoint, "首次打开的模型或服务地址错误。");
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(qwenCompanionPath, serializer.Serialize(new
                {
                    ApiEndpoint = QwenFixtureEndpoint, Model = QwenSearchService.DefaultModel, UseDemoProvider = false,
                    ApiKey = "do-not-import", RememberApiKey = true, ProtectedApiKey = "do-not-import-protected"
                }), Encoding.UTF8);
                var imported = QwenConnectionSettings.ImportCompanion(qwenCompanionPath);
                CheckQwen(imported.Endpoint == QwenFixtureEndpoint && imported.Model == QwenSearchService.DefaultModel &&
                    string.IsNullOrEmpty(imported.ApiKey) && !imported.RememberApiKey, "桌面伙伴导入了凭据或丢失了模型配置。");
                qwenConnection.IsExpanded = true;
                await Layout();
                ClickFixtureAction(FixtureButton(PageHost, "使用桌面伙伴配置"));
                await Layout();
                CheckQwen(qwenEndpoint.Text == QwenFixtureEndpoint && qwenModel.Text == imported.Model &&
                    string.IsNullOrEmpty(qwenApiKey.Password) && fixture.Requests == 0, "导入操作未正确更新配置或触发了查询。");
                qwenQuery.Text = QwenFixtureQuestion;
                if (qwenSubmit.IsEnabled) ClickFixtureAction(qwenSubmit);
                await Layout();
                CheckQwen(fixture.Requests == 0 && !qwenCancel.IsEnabled, "没有凭据时发送了请求。");
                qwenApiKey.Password = QwenFixtureKey;
                await Layout();
                qwenRememberKey.IsChecked = true;
                ClickFixtureAction(FixtureButton(PageHost, "保存连接"));
                CheckQwen(File.Exists(qwenSettingsPath), "未写出连接设置：" + qwenSearchStatus.Text);
                CheckQwen(!File.ReadAllText(qwenSettingsPath).Contains(QwenFixtureKey), "配置文件含有明文密钥。");
                CheckQwen(QwenConnectionSettings.Load(qwenSettingsPath).ApiKey == QwenFixtureKey,
                    "本机保存的密钥无法回读。");
                qwenRememberKey.IsChecked = false;
                ClickFixtureAction(FixtureButton(PageHost, "保存连接"));
                CheckQwen(string.IsNullOrEmpty(QwenConnectionSettings.Load(qwenSettingsPath).ApiKey),
                    "取消记住后仍然存有密钥。");
                qwenConnection.IsExpanded = false;
                await Layout();
                await SearchAndWait();
                CheckQwen(fixture.Requests == 1 && fixture.ValidationFailure == null &&
                    qwenAnswer.Text == QwenFixtureAnswer, "正常查询的请求或回答错误。");
                var sources = FixtureVisuals(qwenSources).OfType<Button>().ToList();
                CheckQwen(sources.Count == 1 && sources[0].ToolTip as string == QwenFixtureSource,
                    "来源链接缺失或包含不受支持的链接。");
                ClickFixtureAction(sources[0]);
                CheckQwen(opened.Count == 1 && opened[0].AbsoluteUri == QwenFixtureSource, "点击来源打开了错误地址。");
                PageScroll.ScrollToBottom();
                ArrangeLayoutProbe(960, 600, "qwen-search-result");

                fixture.OmitSources = true;
                await SearchAndWait();
                CheckQwen(qwenAnswer.Text == QwenFixtureAnswer && !FixtureVisuals(qwenSources).OfType<Button>().Any() &&
                    FixtureVisuals(qwenSources).OfType<TextBlock>().Any(label => label.Text.Contains("未附来源")),
                    "缺少来源元数据时未保留回答和对应说明。");
                foreach (var code in new[] { HttpStatusCode.Unauthorized, (HttpStatusCode)429 })
                {
                    fixture.ResponseCode = code;
                    await SearchAndWait();
                    string message = code == HttpStatusCode.Unauthorized ? "认证" : "限额";
                    CheckQwen(qwenSearchStatus.Text.Contains(message) && !qwenSearchStatus.Text.Contains(QwenFixtureKey) &&
                        !qwenSearchStatus.Text.Contains("private provider details") && !qwenAnswer.Text.Contains(QwenFixtureKey),
                        "服务错误内容未安全处理。");
                }
                CheckQwen(fixture.Requests == 4 && fixture.ValidationFailure == null, "错误响应未走真实搜索入口。");
                fixture.WaitForCancellation = true;
                ClickFixtureAction(qwenSubmit);
                await WaitForQwenAsync(() => fixture.Requests == 5, "启动可取消的 HTTP 请求");
                ClickFixtureAction(qwenCancel);
                await WaitForQwenAsync(() => fixture.Canceled.Task.IsCompleted, "取消 HTTP 请求");
                await Layout();
                await VerifyQwenCanceledQueriesAsync();
                return "Offline Qwen search passed: companion settings import without credentials, native UI navigation, " +
                    "authenticated HTTP request with web search, answer and source, 401/429, cancel and stale navigation.";
            }
            finally
            {
                Environment.SetEnvironmentVariable(environmentKey, previousKey);
                Navigate("首页");
                qwenOpenUri = null;
                qwenRunSearch = null;
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        async Task VerifyQwenCanceledQueriesAsync()
        {
            var waiting = new TaskCompletionSource<QwenSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken seen = default;
            qwenRunSearch = (question, key, endpoint, model, token) => { seen = token; return waiting.Task; };
            ClickFixtureAction(qwenSubmit);
            CheckQwen(seen.CanBeCanceled && !qwenSubmit.IsEnabled && qwenCancel.IsEnabled, "正在搜索时的操作状态错误。");
            ClickFixtureAction(qwenCancel);
            CheckQwen(seen.IsCancellationRequested && qwenSubmit.IsEnabled && !qwenCancel.IsEnabled, "取消后无法重新搜索。");

            var previous = waiting;
            waiting = new TaskCompletionSource<QwenSearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            ClickFixtureAction(qwenSubmit);
            previous.SetResult(new QwenSearchResult("old response", new QwenSearchSource[0]));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            CheckQwen(!qwenSubmit.IsEnabled && qwenCancel.IsEnabled && qwenAnswer.Text != "old response",
                "已取消的请求覆盖了正在进行的新查询。");
            ClickFixtureAction(TopLaunchButton);
            CheckQwen(seen.IsCancellationRequested && currentPage == "首页", "离开搜索页面时未取消请求。");
            waiting.SetResult(new QwenSearchResult("stale navigation", new QwenSearchSource[0]));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            ArrangeLayoutProbe(960, 600);
            ClickFixtureAction(FixtureButton(PageHost, QwenSearchPage));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            CheckQwen(qwenSubmit.IsEnabled && !qwenCancel.IsEnabled && qwenAnswer.Text != "stale navigation",
                "离开后完成的请求覆盖了重新打开的搜索页。");
        }
    }
}
