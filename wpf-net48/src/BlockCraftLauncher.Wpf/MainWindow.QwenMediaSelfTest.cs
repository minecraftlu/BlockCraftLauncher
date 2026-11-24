using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        sealed class MediaFixture
        {
            public byte[] Png;
            public readonly byte[] Mp4 = { 0,0,0,24,102,116,121,112,105,115,111,109,
                0,0,0,1,105,115,111,109,105,115,111,50 };
            public int Images, Videos, Polls, Downloads;
            public volatile bool HoldVideo, FailVideo, FailDownload;
            public string LastPayload;
        }

        sealed class MediaFixtureHandler : HttpMessageHandler
        {
            const string Output = "https://dashscope-result-bj.oss-cn-beijing.aliyuncs.com/fixture/";
            readonly MediaFixture state;
            public MediaFixtureHandler(MediaFixture state) { this.state = state; }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.RequestUri.AbsoluteUri.StartsWith(Output, StringComparison.Ordinal))
                {
                    CheckQwen(request.Method == HttpMethod.Get && request.Headers.Authorization == null,
                        "作品下载错误地携带了模型凭据");
                    Interlocked.Increment(ref state.Downloads);
                    if (state.FailDownload) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    byte[] bytes = request.RequestUri.AbsolutePath.EndsWith(".png") ? state.Png : state.Mp4;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                }
                CheckQwen(request.Headers.Authorization?.Parameter == QwenFixtureKey, "创作未使用用户选择的连接");
                string route = request.RequestUri.AbsolutePath;
                object output;
                if (request.Method == HttpMethod.Post)
                {
                    state.LastPayload = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (route.EndsWith("/multimodal-generation/generation"))
                    {
                        Interlocked.Increment(ref state.Images);
                        output = new { choices = new[] { new { message = new { content = new[] { new { image = Output + "image.png" } } } } } };
                    }
                    else if (route.EndsWith("/video-generation/video-synthesis"))
                    {
                        Interlocked.Increment(ref state.Videos);
                        output = new { task_id = "media-ui-fixture" };
                    }
                    else throw new InvalidOperationException("Unexpected media fixture route.");
                }
                else if (request.Method == HttpMethod.Get && route == "/api/v1/tasks/media-ui-fixture")
                {
                    Interlocked.Increment(ref state.Polls);
                    if (state.HoldVideo) await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    output = new { task_id = "media-ui-fixture", task_status = state.FailVideo ? "FAILED" : "SUCCEEDED",
                        video_url = Output + "movie.mp4" };
                }
                else throw new InvalidOperationException("Unexpected media fixture route.");
                string json = new JavaScriptSerializer().Serialize(new { output });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
        }

        // A fully local, opaque PNG for the picture picker/decoder and the offline UI screenshot.
        static byte[] MediaFixturePng()
        {
            const int w = 480, h = 256;
            var pixels = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int column = x / 12, row = y / 10;
                int back = 11 + (int)(3 * Math.Sin(column * 0.27 + 0.6));
                int front = 17 + (int)(4 * Math.Sin(column * 0.15 - 0.8));
                int color = row < 6 ? 0x87b3c7 : 0xbdd6d6;
                if (x / 24 >= 14 && x / 24 < 16 && row >= 3 && row < 7) color = 0xf5d187;
                if (row >= back) color = row == back ? 0x839f9b : 0x607f83;
                if (row >= front) color = row == front ? 0x789269 : 0x516e5b;
                if (row >= 20 && column < 4 + (row - 20) * 3) color = (column + row) % 4 == 1 ? 0x79aaab : 0x5a919a;
                if (column >= 28 && column <= 30 && row >= 13 && row <= 19) color = 0x3b5150;
                if (column >= 25 && column <= 33 && row >= 8 && row <= 14) color = row == 8 ? 0x577b68 : 0x426656;
                if (column >= 27 && column <= 31 && row >= 6 && row < 9) color = 0x577b68;
                int offset = (y * w + x) * 3;
                pixels[offset] = (byte)color;
                pixels[offset + 1] = (byte)(color >> 8);
                pixels[offset + 2] = (byte)(color >> 16);
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3)));
            using (var buffer = new MemoryStream()) { encoder.Save(buffer); return buffer.ToArray(); }
        }

        internal async Task<string> VerifyQwenMediaAsync()
        {
            await QwenMediaServiceCheck.RunAsync();
            string folder = Path.Combine(Path.GetTempPath(), "blockcraft-media-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            qwenSettingsPath = Path.Combine(folder, "connection.json");
            qwenCompanionPath = Path.Combine(folder, "companion.json");
            qwenHistoryPath = Path.Combine(folder, "history.json");
            qwenMediaTaskPath = Path.Combine(folder, "media-task.json");
            var fixture = new MediaFixture { Png = MediaFixturePng() };
            qwenMediaClientFactory = () => new QwenMediaService(new MediaFixtureHandler(fixture), TimeSpan.FromMilliseconds(10));
            string asset = Path.Combine(folder, "方块山谷.png");
            File.WriteAllBytes(asset, fixture.Png);
            async Task Submit(bool resume = false)
            {
                var button = resume ? mediaResume : mediaSubmit;
                CheckQwen(button.IsEnabled && button.Visibility == Visibility.Visible, "创作操作不可用");
                ClickFixtureAction(button);
                await WaitForQwenAsync(() => mediaRequest == null, "创作界面完成");
            }
            try
            {
                ShowActivated = false;
                ShowInTaskbar = false;
                SuppressAutomaticUpdateCheck = true;
                if (!IsVisible) Show();
                Navigate(QwenSearchPage);
                ArrangeLayoutProbe(960, 600);
                qwenApiKey.Password = QwenFixtureKey;
                qwenEndpoint.Text = QwenFixtureEndpoint;
                ClickFixtureAction(qwenMediaTab);
                CheckQwen(qwenMediaBody.Visibility == Visibility.Visible && mediaMode.Items.Count == 4, "创作导航或类型缺失");
                await Submit();
                CheckQwen(fixture.Images + fixture.Videos == 0, "空描述发起了付费调用");
                CheckQwen(!SetQwenMediaDraft("image", new string('a', QwenMediaService.MaxPrompt + 1)), "接受了超过模型上限的描述");
                CheckQwen(SetQwenMediaDraft("image", "绘制一幅像素风格的山谷，远山、清晨的河流和方块树。"), "无法从聊天打开图片草稿");
                fixture.FailDownload = true;
                await Submit();
                CheckQwen(fixture.Images == 1 && mediaPendingOutput != null && !mediaSubmit.IsEnabled, "下载中断丢失已受理的创作");
                fixture.FailDownload = false;
                await Submit(true);
                CheckQwen(fixture.Images == 1 && mediaImage.Source != null && File.Exists(mediaOutputPath), "取回图片时重复生成或预览失败");
                string saved = Path.Combine(folder, "保存的作品.png");
                await SaveQwenMediaOutputAsync(saved);
                CheckQwen(File.ReadAllBytes(saved).SequenceEqual(fixture.Png), "保存的 PNG 与原作品不同");

                string previous = mediaOutputPath;
                SetQwenMediaDraft("edit", "将山谷画成柔和的像素插画，保留树和河流。");
                await Submit();
                CheckQwen(fixture.Images == 1 && mediaInputPath == null, "编辑未选择图片就提交了");
                SetQwenMediaInput(asset);
                CheckQwen(mediaInputPreview.Source != null && mediaInputName.Text == Path.GetFileName(asset), "原图预览未显示");
                await Submit();
                CheckQwen(fixture.Images == 2 && fixture.LastPayload.Contains("data:image/png;base64,") &&
                    !fixture.LastPayload.Contains(folder) && !File.Exists(previous), "编辑请求未包含选中原图或未清理旧预览");
                SetMediaStatus("离线自检示例 · 图片处理完成，可预览并另存为 PNG。");
                ArrangeLayoutProbe(960, 600);
                PageScroll.ScrollToBottom();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                ArrangeLayoutProbe(960, 600, "qwen-media");

                SetQwenMediaDraft("video", "生成山谷晨雾缓慢散去的视频。");
                fixture.HoldVideo = true;
                ClickFixtureAction(mediaSubmit);
                await WaitForQwenAsync(() => fixture.Polls == 1 && ReadPendingMediaTask()?.Id == "media-ui-fixture", "任务编号与轮询");
                string journal = File.ReadAllText(qwenMediaTaskPath);
                CheckQwen(!journal.Contains(QwenFixtureKey) && !journal.Contains(mediaPrompt.Text), "视频记录包含密钥或创作描述");
                ClickFixtureAction(mediaStop);
                await WaitForQwenAsync(() => mediaRequest == null && mediaResume.Visibility == Visibility.Visible, "停止视频等待");
                CheckQwen(!SetQwenMediaDraft("image", "另一个草稿"), "意外覆盖了已受理的视频任务");
                fixture.HoldVideo = false;
                await Submit(true);
                CheckQwen(fixture.Videos == 1 && fixture.Polls == 2 && mediaOutputIsVideo && mediaVideo.Source?.IsFile == true &&
                    !File.Exists(qwenMediaTaskPath), "恢复视频重复提交或遗留任务");
                string movie = Path.Combine(folder, "保存的视频.mp4");
                await SaveQwenMediaOutputAsync(movie);
                CheckQwen(File.ReadAllBytes(movie).SequenceEqual(fixture.Mp4), "保存的 MP4 与返回结果不同");
                SetQwenMediaDraft("image-video", "从原图开始缓慢向河边移动。");
                fixture.FailVideo = true;
                await Submit();
                CheckQwen(fixture.LastPayload.Contains("wan2.6-i2v") && fixture.LastPayload.Contains("data:image/png;base64,") &&
                    mediaPendingTask == null && mediaSubmit.IsEnabled && !File.Exists(qwenMediaTaskPath), "终止的图生视频任务未释放");
                string temporaryFolder = mediaTempFolder;
                DisposeQwenMediaPage();
                CheckQwen(!Directory.Exists(temporaryFolder) && File.Exists(movie) && File.Exists(saved), "关闭时临时作品或已保存作品清理错误");
                return "PASS · 创作四种入口、原图预览、PNG/MP4 另存、已受理任务取消与恢复、终止后重新创作、临时文件清理。媒体响应来自离线测试。";
            }
            finally
            {
                Close();
                Directory.Delete(folder, true);
            }
        }
    }
}
