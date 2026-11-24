using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class QwenSearchSource
    {
        public int Index { get; }
        public string Title { get; }
        public Uri Url { get; }
        public QwenSearchSource(int index, string title, Uri url) { Index = index; Title = title; Url = url; }
    }

    internal sealed class QwenSearchResult
    {
        public string Answer { get; }
        public IReadOnlyList<QwenSearchSource> Sources { get; }
        public QwenSearchResult(string answer, IReadOnlyList<QwenSearchSource> sources) { Answer = answer; Sources = sources; }
    }

    internal sealed class QwenSearchException : Exception
    {
        public QwenSearchException(string message) : base(message) { }
    }

    internal sealed class QwenSearchService : IDisposable
    {
        const string ChatPath = "/compatible-mode/v1/chat/completions";
        const int MaxResponseBytes = 512 * 1024;
        public const int MaxQuestionLength = 2000;
        public const string DefaultModel = "qwen4-max";
        public const string DefaultEndpoint = "https://trial.cn-beijing.maas.aliyuncs.com" + ChatPath;
        readonly HttpClient client;

        public QwenSearchService(LauncherConfig settings) : this(NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false)) { }
        internal QwenSearchService(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        public static bool TryGetEndpoint(string value, out Uri endpoint)
        {
            endpoint = null;
            value = value?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.IndexOfAny(new[] { '@', '\\', '?', '#' }) >= 0 ||
                value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns) return false;

            string host = uri.IdnHost.ToLowerInvariant();
            if (host != "dashscope.aliyuncs.com" && host != "dashscope-intl.aliyuncs.com" && !Regex.IsMatch(host,
                @"\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.(?:cn-beijing|ap-southeast-1)\.maas\.aliyuncs\.com\z")) return false;
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length != 0 && path != "/compatible-mode/v1" && path != ChatPath) return false;

            endpoint = new Uri("https://" + host + ChatPath);
            return true;
        }

        public static bool IsValidModel(string model) => model != null &&
            Regex.IsMatch(model.Trim(), @"\Aqwen(?:[0-9]|-)[a-z0-9.-]{1,75}\z");

        public async Task<QwenSearchResult> SearchAsync(string question, string apiKey, string endpoint, string model, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            question = question?.Trim();
            if (string.IsNullOrEmpty(question) || question.Length > MaxQuestionLength)
                throw new QwenSearchException("请输入 1 到 2000 个字符的问题。");
            apiKey = apiKey?.Trim();
            var address = ValidateConnection(apiKey, endpoint, model);

            var serializer = new JavaScriptSerializer { MaxJsonLength = MaxResponseBytes, RecursionLimit = 32 };
            var payload = new {
                model = model.Trim(), messages = new[] { new { role = "user", content = question } },
                enable_search = true, search_options = new { forced_search = true },
                enable_thinking = false, stream = false, max_tokens = 4096
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, address))
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(120));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(serializer.Serialize(payload), Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!response.IsSuccessStatusCode) throw new QwenSearchException(StatusMessage((int)response.StatusCode));
                        if (response.Content == null) throw new QwenSearchException("千问没有返回回答。");
                        await NetworkDownloader.CopyBodyAsync(response, buffer, MaxResponseBytes, 0, response.Content.Headers.ContentLength,
                            null, TimeSpan.FromSeconds(30), limit.Token).ConfigureAwait(false);
                        var result = ParseResponse(serializer, Encoding.UTF8.GetString(buffer.ToArray()));
                        ct.ThrowIfCancellationRequested();
                        return result;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new QwenSearchException("搜索超时，请稍后重试。"); }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new QwenSearchException("千问连接中断或响应超出大小限制，请检查网络后重试。");
                }
            }
        }

        internal static Uri ValidateConnection(string apiKey, string endpoint, string model)
        {
            if (!TryGetEndpoint(endpoint, out var address)) throw new QwenSearchException("请填写阿里云百炼北京或新加坡地域的 HTTPS 接口地址。");
            if (!IsValidModel(model)) throw new QwenSearchException("请填写千问模型 ID，例如 qwen4-max。");
            if (string.IsNullOrEmpty(apiKey) || apiKey.Length > 1024 || apiKey.Any(c => c < '!' || c > '~'))
                throw new QwenSearchException("请填写当前地域的百炼 API Key。");
            return address;
        }

        internal static string StatusMessage(int status)
        {
            switch (status)
            {
                case 400: return "千问拒绝了搜索参数，请确认所选模型支持联网搜索。";
                case 401: return "千问认证失败，请检查 API Key 是否有效并与接口地域一致。";
                case 402: return "百炼账户余额或额度不足，请检查账户状态。";
                case 403: return "当前 API Key 无权使用此模型，请在百炼检查业务空间、模型权限和额度。";
                case 404: return "未找到千问模型或接口，请检查模型 ID 和业务空间地址。";
                case 429: return "百炼调用已达限额，请稍后再试或检查账户配额。";
                default: return "千问服务未能完成搜索（HTTP " + status + "）。";
            }
        }

        internal static object Field(object obj, string key) => obj is Dictionary<string, object> fields && fields.TryGetValue(key, out var value) ? value : null;
        internal static object[] Items(object obj, string key) => Field(obj, key) as object[] ?? Array.Empty<object>();

        internal static object ReadJson(JavaScriptSerializer serializer, string json)
        {
            try { return serializer.DeserializeObject(json); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException)
            {
                throw new QwenSearchException("千问返回的数据格式无效，请稍后重试。");
            }
        }

        internal static QwenSearchResult ParseResponse(JavaScriptSerializer serializer, string json)
        {
            object data = ReadJson(serializer, json);
            var message = Field(Items(data, "choices").FirstOrDefault(), "message");
            string answer = Field(message, "content") as string;
            if (string.IsNullOrWhiteSpace(answer)) throw new QwenSearchException("千问没有返回回答，请确认模型支持联网搜索。");
            var sources = new List<QwenSearchSource>();
            AppendSources(data, message, sources);
            return new QwenSearchResult(answer.Trim(), sources.AsReadOnly());
        }

        internal static void AppendSources(object data, object message, List<QwenSearchSource> sources)
        {
            // Citations are optional. Only expose structured links actually returned by the service.
            var searchInfo = Field(data, "search_info") ?? Field(message, "search_info");
            if (sources.Count >= 48 || (searchInfo == null && Field(message, "annotations") == null)) return;
            var seenUrls = new HashSet<Uri>(sources.Select(s => s.Url));
            var seenIndices = new HashSet<int>(sources.Select(s => s.Index));
            int nextIndex = 1;
            while (seenIndices.Contains(nextIndex)) nextIndex++;
            var references = Items(searchInfo, "search_results").Concat(
                Items(message, "annotations").Where(entry => Field(entry, "type") as string == "url_citation").Select(entry => Field(entry, "url_citation")));
            foreach (var entry in references)
            {
                if (sources.Count >= 48) break;
                if (!Uri.TryCreate(Field(entry, "url") as string, UriKind.Absolute, out var url) || !IsSafeSource(url) || seenUrls.Contains(url)) continue;
                int index = Field(entry, "index") is int explicitIndex && explicitIndex > 0 ? explicitIndex : nextIndex;
                if (!seenIndices.Add(index)) continue;
                seenUrls.Add(url);
                while (seenIndices.Contains(nextIndex)) nextIndex++;
                string title = (Field(entry, "title") as string)?.Trim();
                if (string.IsNullOrEmpty(title)) title = url.Host;
                sources.Add(new QwenSearchSource(index, title.Length > 200 ? title.Substring(0, 200) : title, url));
            }
        }

        public static bool IsSafeSource(Uri url)
        {
            if (url == null || !url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
                !url.IsDefaultPort || url.UserInfo.Length != 0 || url.IsLoopback || url.HostNameType != UriHostNameType.Dns ||
                url.OriginalString.IndexOf('\\') >= 0 || url.OriginalString.Any(char.IsControl)) return false;
            string host = url.IdnHost.ToLowerInvariant().TrimEnd('.');
            return host.Contains(".") && !host.EndsWith(".localhost", StringComparison.Ordinal) && !host.EndsWith(".local", StringComparison.Ordinal) &&
                !host.EndsWith(".internal", StringComparison.Ordinal) && !host.EndsWith(".lan", StringComparison.Ordinal) &&
                host != "home.arpa" && !host.EndsWith(".home.arpa", StringComparison.Ordinal);
        }

        public void Dispose() => client.Dispose();
    }
}
