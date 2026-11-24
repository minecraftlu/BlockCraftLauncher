using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class CurseForgeServiceDiscoverySanityChecks
    {
        internal static async Task RunAsync()
        {
            string document = "{\"serviceUrl\":\"\"}";
            string manifest = "https://raw.githubusercontent.com/minecraftlu/BlockCraftLauncher/curseforge-service-config/service.json";
            var now = DateTime.UtcNow;
            var status = HttpStatusCode.OK;
            int requests = 0;
            TaskCompletionSource<bool> pause = null;
            bool invalidRequest = false;
            var discovery = new CurseForgeServiceDiscovery(_ => new Handler(async request =>
            {
                Interlocked.Increment(ref requests);
                if (request.Method != HttpMethod.Get || request.RequestUri.AbsoluteUri != manifest ||
                    request.Headers.Contains("x-api-key") || request.Headers.Authorization != null ||
                    request.Headers.Contains("Cookie")) invalidRequest = true;
                if (pause != null) await pause.Task.ConfigureAwait(false);
                var response = new HttpResponseMessage(status) {
                    Content = new StringContent(document, Encoding.UTF8, "application/json")
                };
                if (status == HttpStatusCode.Redirect) response.Headers.Location = new Uri("https://other.example/");
                return response;
            }), () => now);
            var settings = new LauncherConfig();
            var ct = CancellationToken.None;
            if (await discovery.ResolveAsync(settings, ct) != null ||
                await discovery.ResolveAsync(settings, ct) != null || requests != 1)
                throw new InvalidOperationException("未配置时应短暂缓存空结果");
            now = now.AddSeconds(31);
            document = "{\"serviceUrl\":\"https://published.example/\"}";
            pause = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Enumerable.Range(0, 5).Select(i => discovery.ResolveAsync(settings, ct)).ToArray();
            pause.SetResult(true);
            var ready = await Task.WhenAll(pending).ConfigureAwait(false);
            if (requests != 2 || ready.Any(uri => uri?.AbsoluteUri != "https://published.example/") ||
                discovery.FreshOrigin?.Host != "published.example")
                throw new InvalidOperationException("并发发现应共享一次有效的配置读取");
            pause = null;
            now = now.AddMinutes(6);
            status = HttpStatusCode.Redirect;
            if (discovery.FreshOrigin != null || await discovery.ResolveAsync(settings, ct) != null || requests != 3)
                throw new InvalidOperationException("过期配置或重定向不应启用下载服务");
            status = HttpStatusCode.OK;
            foreach (string invalid in new[] {
                "not-json", "{\"serviceUrl\":\"http://insecure.example\"}",
                "{\"serviceUrl\":\"https://localhost\"}", "{\"serviceUrl\":\"https://127.0.0.1\"}",
                "{\"serviceUrl\":\"https://user:secret@operator.example/path\"}",
                "{\"serviceUrl\":\"https://operator.example/?token=value\"}",
                "{\"serviceUrl\":\"https://operator.example/\"}" + new string(' ', 4096)
            })
            {
                document = invalid;
                if (await discovery.ResolveAsync(settings, ct, refresh: true) != null)
                    throw new InvalidOperationException("异常或超长服务配置应保持停用");
            }
            int beforeRetry = requests;
            document = "{\"serviceUrl\":\"https://new.example\"}";
            if ((await discovery.ResolveAsync(settings, ct, refresh: true))?.Host != "new.example" ||
                invalidRequest || requests != beforeRetry + 1)
                throw new InvalidOperationException("手动重试应匿名读取固定地址并发现新配置");
        }

        sealed class Handler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> respond;
            internal Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) { this.respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                respond(request);
        }
    }
}
