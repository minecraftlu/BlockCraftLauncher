using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class OllamaLocalApi : IDisposable
    {
        readonly HttpClient client;
        const int MaxJsonBytes = 1024 * 1024;
        const int MaxProgressBytes = 32 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal OllamaLocalApi(HttpMessageHandler handler) =>
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        static Uri Route(Uri address, string path)
        {
            if (address == null || !address.IsAbsoluteUri || address.Scheme != "http" || address.Host != "127.0.0.1" ||
                address.Port <= 0 || address.UserInfo.Length != 0 || address.PathAndQuery != "/" || address.Fragment.Length != 0)
                throw new ArgumentException("Ollama 服务地址必须是本机回环地址。");
            return new Uri(address, path);
        }

        static Dictionary<string, object> Parse(byte[] bytes, int count)
        {
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes, RecursionLimit = 32 };
                return json.DeserializeObject(Utf8.GetString(bytes, 0, count)) as Dictionary<string, object>
                    ?? throw new InvalidDataException("Ollama 返回了无效的数据。");
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            { throw new InvalidDataException("Ollama 返回了无效的数据。", ex); }
        }

        async Task<Dictionary<string, object>> GetAsync(Uri address, string path, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using (var response = await client.GetAsync(Route(address, path), HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false))
                using (var buffer = new MemoryStream())
                {
                    response.EnsureSuccessStatusCode();
                    await NetworkDownloader.CopyBodyAsync(response, buffer, MaxJsonBytes, 0, response.Content.Headers.ContentLength,
                        null, TimeSpan.FromSeconds(3), timeout.Token).ConfigureAwait(false);
                    return Parse(buffer.GetBuffer(), (int)buffer.Length);
                }
            }
        }

        static string Field(Dictionary<string, object> obj, string key) =>
            obj.TryGetValue(key, out var value) ? value as string : null;

        internal async Task<bool> IsServerAsync(Uri address, CancellationToken ct)
        {
            try
            {
                var version = await GetAsync(address, "api/version", ct).ConfigureAwait(false);
                return !string.IsNullOrWhiteSpace(Field(version, "version"));
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is OperationCanceledException ||
                ex is ObjectDisposedException || ex is TimeoutException)
            { ct.ThrowIfCancellationRequested(); return false; }
        }

        internal async Task<bool> HasModelAsync(Uri address, CancellationToken ct)
        {
            var tags = await GetAsync(address, "api/tags", ct).ConfigureAwait(false);
            if (!tags.TryGetValue("models", out var list) || !(list is object[] models))
                throw new InvalidDataException("无法读取本机 Ollama 模型列表。");
            foreach (var item in models)
            {
                if (!(item is Dictionary<string, object> model)) continue;
                if (string.Equals(Field(model, "name") ?? Field(model, "model"), LocalModelRuntime.Model,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(Field(model, "remote_model")) && string.IsNullOrEmpty(Field(model, "remote_host")))
                    return true;
            }
            return false;
        }

        internal async Task PullAsync(Uri address, IProgress<string> progress, CancellationToken ct)
        {
            var payload = new JavaScriptSerializer().Serialize(new { model = LocalModelRuntime.Model, stream = true });
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(HttpMethod.Post, Route(address, "api/pull")))
            {
                var idleLimit = TimeSpan.FromMinutes(5);
                idle.CancelAfter(idleLimit);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token)
                        .ConfigureAwait(false))
                    using (idle.Token.Register(response.Dispose))
                    {
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("Ollama 无法下载千问模型（HTTP " + (int)response.StatusCode + "）。");
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var line = new MemoryStream())
                        {
                            byte[] bytes = new byte[8192];
                            var cadence = Stopwatch.StartNew();
                            while (true)
                            {
                                idle.CancelAfter(idleLimit);
                                int count = await stream.ReadAsync(bytes, 0, bytes.Length, idle.Token).ConfigureAwait(false);
                                int start = 0;
                                for (int i = 0; i <= count; i++)
                                {
                                    if (i != count && bytes[i] != (byte)'\n') continue;
                                    if (line.Length + i - start > MaxProgressBytes) throw new InvalidDataException("Ollama 下载进度过长。");
                                    line.Write(bytes, start, i - start);
                                    start = i + 1;
                                    if (i == count && count != 0) break; // partial line continues in next read
                                    if (line.Length > 0 && Progress(line, cadence, progress)) return;
                                    line.SetLength(0);
                                }
                                if (count == 0) throw new EndOfStreamException("模型下载中断，可再次点击安装以继续。");
                            }
                        }
                    }
                }
                catch (Exception) when (idle.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new TimeoutException("模型下载长时间没有响应，可检查网络后继续安装。");
                }
            }
        }

        static bool Progress(MemoryStream line, Stopwatch cadence, IProgress<string> progress)
        {
            int length = (int)line.Length;
            if (line.GetBuffer()[length - 1] == (byte)'\r') length--;
            if (length == 0) return false;
            var item = Parse(line.GetBuffer(), length);
            if (!string.IsNullOrEmpty(Field(item, "error")))
                throw new InvalidOperationException("Ollama 未能下载或校验模型。请检查网络、代理和可用磁盘空间后重试。");
            string status = Field(item, "status");
            if (status == "success") return true;
            if (cadence.ElapsedMilliseconds < 300 || progress == null) return false;
            cadence.Restart();
            if (item.TryGetValue("completed", out var done) && item.TryGetValue("total", out var total) &&
                long.TryParse(Convert.ToString(done, CultureInfo.InvariantCulture), out long current) &&
                long.TryParse(Convert.ToString(total, CultureInfo.InvariantCulture), out long size) && current >= 0 && size >= current)
                progress.Report(string.Format("正在下载千问模型（当前文件 {0:N0} / {1:N0} MB）", current / 1000000d, size / 1000000d));
            else if (status == "verifying sha256 digest") progress.Report("正在校验千问模型…");
            else progress.Report("正在准备千问模型…");
            return false;
        }

        public void Dispose() => client.Dispose();
    }
}
