using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Optional, explicitly authorized first-party Minecraft Java skin upload.
    /// No third-party website, OAuth login flow, background synchronization,
    /// retries or logs containing bearer tokens are used.
    /// </summary>
    internal static class MinecraftSkinUploadService
    {
        internal const string Endpoint =
            "https://api.minecraftservices.com/minecraft/profile/skins";

        static int BigEndian32(byte[] bytes, int offset)
        {
            return bytes[offset] << 24 | bytes[offset + 1] << 16 |
                bytes[offset + 2] << 8 | bytes[offset + 3];
        }

        internal static HttpRequestMessage BuildRequest(
            string accessToken, byte[] png, bool slim)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException(
                    "没有有效的 Minecraft Java 正版会话，请先重新登录微软正版账号。");
            if (png == null || png.Length < 33 || png.Length > 4 * 1024 * 1024 ||
                !png.Take(8).SequenceEqual(
                    new byte[] {137,80,78,71,13,10,26,10}) ||
                BigEndian32(png, 16) != 64 || BigEndian32(png, 20) != 64)
                throw new InvalidDataException(
                    "上传的皮肤必须是标准 64×64 PNG，且小于 4 MiB。");
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
                "application/json"));
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(slim ? "slim" : "classic",
                Encoding.ASCII), "variant");
            var file = new ByteArrayContent(png);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "blockcraft-custom-skin.png");
            request.Content = form;
            return request;
        }

        public static async Task UploadAsync(string token, byte[] png,
            bool slim, CancellationToken ct)
        {
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false, // never redirect a bearer token
                UseCookies = false
            })
            using (var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(35)
            })
            using (var request = BuildRequest(token, png, slim))
            using (var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (response.IsSuccessStatusCode) return;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException(
                        "Minecraft 正版登录已失效。请重新登录微软账号后再上传皮肤。");
                if (response.StatusCode == HttpStatusCode.Forbidden)
                    throw new InvalidOperationException(
                        "Minecraft 拒绝修改此账户的皮肤。请确认该账号拥有 Minecraft Java 版。");
                if ((int)response.StatusCode == 429)
                    throw new InvalidOperationException(
                        "Minecraft 官方接口暂时限制皮肤上传次数。请过一段时间再手动尝试，"
                        + "启动器不会自动重试，以保护你的账号。");
                if ((int)response.StatusCode >= 300 &&
                    (int)response.StatusCode < 400)
                    throw new InvalidOperationException(
                        "官方皮肤服务发生重定向，为保护微软登录凭据已阻止继续上传。");
                throw new InvalidOperationException(
                    "Minecraft 官方皮肤上传未成功（HTTP " +
                    (int)response.StatusCode +
                    "）。请检查图片格式、网络和账号状态，或在官方启动器中上传。");
            }
        }

        internal static void RunOfflineSelfTest()
        {
            void Require(bool ok, string description)
            {
                if (!ok) throw new InvalidOperationException(
                    "正版皮肤上传安全自检：" + description);
            }
            var skin = new SkinEditorDocument();
            byte[] png = skin.ExportPngBytes();
            using (var request = BuildRequest("dummy-selftest-token", png, false))
            {
                Require(request.Method == HttpMethod.Post &&
                    request.RequestUri.AbsoluteUri == Endpoint &&
                    request.Headers.Authorization?.Scheme == "Bearer" &&
                    request.Headers.Authorization?.Parameter == "dummy-selftest-token",
                    "只使用官方 HTTPS 皮肤接口及 Minecraft 访问令牌");
                // HTTP multipart uses runtime-dependent quoting rules.
                // Inspect typed headers/parts rather than interpreting PNG
                // binary data as text and checking raw boundary text.
                var parts = ((MultipartFormDataContent)request.Content).ToArray();
                Require(parts.Length == 2 &&
                    parts[0].Headers.ContentDisposition?.Name?.Trim('"') == "variant" &&
                    parts[1].Headers.ContentDisposition?.Name?.Trim('"') == "file" &&
                    parts[1].Headers.ContentType?.MediaType == "image/png" &&
                    parts[0].ReadAsStringAsync().GetAwaiter().GetResult() == "classic" &&
                    parts[1].ReadAsByteArrayAsync().GetAwaiter().GetResult().SequenceEqual(png),
                    "只上传指定的经典皮肤 PNG 和模型");
            }
            using (var slim = BuildRequest("dummy-selftest-token", png, true))
            {
                string form = slim.Content.ReadAsStringAsync()
                    .GetAwaiter().GetResult();
                Require(form.Contains("slim"), "Alex 纤细模型请求正确");
            }
            bool rejected = false;
            try { BuildRequest("dummy", new byte[128], false); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "拒绝伪装和损坏的皮肤图片");
            rejected = false;
            try { BuildRequest(null, png, false); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "离线账号不得上传皮肤");
        }
    }
}
