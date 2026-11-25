using System;
using System.Collections.Generic;
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
    internal static class LocalQwenImageChecks
    {
        const string EditFixture = "blockcraft_fixture.png";
        const string Prompt = "把村庄改成黄昏";
        static readonly Uri Endpoint = new Uri("http://127.0.0.1:8188/");

        // Consumed by tests/check_local_qwen_graphs.py with the pinned real CPU-side ComfyUI validator.
        internal static void ExportGraphs(string directory)
        {
            foreach (var kind in new[] { "generate", "edit" })
            {
                var graph = LocalQwenImageService.CreateGraph(Prompt, kind == "edit" ? EditFixture : null, 42);
                File.WriteAllText(Path.Combine(directory, kind + ".json"), new JavaScriptSerializer().Serialize(graph), new UTF8Encoding(false));
            }
        }

        internal static async Task RunAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blockcraft-local-image-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                foreach (string invalid in new[] { "https://example.com/", "http://127.0.0.1@localhost/", "http://127.0.0.1:8188/remote" })
                {
                    try { using (new LocalQwenImageService(new Uri(invalid))) { } }
                    catch (ArgumentException) { continue; }
                    throw new InvalidOperationException("Qwen local image accepted an external endpoint.");
                }
                var fake = new Handler();
                string source = Path.Combine(root, "village.png");
                string output = Path.Combine(root, "result.png");
                File.WriteAllBytes(source, fake.Png);
                using (var service = new LocalQwenImageService(Endpoint, fake, TimeSpan.Zero))
                {
                    await service.GenerateAsync(Prompt, null, output, null, CancellationToken.None);
                    Require(File.ReadAllBytes(output).SequenceEqual(fake.Png), "generated PNG bytes");
                    await service.GenerateAsync(Prompt, source, output, null, CancellationToken.None);
                    Require(fake.Jobs.Count == 2 && fake.UploadCount == 1 && fake.Polls == 4, "one submission per action");
                    Require(File.ReadAllBytes(source).SequenceEqual(fake.Png), "original image retained");
                    fake.InvalidResult = true;
                    try
                    {
                        await service.GenerateAsync(Prompt, null, output, null, CancellationToken.None);
                        throw new InvalidOperationException("accepted truncated PNG");
                    }
                    catch (QwenSearchException) { }
                    Require(File.ReadAllBytes(output).SequenceEqual(fake.Png), "kept previous complete PNG");
                    fake.InvalidResult = false;
                    using (fake.CancelOnPoll = new CancellationTokenSource())
                    {
                        try
                        {
                            await service.GenerateAsync(Prompt, null, output, null, fake.CancelOnPoll.Token);
                            throw new InvalidOperationException("ignored cancellation");
                        }
                        catch (OperationCanceledException) { }
                        Require(fake.DeletedJob == fake.CurrentId, "cancel affected only the current job");
                    }
                }
            }
            finally { Directory.Delete(root, true); }
        }

        static void Require(bool passed, string stage)
        {
            if (!passed) throw new InvalidOperationException("Local Qwen image check failed: " + stage);
        }

        sealed class Handler : HttpMessageHandler
        {
            internal readonly byte[] Png;
            internal readonly HashSet<string> Jobs = new HashSet<string>();
            internal int UploadCount, Polls;
            internal bool InvalidResult;
            internal string DeletedJob, CurrentId;
            internal CancellationTokenSource CancelOnPoll;
            string uploaded;
            readonly JavaScriptSerializer json = new JavaScriptSerializer();

            internal Handler()
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, new byte[1024], 64)));
                using (var stream = new MemoryStream()) { encoder.Save(stream); Png = stream.ToArray(); }
            }

            HttpResponseMessage Json(object value) => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json.Serialize(value), Encoding.UTF8, "application/json") };

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Require(request.RequestUri.Host == "127.0.0.1" && request.RequestUri.Port == 8188, "loopback request");
                string path = request.RequestUri.AbsolutePath;
                if (path == "/upload/image")
                {
                    var parts = (MultipartFormDataContent)request.Content;
                    var part = parts.Single(p => p.Headers.ContentDisposition.Name.Trim('"') == "image");
                    uploaded = part.Headers.ContentDisposition.FileName.Trim('"');
                    Require(uploaded.StartsWith("blockcraft_", StringComparison.Ordinal) && (await part.ReadAsByteArrayAsync()).SequenceEqual(Png), "upload image");
                    UploadCount++;
                    var type = parts.Single(p => p.Headers.ContentDisposition.Name.Trim('"') == "type");
                    Require(await type.ReadAsStringAsync() == "temp", "original image upload is temporary");
                    return Json(new { name = uploaded, subfolder = "", type = "temp" });
                }
                if (path == "/prompt")
                {
                    var payload = json.DeserializeObject(await request.Content.ReadAsStringAsync());
                    Require(Jobs.Add((string)Field(payload, "client_id")), "unique client ID");
                    var graph = Field(payload, "prompt");
                    var loader = Field(Field(graph, "1"), "inputs");
                    bool edit = Field(graph, "11") != null;
                    Require((string)Field(loader, "unet_name") == (edit ? LocalQwenImageService.EditorWeights : LocalQwenImageService.GeneratorWeights), "selected weights");
                    Require((string)Field(Field(Field(graph, "2"), "inputs"), "type") == "qwen_image", "encoder family");
                    Require((string)Field(Field(graph, "10"), "class_type") == "PreviewImage", "temporary result");
                    if (edit)
                    {
                        Require(uploaded != null && (string)Field(Field(Field(graph, "11"), "inputs"), "image") == uploaded + " [temp]", "own uploaded input");
                        Require((string)Field(Field(graph, "14"), "class_type") == "TextEncodeQwenImageEditPlus", "edit conditioning");
                        Require((string)Field(Field(Field(graph, "14"), "inputs"), "prompt") == Prompt, "edit prompt");
                    }
                    else Require((string)Field(Field(Field(graph, "5"), "inputs"), "text") == Prompt, "generation prompt");
                    CurrentId = (string)Field(payload, "prompt_id");
                    Require(Guid.TryParseExact(CurrentId, "D", out _), "specific job ID");
                    return Json(new { prompt_id = CurrentId });
                }
                if (path == "/api/jobs/" + CurrentId)
                {
                    Polls++;
                    if (CancelOnPoll != null) { CancelOnPoll.Cancel(); ct.ThrowIfCancellationRequested(); }
                    if (Polls % 2 != 0) return Json(new { id = CurrentId, status = "in_progress" });
                    var image = new { filename = "ComfyUI_temp_test.png", subfolder = "", type = "temp" };
                    var outputs = new Dictionary<string, object> { ["10"] = new { images = new[] { image } } };
                    return Json(new { id = CurrentId, status = "completed", outputs });
                }
                if (path == "/view")
                {
                    Require(request.RequestUri.Query.Contains("type=temp") && request.RequestUri.Query.Contains("filename=ComfyUI_temp_test.png"), "result address");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InvalidResult ? Png.Take(16).ToArray() : Png) };
                }
                if (path == "/api/jobs/" + CurrentId + "/cancel" && request.Method == HttpMethod.Post)
                {
                    DeletedJob = CurrentId;
                    return Json(new { cancelled = true });
                }
                throw new InvalidOperationException("Unexpected image request " + path);
            }
        }
    }
}
