using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class LocalModelRuntimeChecks
    {
        sealed class LocalResponse : HttpMessageHandler
        {
            internal string ModelList = "{\"models\":[{\"name\":\"qwen3.5:4b\"}]}";
            internal string PullResult = "{\"status\":\"pulling manifest\"}\r\n{\"status\":\"success\"}";
            internal int Requests;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Requests++;
                string path = request.RequestUri.AbsolutePath;
                if (request.RequestUri.Host != "127.0.0.1") throw new Exception("本机协议访问了外网。");
                if (path == "/api/version") return Reply("{\"version\":\"1.0\"}");
                if (path == "/api/tags") return Reply(ModelList);
                if (path == "/api/pull" && request.Method == HttpMethod.Post)
                {
                    var payload = await request.Content.ReadAsStringAsync();
                    if (!payload.Contains("\"model\":\"qwen3.5:4b\"") || !payload.Contains("\"stream\":true"))
                        throw new Exception("模型下载请求不正确。");
                    return Reply(PullResult);
                }
                throw new Exception("意外的本机请求。");
            }
            static HttpResponseMessage Reply(string data) =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8) };
        }

        internal static async Task RunAsync()
        {
            var local = new Uri("http://127.0.0.1:11434/");
            var stub = new LocalResponse();
            using (var runtime = new LocalModelRuntime(new LauncherConfig(), stub))
            {
                if (await runtime.EnsureReadyAsync("", false, null, CancellationToken.None) != local || stub.Requests != 2)
                    throw new Exception("没有复用本机已安装的模型。");
                var choice = Path.Combine(Path.GetTempPath(), "BlockCraft-test-" + Guid.NewGuid().ToString("N"));
                if (await runtime.CheckReadyAsync(choice, CancellationToken.None) || Directory.Exists(choice) || stub.Requests != 2)
                    throw new Exception("检查自选目录时访问了其他位置。");
                using (var stop = new CancellationTokenSource())
                {
                    stop.Cancel();
                    try { await runtime.EnsureReadyAsync(choice, true, null, stop.Token); throw new Exception("取消后仍尝试安装。"); }
                    catch (OperationCanceledException) { }
                }
            }

            stub = new LocalResponse();
            using (var api = new OllamaLocalApi(stub))
            {
                stub.ModelList = "{\"models\":[{\"name\":\"qwen3.5:4b\",\"remote_model\":\"qwen3.5\"}]}";
                if (await api.HasModelAsync(local, CancellationToken.None)) throw new Exception("云端模型被视为本机模型。");
                stub.ModelList = "{\"models\":[{\"name\":\"qwen3.5:0.8b\"}]}";
                if (await api.HasModelAsync(local, CancellationToken.None)) throw new Exception("另一个规格的模型被视为已就绪。");
                await api.PullAsync(local, null, CancellationToken.None);
                stub.PullResult = "{\"status\":\"pulling manifest\"}\n";
                try { await api.PullAsync(local, null, CancellationToken.None); throw new Exception("下载中断被视为成功。"); }
                catch (EndOfStreamException) { }
                stub.PullResult = "{\"error\":\"registry unavailable\"}\n";
                try { await api.PullAsync(local, null, CancellationToken.None); throw new Exception("注册表错误被视为成功。"); }
                catch (InvalidOperationException) { }
                stub.PullResult = new string('x', 32769);
                try { await api.PullAsync(local, null, CancellationToken.None); throw new Exception("Ollama 进度超出长度上限。"); }
                catch (InvalidDataException) { }
                try { await api.PullAsync(new Uri("http://192.0.2.1/"), null, CancellationToken.None); throw new Exception("地址未限定为回环。"); }
                catch (ArgumentException) { }
            }
            await OllamaPortablePackageChecks.RunAsync();
        }
    }
}
