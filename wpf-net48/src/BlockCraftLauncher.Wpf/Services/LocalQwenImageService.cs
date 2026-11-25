using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;
using static BlockCraftLauncher.Wpf.Services.QwenSearchService;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class LocalQwenImageService : IDisposable
    {
        internal static string GeneratorWeights => Path.GetFileName(LocalQwenImageAsset.Generator.PathName);
        internal static string EditorWeights => Path.GetFileName(LocalQwenImageAsset.Editor.PathName);
        internal static string EncoderWeights => Path.GetFileName(LocalQwenImageAsset.Encoder.PathName);
        internal static string VaeWeights => Path.GetFileName(LocalQwenImageAsset.Vae.PathName);
        const int MaxImageBytes = 32 * 1024 * 1024;
        const int MaxJsonBytes = 2 * 1024 * 1024;
        const string OutputNode = "10";
        readonly HttpClient client;
        readonly TimeSpan pollInterval;

        public LocalQwenImageService(Uri endpoint)
            : this(endpoint, new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }, TimeSpan.FromSeconds(2)) { }

        internal LocalQwenImageService(Uri endpoint, HttpMessageHandler handler, TimeSpan pollInterval)
        {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp ||
                (endpoint.Host != "127.0.0.1" && endpoint.Host != "[::1]") || endpoint.UserInfo.Length != 0 ||
                endpoint.PathAndQuery != "/" || endpoint.Fragment.Length != 0)
                throw new ArgumentException("千问图像引擎需要本机回环地址。", nameof(endpoint));
            this.pollInterval = pollInterval;
            client = new HttpClient(handler) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        }

        public async Task GenerateAsync(string prompt, string inputImagePath, string destination,
            IProgress<string> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            prompt = (prompt ?? "").Trim();
            if (prompt.Length == 0 || prompt.Length > 4000) throw new QwenSearchException("请输入 1 至 4000 个字的图片描述。");
            if (!IsLocalFile(destination) || !string.Equals(Path.GetExtension(destination), ".png", StringComparison.OrdinalIgnoreCase))
                throw new QwenSearchException("请选择本机的 PNG 保存位置。");
            if (string.Equals(Path.GetFullPath(destination), inputImagePath == null ? null : Path.GetFullPath(inputImagePath), StringComparison.OrdinalIgnoreCase))
                throw new QwenSearchException("请将修改结果保存为另一张图片。");

            string uploadName = null;
            string jobId = null;
            try
            {
                if (inputImagePath != null)
                {
                    progress?.Report("正在读取原图…");
                    uploadName = await UploadAsync(inputImagePath, ct).ConfigureAwait(false);
                }
                progress?.Report("正在提交本机图像任务…");
                string clientId = Guid.NewGuid().ToString();
                long seed = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & 0x001fffffffffffffL;
                jobId = Guid.NewGuid().ToString();
                var payload = new { prompt_id = jobId, client_id = clientId, prompt = CreateGraph(prompt, uploadName, seed) };
                object accepted = await JsonAsync(HttpMethod.Post, "prompt", JsonContent(payload), ct).ConfigureAwait(false);
                if (Field(accepted, "prompt_id") as string != jobId) throw new QwenSearchException("本机图像引擎返回的任务编号不匹配。");
                string filename = await WaitForImageAsync(jobId, progress, ct).ConfigureAwait(false);
                progress?.Report("正在校验并保存 PNG…");
                var path = "view?type=temp&subfolder=&filename=" + Uri.EscapeDataString(filename);
                byte[] image = await ReadResponseAsync(HttpMethod.Get, path, null, MaxImageBytes, ct).ConfigureAwait(false);
                await Task.Run(() => SavePng(image, destination, ct), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (jobId != null) await CancelJobAsync(jobId).ConfigureAwait(false);
                throw;
            }
            catch (HttpRequestException)
            {
                ct.ThrowIfCancellationRequested();
                throw new QwenSearchException("无法连接本机 ComfyUI 图像引擎，请检查引擎状态后重试。");
            }
        }

        // Core nodes from Comfy-Org/workflow_templates: image_qwen_Image_2512.json and
        // image_qwen_image_edit_2511.json. Both select the base model path without the optional Lightning LoRA.
        internal static Dictionary<string, object> CreateGraph(string prompt, string inputName, long seed)
        {
            bool edit = inputName != null;
            var graph = new Dictionary<string, object>
            {
                ["1"] = Node("UNETLoader", new { unet_name = edit ? EditorWeights : GeneratorWeights, weight_dtype = "default" }),
                ["2"] = Node("CLIPLoader", new { clip_name = EncoderWeights, type = "qwen_image", device = "default" }),
                ["3"] = Node("VAELoader", new { vae_name = VaeWeights }),
                ["4"] = Node("ModelSamplingAuraFlow", new { model = Link("1"), shift = 3.1 })
            };
            if (edit)
            {
                graph["11"] = Node("LoadImage", new { image = inputName + " [temp]" });
                graph["12"] = Node("FluxKontextImageScale", new { image = Link("11") });
                graph["13"] = Node("CFGNorm", new { model = Link("4"), strength = 1.0 });
                graph["14"] = Node("TextEncodeQwenImageEditPlus", new { clip = Link("2"), vae = Link("3"), image1 = Link("12"), prompt });
                graph["15"] = Node("TextEncodeQwenImageEditPlus", new { clip = Link("2"), vae = Link("3"), image1 = Link("12"), prompt = "" });
                graph["5"] = Node("FluxKontextMultiReferenceLatentMethod", new { conditioning = Link("14"), reference_latents_method = "index_timestep_zero" });
                graph["6"] = Node("FluxKontextMultiReferenceLatentMethod", new { conditioning = Link("15"), reference_latents_method = "index_timestep_zero" });
                graph["7"] = Node("VAEEncode", new { pixels = Link("12"), vae = Link("3") });
            }
            else
            {
                graph["5"] = Node("CLIPTextEncode", new { clip = Link("2"), text = prompt });
                graph["6"] = Node("CLIPTextEncode", new { clip = Link("2"), text = "低分辨率，低画质，文字模糊，扭曲" });
                graph["7"] = Node("EmptySD3LatentImage", new { width = 1024, height = 1024, batch_size = 1 });
            }
            graph["8"] = Node("KSampler", new { model = Link(edit ? "13" : "4"), positive = Link("5"), negative = Link("6"),
                latent_image = Link("7"), seed, steps = edit ? 40 : 50, cfg = 4.0, sampler_name = "euler", scheduler = "simple", denoise = 1.0 });
            graph["9"] = Node("VAEDecode", new { samples = Link("8"), vae = Link("3") });
            graph[OutputNode] = Node("PreviewImage", new { images = Link("9") });
            return graph;
        }

        static object Node(string type, object inputs) => new { class_type = type, inputs };
        static object[] Link(string id) => new object[] { id, 0 };
        static bool IsLocalFile(string path) => !string.IsNullOrWhiteSpace(path) && path.Length > 3 &&
            char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

        async Task<string> UploadAsync(string path, CancellationToken ct)
        {
            if (!IsLocalFile(path)) throw new QwenSearchException("请选择本机的 PNG 或 JPEG 原图。");
            byte[] bytes;
            using (var file = File.OpenRead(path))
            {
                if (file.Length < 24 || file.Length > MaxImageBytes) throw new QwenSearchException("原图需为 32 MB 以内的 PNG 或 JPEG。");
                bytes = await ReadLimitedAsync(file, MaxImageBytes, ct).ConfigureAwait(false);
            }
            bool jpeg = await Task.Run(() => ValidateImage(bytes, false, ct), ct).ConfigureAwait(false);
            string name = "blockcraft_" + Guid.NewGuid().ToString("N") + (jpeg ? ".jpg" : ".png");
            using (var form = new MultipartFormDataContent())
            {
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new MediaTypeHeaderValue(jpeg ? "image/jpeg" : "image/png");
                form.Add(file, "image", name);
                form.Add(new StringContent("temp"), "type");
                object uploaded = await JsonAsync(HttpMethod.Post, "upload/image", form, ct).ConfigureAwait(false);
                if (Field(uploaded, "name") as string != name || Field(uploaded, "type") as string != "temp" ||
                    Field(uploaded, "subfolder") as string != "") throw new QwenSearchException("本机图像引擎未确认收到这张原图。");
            }
            return name;
        }

        async Task<string> WaitForImageAsync(string jobId, IProgress<string> progress, CancellationToken ct)
        {
            string lastStatus = null;
            for (;;)
            {
                object job = await JsonAsync(HttpMethod.Get, "api/jobs/" + jobId, null, ct).ConfigureAwait(false);
                if (Field(job, "id") as string != jobId) throw new QwenSearchException("图像引擎没有找到本次任务。");
                string status = Field(job, "status") as string;
                switch (status)
                {
                    case "pending":
                        if (status != lastStatus) progress?.Report("已排队，等待本机 GPU…");
                        break;
                    case "in_progress":
                        if (status != lastStatus) progress?.Report("正在本机推理，首次载入模型可能需要几分钟…");
                        break;
                    case "completed": return ResultFilename(job);
                    case "cancelled": throw new QwenSearchException("本机图像任务已取消。");
                    case "failed": throw new QwenSearchException("本机图像推理失败，请检查可用显存及模型文件。");
                    default: throw new QwenSearchException("本机图像引擎返回了未知的任务状态。");
                }
                lastStatus = status;
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
        }

        static string ResultFilename(object job)
        {
            var output = Field(Field(job, "outputs"), OutputNode);
            var image = Items(output, "images").FirstOrDefault();
            var filename = Field(image, "filename") as string;
            if (string.IsNullOrEmpty(filename) || filename.Length > 240 || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                filename != Path.GetFileName(filename) || !filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                Field(image, "type") as string != "temp" || Field(image, "subfolder") as string != "")
                throw new QwenSearchException("本机图像任务没有返回有效的 PNG 结果。");
            return filename;
        }

        async Task CancelJobAsync(string jobId)
        {
            // The pinned runtime atomically cancels only this ID whether it is running or queued.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await ReadResponseAsync(HttpMethod.Post, "api/jobs/" + jobId + "/cancel", null, MaxJsonBytes, timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is OperationCanceledException || ex is QwenSearchException) { }
            }
        }

        static StringContent JsonContent(object value) =>
            new StringContent(new JavaScriptSerializer().Serialize(value), Encoding.UTF8, "application/json");

        async Task<object> JsonAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct)
        {
            var bytes = await ReadResponseAsync(method, path, content, MaxJsonBytes, ct).ConfigureAwait(false);
            return ReadJson(new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes }, Encoding.UTF8.GetString(bytes));
        }

        async Task<byte[]> ReadResponseAsync(HttpMethod method, string path, HttpContent content, int limit, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var request = new HttpRequestMessage(method, path) { Content = content })
            {
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            if (path == "prompt" && (int)response.StatusCode == 400)
                                throw new QwenSearchException("本机图像引擎未接受工作流，请确认对应模型与组件均已安装。");
                            throw new QwenSearchException("本机图像引擎请求失败（HTTP " + (int)response.StatusCode + "）。");
                        }
                        if (response.Content == null) return Array.Empty<byte>();
                        if (response.Content.Headers.ContentLength > limit) throw new QwenSearchException("本机图像引擎返回的数据过大。");
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (timeout.Token.Register(stream.Dispose))
                            return await ReadLimitedAsync(stream, limit, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception) when (timeout.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw new QwenSearchException("本机图像引擎响应超时。");
                }
            }
        }

        static async Task<byte[]> ReadLimitedAsync(Stream input, int limit, CancellationToken ct)
        {
            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(chunk, 0, chunk.Length, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > limit) throw new QwenSearchException("图像引擎返回的数据过大。");
                    buffer.Write(chunk, 0, count);
                }
                return buffer.ToArray();
            }
        }

        static bool ValidateImage(byte[] bytes, bool pngOnly, CancellationToken ct)
        {
            try
            {
                using (var input = new MemoryStream(bytes, false))
                {
                    var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                    bool jpeg = decoder is JpegBitmapDecoder;
                    if (!(decoder is PngBitmapDecoder) && (!jpeg || pngOnly)) throw new QwenSearchException("图片格式无效。");
                    var frame = decoder.Frames[0];
                    if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 8192 || frame.PixelHeight > 8192 ||
                        (long)frame.PixelWidth * frame.PixelHeight > 16L * 1024 * 1024) throw new QwenSearchException("图片分辨率过高。");
                    // Force decoding so a partial or corrupt stream cannot replace an existing result.
                    int stride = (frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8;
                    const int rows = 64;
                    var strip = new byte[stride * Math.Min(rows, frame.PixelHeight)];
                    for (int y = 0; y < frame.PixelHeight; y += rows)
                    {
                        ct.ThrowIfCancellationRequested();
                        frame.CopyPixels(new System.Windows.Int32Rect(0, y, frame.PixelWidth, Math.Min(rows, frame.PixelHeight - y)), strip, stride, 0);
                    }
                    return jpeg;
                }
            }
            catch (Exception ex) when (ex is FormatException || ex is NotSupportedException || ex is IOException ||
                ex is ArgumentException || ex is System.Runtime.InteropServices.COMException)
            { throw new QwenSearchException("无法读取图片，文件可能不完整。"); }
        }

        static void SavePng(byte[] bytes, string destination, CancellationToken ct)
        {
            ValidateImage(bytes, true, ct);
            ct.ThrowIfCancellationRequested();
            string full = Path.GetFullPath(destination);
            string temp = Path.Combine(Path.GetDirectoryName(full), "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    file.Write(bytes, 0, bytes.Length);
                ct.ThrowIfCancellationRequested();
                if (File.Exists(full)) File.Replace(temp, full, null);
                else File.Move(temp, full);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public void Dispose() => client.Dispose();
    }
}
