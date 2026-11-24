using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static BlockCraftLauncher.Wpf.Services.QwenSearchService;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class QwenMediaServiceCheck
    {
        const string Workspace = "qa-workspace.ap-southeast-1.maas.aliyuncs.com";
        const string Endpoint = "https://" + Workspace + "/compatible-mode/v1/chat/completions";
        const string ObjectHost = "dashscope-result-sh.oss-accelerate.aliyuncs.com";
        const string Key = "fixture-only";
        const string Prompt = "小屋外的雪景";
        const string TaskId = "fixture-task-42";

        internal static async Task RunAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blockcraft-media-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var fake = new MediaHandler();
                string source = Path.Combine(root, "素材.png");
                File.WriteAllBytes(source, fake.Png);
                using (var service = new QwenMediaService(fake, TimeSpan.Zero))
                {
                    foreach (QwenMediaKind kind in Enum.GetValues(typeof(QwenMediaKind)))
                    {
                        fake.Kind = kind;
                        fake.Polls = 0;
                        string accepted = null;
                        int previous = fake.Posts;
                        var result = await service.GenerateAsync(kind, Prompt, source, Key, Endpoint, null,
                            CancellationToken.None, id => accepted = id);
                        bool video = QwenMediaService.IsVideoKind(kind);
                        Require(result.Kind == kind && result.IsVideo == video && fake.Posts == previous + 1 &&
                            (video ? accepted == TaskId && fake.Polls == 3 : accepted == null && fake.Polls == 0), "task lifetime");
                        string destination = Path.Combine(root, kind + result.Extension);
                        await service.DownloadAsync(result, destination, CancellationToken.None);
                        Require(File.ReadAllBytes(destination).SequenceEqual(video ? fake.Mp4 : fake.Png), "download bytes");
                    }

                    fake.Kind = QwenMediaKind.ImageEdit;
                    string jpegPath = Path.Combine(root, "素材.jpg");
                    var jpeg = new JpegBitmapEncoder();
                    using (var image = new MemoryStream(fake.Png))
                    using (var output = File.Create(jpegPath))
                    {
                        jpeg.Frames.Add(BitmapFrame.Create(image));
                        jpeg.Save(output);
                    }
                    fake.SourceData = "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(jpegPath));
                    await service.GenerateAsync(fake.Kind, Prompt, jpegPath, Key, Endpoint, null, CancellationToken.None);
                    fake.SourceData = null;
                    fake.Kind = QwenMediaKind.TextToVideo;
                    int posts = fake.Posts;
                    string savedId = null;
                    using (var stop = new CancellationTokenSource())
                    {
                        try
                        {
                            await service.GenerateAsync(fake.Kind, Prompt, null, Key, Endpoint, null, stop.Token,
                                id => { savedId = id; stop.Cancel(); });
                            throw new InvalidOperationException("Media check: cancellation missing");
                        }
                        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    }
                    Require(fake.Posts == posts + 1 && savedId == TaskId, "recoverable accepted task");
                    fake.Polls = 2;
                    var completed = await service.ResumeVideoAsync(fake.Kind, savedId, Key, Endpoint, null, CancellationToken.None);
                    Require(fake.Posts == posts + 1 && fake.Polls == 3, "resume without resubmitting");
                    await ExpectRejected(() => service.ResumeVideoAsync(fake.Kind, "../bad", Key, Endpoint, null, CancellationToken.None));
                    await ExpectRejected(() => service.GenerateAsync(fake.Kind, Prompt, null, Key, "https://example.com", null, CancellationToken.None));
                    Require(fake.Posts == posts + 1 && fake.Polls == 3, "rejected invalid request before network");

                    fake.BadAsset = true;
                    string existing = Path.Combine(root, "existing.mp4");
                    File.WriteAllText(existing, "original");
                    await ExpectRejected(() => service.DownloadAsync(completed, existing, CancellationToken.None));
                    Require(File.ReadAllText(existing) == "original" && Directory.GetFiles(root, "*.part").Length == 0, "atomic failed save");
                    fake.BadAsset = false;
                    await service.DownloadAsync(completed, existing, CancellationToken.None);
                    Require(File.ReadAllBytes(existing).SequenceEqual(fake.Mp4) && fake.Posts == posts + 1, "retry download");
                    await ExpectRejected(() => service.DownloadAsync(completed, Path.Combine(root, "bad.exe"), CancellationToken.None));
                    fake.BadUrl = true;
                    await ExpectRejected(() => service.ResumeVideoAsync(fake.Kind, savedId, Key, Endpoint, null, CancellationToken.None));
                    fake.BadUrl = false;
                    fake.PollStatus = "FAILED";
                    try
                    {
                        await service.ResumeVideoAsync(fake.Kind, savedId, Key, Endpoint, null, CancellationToken.None);
                        throw new InvalidOperationException("Media check: terminal failure missing");
                    }
                    catch (QwenSearchException ex)
                    { Require(Equals(ex.Data["QwenMediaTaskEnded"], true), "terminal task notification"); }

                    fake.Kind = QwenMediaKind.ImageToVideo;
                    File.WriteAllBytes(source, MakePng(32, false));
                    await ExpectRejected(() => service.GenerateAsync(fake.Kind, Prompt, source, Key, Endpoint, null, CancellationToken.None));
                    File.WriteAllBytes(source, MakePng(240, true));
                    await ExpectRejected(() => service.GenerateAsync(fake.Kind, Prompt, source, Key, Endpoint, null, CancellationToken.None));
                    Require(fake.Posts == posts + 1, "first-frame validation before submission");
                }
            }
            finally { Directory.Delete(root, true); }
        }

        static async Task ExpectRejected(Func<Task> action)
        {
            try { await action(); }
            catch (QwenSearchException ex)
            {
                Require(!ex.Message.Contains(Key) && !ex.Message.Contains("fake-signature"), "redacted service error");
                return;
            }
            throw new InvalidOperationException("Media check: expected validation failure");
        }

        static byte[] MakePng(int width, bool alpha)
        {
            var format = alpha ? PixelFormats.Bgra32 : PixelFormats.Rgb24;
            int stride = width * (alpha ? 4 : 3);
            var bitmap = BitmapSource.Create(width, width, 96, 96, format, null, new byte[stride * width], stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var buffer = new MemoryStream()) { encoder.Save(buffer); return buffer.ToArray(); }
        }

        static void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException("Media check failed: " + detail);
        }

        sealed class MediaHandler : HttpMessageHandler
        {
            public QwenMediaKind Kind;
            public int Posts, Polls;
            public bool BadAsset, BadUrl;
            public string PollStatus, SourceData;
            public readonly byte[] Png = MakePng(240, false);
            public readonly byte[] Mp4 = { 0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 1, 105, 115, 111, 109, 109, 112, 52, 49 };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                bool video = QwenMediaService.IsVideoKind(Kind);
                string url = "https://" + (BadUrl ? "example.org" : ObjectHost) + "/object." + (video ? "mp4" : "png") + "?signature=fake-signature";
                if (request.RequestUri.Host == ObjectHost)
                {
                    Require(request.Method == HttpMethod.Get && request.Headers.Authorization == null && !request.Headers.Contains("X-DashScope-Async"),
                        "asset request contains only CDN authorization");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BadAsset ? new byte[28] : (video ? Mp4 : Png)) };
                }
                Require(request.RequestUri.Host == Workspace && request.Headers.Authorization?.Scheme == "Bearer" &&
                    request.Headers.Authorization?.Parameter == Key, "regional workspace authentication");
                if (request.Method == HttpMethod.Get)
                {
                    Require(request.RequestUri.AbsolutePath == "/api/v1/tasks/" + TaskId && !request.Headers.Contains("X-DashScope-Async"), "poll");
                    Polls++;
                    string status = PollStatus ?? (Polls == 1 ? "PENDING" : Polls == 2 ? "RUNNING" : "SUCCEEDED");
                    return Json(new { output = new { task_id = TaskId, task_status = status, video_url = url } });
                }
                Require(request.Method == HttpMethod.Post && request.Headers.Contains("X-DashScope-Async") == video, "submission headers");
                Posts++;
                var serializer = new JavaScriptSerializer();
                object payload = serializer.DeserializeObject(await request.Content.ReadAsStringAsync());
                object input = Field(payload, "input"), parameters = Field(payload, "parameters");
                Require(Field(payload, "model") as string == QwenMediaService.ModelFor(Kind), "native model");
                string data = SourceData ?? "data:image/png;base64," + Convert.ToBase64String(Png);
                if (video)
                {
                    Require(request.RequestUri.AbsolutePath == "/api/v1/services/aigc/video-generation/video-synthesis" &&
                        request.Headers.GetValues("X-DashScope-Async").Single() == "enable" && (int)Field(parameters, "duration") == 5 &&
                        Field(input, "prompt") as string == Prompt, "video request");
                    Require(Kind == QwenMediaKind.ImageToVideo
                        ? Field(parameters, "resolution") as string == "720P" && Field(input, "img_url") as string == data
                        : Field(parameters, "size") as string == "1280*720" && Field(input, "img_url") == null, "video input");
                    return Json(new { output = new { task_id = TaskId, task_status = "PENDING" } });
                }
                var message = Items(input, "messages").Single();
                var content = Items(message, "content");
                Require(request.RequestUri.AbsolutePath == "/api/v1/services/aigc/multimodal-generation/generation" &&
                    Field(message, "role") as string == "user" && (int)Field(parameters, "n") == 1 &&
                    Field(content.Last(), "text") as string == Prompt, "image prompt");
                Require(Kind == QwenMediaKind.ImageEdit
                    ? content.Length == 2 && Field(content[0], "image") as string == data && Field(parameters, "size") == null
                    : content.Length == 1 && Field(parameters, "size") as string == "1024*1024", "image input");
                return Json(new { output = new { choices = new[] { new { message = new { content = new[] { new { image = url } } } } } } });
            }

            static HttpResponseMessage Json(object value) => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new JavaScriptSerializer().Serialize(value), Encoding.UTF8, "application/json") };
        }
    }
}
