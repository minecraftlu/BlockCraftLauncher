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
using System.Windows.Media.Imaging;
using BlockCraftLauncher.Wpf.Models;
using static BlockCraftLauncher.Wpf.Services.QwenSearchService;

namespace BlockCraftLauncher.Wpf.Services
{
    internal enum QwenMediaKind { TextToImage, ImageEdit, TextToVideo, ImageToVideo }

    internal sealed class QwenMediaResult
    {
        internal QwenMediaResult(QwenMediaKind kind, Uri url) { Kind = kind; Url = url; }
        public QwenMediaKind Kind { get; }
        public Uri Url { get; }
        public bool IsVideo => QwenMediaService.IsVideoKind(Kind);
        public string Extension => IsVideo ? ".mp4" : ".png";
        public string Model => QwenMediaService.ModelFor(Kind);
    }

    internal sealed class QwenMediaService : IDisposable
    {
        const string ImagePath = "/api/v1/services/aigc/multimodal-generation/generation";
        const string VideoPath = "/api/v1/services/aigc/video-generation/video-synthesis";
        const int MiB = 1024 * 1024;
        const int MaxResponse = MiB;
        internal const int MaxPrompt = 1500;
        readonly HttpClient client;
        readonly TimeSpan pollInterval;

        public QwenMediaService(LauncherConfig settings)
            : this(NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false), TimeSpan.FromSeconds(15)) { }
        internal QwenMediaService(HttpMessageHandler handler, TimeSpan pollInterval)
        {
            if (pollInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
            this.pollInterval = pollInterval;
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        }

        internal static bool IsVideoKind(QwenMediaKind kind) =>
            kind == QwenMediaKind.TextToVideo || kind == QwenMediaKind.ImageToVideo;

        internal static string ModelFor(QwenMediaKind kind)
        {
            switch (kind)
            {
                case QwenMediaKind.TextToImage:
                case QwenMediaKind.ImageEdit: return "qwen-image-2.0";
                case QwenMediaKind.TextToVideo: return "wan2.6-t2v";
                case QwenMediaKind.ImageToVideo: return "wan2.6-i2v";
                default: throw new QwenSearchException("请选择创作方式。");
            }
        }

        public async Task<QwenMediaResult> GenerateAsync(QwenMediaKind kind, string prompt, string inputImagePath,
            string apiKey, string endpoint, IProgress<string> progress, CancellationToken ct, Action<string> onTaskAccepted = null)
        {
            ct.ThrowIfCancellationRequested();
            string model = ModelFor(kind);
            prompt = prompt?.Trim();
            if (string.IsNullOrEmpty(prompt) || prompt.Length > MaxPrompt)
                throw new QwenSearchException("请输入 1 至 1500 个字符的创作要求。");
            apiKey = apiKey?.Trim();
            Uri region = ValidateConnection(apiKey, endpoint, QwenSearchService.DefaultModel);
            bool video = IsVideoKind(kind);
            // Encoding large local images and serializing their data URLs must stay off the UI thread.
            string json = await Task.Run(() =>
            {
                string image = kind == QwenMediaKind.ImageEdit || kind == QwenMediaKind.ImageToVideo
                    ? ReadLocalImage(inputImagePath, kind, ct) : null;
                object payload;
                if (video)
                {
                    var input = new Dictionary<string, object> { ["prompt"] = prompt };
                    if (image != null) input.Add("img_url", image);
                    var parameters = new Dictionary<string, object> { ["duration"] = 5 };
                    if (image == null) parameters.Add("size", "1280*720");
                    else parameters.Add("resolution", "720P");
                    payload = new { model, input, parameters };
                }
                else
                {
                    var content = new List<object>();
                    if (image != null) content.Add(new { image });
                    content.Add(new { text = prompt });
                    var parameters = new Dictionary<string, object> { ["n"] = 1 };
                    if (image == null) parameters.Add("size", "1024*1024");
                    payload = new { model, input = new { messages = new[] { new { role = "user", content } } }, parameters };
                }
                ct.ThrowIfCancellationRequested();
                return new JavaScriptSerializer { MaxJsonLength = 32 * MiB }.Serialize(payload);
            }, ct).ConfigureAwait(false);
            progress?.Report(video ? "正在提交视频任务…" : "正在生成图片…");
            object output = await ApiAsync(new Uri(region, video ? VideoPath : ImagePath), apiKey, json, video, ct).ConfigureAwait(false);
            if (!video)
            {
                var message = Field(Items(output, "choices").FirstOrDefault(), "message");
                string url = Items(message, "content").Select(part => Field(part, "image") as string)
                    .FirstOrDefault(value => !string.IsNullOrEmpty(value));
                return Result(kind, url);
            }
            string id = Field(output, "task_id") as string;
            ValidateTaskId(id);
            onTaskAccepted?.Invoke(id);
            return await PollAsync(kind, id, region, apiKey, progress, ct, delayFirst: true).ConfigureAwait(false);
        }

        public Task<QwenMediaResult> ResumeVideoAsync(QwenMediaKind kind, string taskId, string apiKey,
            string endpoint, IProgress<string> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsVideoKind(kind)) throw new QwenSearchException("请选择已有的视频任务。");
            ValidateTaskId(taskId);
            apiKey = apiKey?.Trim();
            Uri region = ValidateConnection(apiKey, endpoint, QwenSearchService.DefaultModel);
            return PollAsync(kind, taskId, region, apiKey, progress, ct, delayFirst: false);
        }

        static void ValidateTaskId(string id)
        {
            if (id == null || !Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z"))
                throw new QwenSearchException("视频服务返回的任务编号无效。");
        }

        async Task<QwenMediaResult> PollAsync(QwenMediaKind kind, string id, Uri region, string key,
            IProgress<string> progress, CancellationToken ct, bool delayFirst)
        {
            var address = new Uri(region, "/api/v1/tasks/" + id);
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(TimeSpan.FromMinutes(30));
                try
                {
                    progress?.Report("正在等待视频任务，可稍后继续查询。");
                    while (true)
                    {
                        if (delayFirst) await Task.Delay(pollInterval, limit.Token).ConfigureAwait(false);
                        delayFirst = true;
                        object output = await ApiAsync(address, key, null, false, limit.Token).ConfigureAwait(false);
                        string returnedId = Field(output, "task_id") as string;
                        if (returnedId != null && returnedId != id) throw new QwenSearchException("服务返回的任务编号与当前任务不一致。");
                        switch (Field(output, "task_status") as string)
                        {
                            case "PENDING": progress?.Report("视频任务排队中…"); break;
                            case "RUNNING": progress?.Report("正在生成视频…"); break;
                            case "SUCCEEDED": return Result(kind, Field(output, "video_url") as string);
                            case "FAILED": throw EndedTask("视频生成失败。请检查输入内容与模型权限。");
                            case "CANCELED": throw EndedTask("视频任务已由服务端取消。");
                            case "UNKNOWN": throw EndedTask("视频任务已过期或在当前业务空间中不存在。");
                            default: throw new QwenSearchException("暂时无法识别视频任务状态，请稍后继续查询。");
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new QwenSearchException("视频仍在处理，可稍后继续查询此任务。"); }
            }
        }

        static QwenSearchException EndedTask(string message)
        {
            var failure = new QwenSearchException(message);
            failure.Data["QwenMediaTaskEnded"] = true;
            return failure;
        }

        async Task<object> ApiAsync(Uri address, string key, string json, bool asyncHeader, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(json == null ? HttpMethod.Get : HttpMethod.Post, address))
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(TimeSpan.FromMinutes(5));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                if (asyncHeader) request.Headers.Add("X-DashScope-Async", "enable");
                if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            switch ((int)response.StatusCode)
                            {
                                case 400: throw new QwenSearchException("服务未接受创作要求，请检查提示词和素材格式。");
                                case 404: throw json == null ? EndedTask("任务已过期或不属于当前业务空间。") :
                                    new QwenSearchException("当前地域或业务空间未提供所选创作模型。");
                                default: throw new QwenSearchException(StatusMessage((int)response.StatusCode));
                            }
                        }
                        if (response.Content == null) throw new QwenSearchException("创作服务未返回结果。");
                        await NetworkDownloader.CopyBodyAsync(response, buffer, MaxResponse, 0, response.Content.Headers.ContentLength,
                            null, TimeSpan.FromSeconds(30), limit.Token).ConfigureAwait(false);
                        var data = ReadJson(new JavaScriptSerializer { MaxJsonLength = MaxResponse, RecursionLimit = 32 },
                            Encoding.UTF8.GetString(buffer.ToArray()));
                        return Field(data, "output") ?? throw new QwenSearchException("创作服务未返回有效结果，请检查模型权限与输入内容。");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new QwenSearchException("创作服务响应超时。"); }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException)
                { ct.ThrowIfCancellationRequested(); throw new QwenSearchException("与创作服务的连接中断或响应过大。"); }
            }
        }

        static string ReadLocalImage(string path, QwenMediaKind kind, CancellationToken ct)
        {
            bool video = IsVideoKind(kind);
            if (string.IsNullOrWhiteSpace(path) || !Regex.IsMatch(path, @"\A[A-Za-z]:[\\/]"))
                throw new QwenSearchException("请从本机选择一张 PNG 或 JPEG 图片。");
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") throw new QwenSearchException("请选择 PNG 或 JPEG 图片。");
                byte[] bytes;
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length < 24 || stream.Length > (video ? 20 : 10) * MiB)
                        throw new QwenSearchException(video ? "首帧图片须小于等于 20 MB。" : "待处理图片须小于等于 10 MB。");
                    using (var reader = new BinaryReader(stream)) bytes = reader.ReadBytes((int)stream.Length);
                }
                ct.ThrowIfCancellationRequested();
                bool png = IsPng(bytes);
                if (png != (ext == ".png") || (!png && !(bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)))
                    throw new QwenSearchException("图片内容与文件格式不一致。");
                using (var stream = new MemoryStream(bytes, false))
                {
                    var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                    if (video && (frame.PixelWidth < 240 || frame.PixelHeight < 240 || frame.PixelWidth > 8000 || frame.PixelHeight > 8000))
                        throw new QwenSearchException("视频首帧的宽和高均须在 240 至 8000 像素之间。");
                    if (video && png && HasPngAlpha(bytes))
                        throw new QwenSearchException("视频首帧需要无透明通道的图片，可先另存为 JPEG。");
                }
                ct.ThrowIfCancellationRequested();
                return (png ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(bytes);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException ||
                ex is FormatException || ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            { throw new QwenSearchException("无法读取图片，请确认本机文件可以打开。"); }
        }

        static bool IsPng(byte[] b) => b.Length >= 24 && b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71 &&
            b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10 && Unsigned32(b, 8) == 13 &&
            b[12] == 'I' && b[13] == 'H' && b[14] == 'D' && b[15] == 'R';

        static long Unsigned32(byte[] bytes, int offset) => ((long)bytes[offset] << 24) +
            (bytes[offset + 1] << 16) + (bytes[offset + 2] << 8) + bytes[offset + 3];

        static bool ValidOutputHeader(byte[] header, long length, bool video)
        {
            if (video)
            {
                long size = Unsigned32(header, 0);
                return size >= 16 && size <= length && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p';
            }
            long width = Unsigned32(header, 16), height = Unsigned32(header, 20);
            return IsPng(header) && width > 0 && width <= 8192 && height > 0 && height <= 8192 && width * height <= 16L * MiB;
        }

        static bool HasPngAlpha(byte[] bytes)
        {
            if (bytes.Length <= 25 || bytes[25] == 4 || bytes[25] == 6) return true;
            for (long offset = 8; offset + 12 <= bytes.Length;)
            {
                int i = (int)offset;
                string chunk = Encoding.ASCII.GetString(bytes, i + 4, 4);
                if (chunk == "tRNS") return true;
                if (chunk == "IDAT") break;
                offset += 12 + Unsigned32(bytes, i);
            }
            return false;
        }

        static QwenMediaResult Result(QwenMediaKind kind, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(char.IsControl) || value.Contains("\\") ||
                !Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
                url.UserInfo.Length != 0 || url.Fragment.Length != 0 ||
                !Regex.IsMatch(url.IdnHost, @"\A[a-z0-9][a-z0-9-]*\.oss(?:-[a-z0-9-]+)?\.aliyuncs\.com\z"))
                throw new QwenSearchException("服务未提供可验证的阿里云媒体下载地址。");
            return new QwenMediaResult(kind, url);
        }

        public async Task DownloadAsync(QwenMediaResult result, string destination, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (result == null) throw new QwenSearchException("请先生成作品。");
            var url = Result(result.Kind, result.Url?.OriginalString).Url;
            if (!string.Equals(Path.GetExtension(destination), result.Extension, StringComparison.OrdinalIgnoreCase))
                throw new QwenSearchException("保存文件的扩展名应为 " + result.Extension + "。");
            string part = Path.GetFullPath(destination) + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    limit.CancelAfter(TimeSpan.FromMinutes(8));
                    // Signed object-storage URLs authorize themselves. API credentials belong only on the API requests above.
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode || response.Content == null)
                            throw new QwenSearchException("作品下载失败，临时下载地址可能已过期。");
                        using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
                        {
                            long length = await NetworkDownloader.CopyBodyAsync(response, stream, (result.IsVideo ? 256 : 48) * MiB, 0,
                                response.Content.Headers.ContentLength, null, TimeSpan.FromSeconds(30), limit.Token).ConfigureAwait(false);
                            stream.Position = 0;
                            var header = new byte[24];
                            bool hasHeader = length >= 24 && stream.Read(header, 0, header.Length) == header.Length;
                            if (!hasHeader || !ValidOutputHeader(header, length, result.IsVideo))
                                throw new QwenSearchException("下载内容与作品格式不符。");
                        }
                    }
                    limit.Token.ThrowIfCancellationRequested();
                    if (File.Exists(destination)) File.Replace(part, destination, null);
                    else File.Move(part, destination);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new QwenSearchException("下载超时，可重试保存此作品。"); }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            { ct.ThrowIfCancellationRequested(); throw new QwenSearchException("下载或保存失败，请检查网络、文件大小和目录权限后重试。"); }
            finally { if (File.Exists(part)) File.Delete(part); }
        }

        public void Dispose() => client.Dispose();
    }
}
