using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// A search result refers to a public PNG, never a player access token.
    /// Minecraft player lookups use Mojang endpoints; community text searches
    /// use DP-Plugins' documented public, unauthenticated skins endpoint.
    /// </summary>
    internal sealed class OnlineSkinEntry
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string TextureUrl { get; set; }
        public bool SlimArms { get; set; }
        public string Attribution { get; set; }

        public override string ToString()
        {
            return Name + "  ·  " + Source + (SlimArms ? "  ·  Alex" : "  ·  Steve");
        }
    }

    internal static class OnlineSkinSearchService
    {
        const int MaxJsonBytes = 256 * 1024;
        const int MaxPngBytes = 4 * 1024 * 1024;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = MaxJsonBytes
        };
        static readonly HttpClient Http = CreateHttp();

        static HttpClient CreateHttp()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip |
                    DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(16)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher/0.9.35");
            return client;
        }

        static Dictionary<string, object> Dict(object obj) =>
            obj as Dictionary<string, object>;

        static string Str(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var value) || value == null)
                return "";
            return Convert.ToString(value) ?? "";
        }

        static Dictionary<string, object> Object(Dictionary<string, object> dict, string key)
        {
            if (dict != null && dict.TryGetValue(key, out var value))
                return Dict(value);
            return null;
        }

        internal static bool ValidPlayerInput(string value)
        {
            value = (value ?? "").Trim();
            return Regex.IsMatch(value, @"^[A-Za-z0-9_]{3,16}$") ||
                Regex.IsMatch(value, @"^(?:[a-fA-F0-9]{32}|[a-fA-F0-9]{8}(?:-[a-fA-F0-9]{4}){3}-[a-fA-F0-9]{12})$");
        }

        static bool IsUuid(string value)
        {
            return Regex.IsMatch(value, @"^[a-fA-F0-9]{32}$");
        }

        static Uri SafePublicSkinUri(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048)
                throw new InvalidDataException("皮肤来源没有可用的 PNG 地址");
            Uri uri;
            if (value.StartsWith("/", StringComparison.Ordinal) &&
                !value.StartsWith("//", StringComparison.Ordinal))
                uri = new Uri(new Uri("https://dpplugins.com"), value);
            else if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
                throw new InvalidDataException("皮肤地址无效");
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                IPAddress.TryParse(uri.Host, out _) || uri.Host.IndexOf('.') <= 0 ||
                uri.Host.Contains("..") ||
                uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("皮肤图片必须使用可信的 HTTPS 公网地址");
            return uri;
        }

        // Textures payloads can still contain legacy http://textures.minecraft.net
        // URLs. Upgrade that *specific* official host to HTTPS instead of
        // accepting arbitrary plaintext or arbitrary URL redirects.
        internal static string CanonicalMojangTexture(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase) ||
                (uri.Scheme != "http" && uri.Scheme != "https") ||
                uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                !Regex.IsMatch(uri.AbsolutePath, @"^/texture/[A-Fa-f0-9]{32,128}$"))
                throw new InvalidDataException("Mojang 返回了无效的皮肤贴图地址");
            return "https://textures.minecraft.net" + uri.AbsolutePath;
        }

        static async Task<byte[]> GetBytes(Uri url, int maximum, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await Http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound ||
                    response.StatusCode == HttpStatusCode.NoContent)
                    throw new FileNotFoundException("在线服务未找到匹配的皮肤或玩家");
                if ((int)response.StatusCode == 429)
                    throw new InvalidOperationException("在线皮肤服务请求过于频繁，请稍后重试");
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("在线皮肤服务返回 HTTP " +
                        (int)response.StatusCode + "，请检查网络或稍后重试");
                if (response.Content.Headers.ContentLength > maximum)
                    throw new InvalidDataException("在线皮肤响应超过安全大小限制");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[8192];
                    int length;
                    while ((length = await input.ReadAsync(chunk, 0, chunk.Length, ct)
                        .ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + length > maximum)
                            throw new InvalidDataException("在线皮肤响应超过安全大小限制");
                        buffer.Write(chunk, 0, length);
                    }
                    return buffer.ToArray();
                }
            }
        }

        static async Task<Dictionary<string, object>> GetJson(Uri uri, CancellationToken ct)
        {
            var bytes = await GetBytes(uri, MaxJsonBytes, ct).ConfigureAwait(false);
            try
            {
                return Dict(Json.DeserializeObject(Encoding.UTF8.GetString(bytes))) ??
                    throw new InvalidDataException("皮肤服务返回的 JSON 结构无效");
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("皮肤服务没有返回有效的 JSON", error);
            }
        }

        internal static OnlineSkinEntry ParseSessionProfile(
            Dictionary<string, object> session, string fallbackName)
        {
            if (session == null || !session.TryGetValue("properties", out var value) ||
                !(value is object[] items))
                throw new InvalidDataException("Mojang 玩家资料缺少皮肤信息");
            foreach (var entry in items.Select(Dict).Where(x => x != null))
            {
                if (Str(entry, "name") != "textures") continue;
                string encoded = Str(entry, "value");
                if (encoded.Length == 0 || encoded.Length > 30000)
                    throw new InvalidDataException("玩家皮肤资料异常");
                Dictionary<string, object> payload;
                try
                {
                    payload = Dict(Json.DeserializeObject(
                        Encoding.UTF8.GetString(Convert.FromBase64String(encoded))));
                }
                catch (Exception error) when (
                    error is ArgumentException || error is FormatException)
                {
                    throw new InvalidDataException("无法解析 Mojang 皮肤资料", error);
                }
                var skin = Object(Object(payload, "textures"), "SKIN");
                if (skin == null || string.IsNullOrWhiteSpace(Str(skin, "url")))
                    throw new InvalidDataException("此玩家使用默认皮肤，没有可下载的自定义贴图");
                return new OnlineSkinEntry
                {
                    Name = Str(session, "name").Length > 0 ?
                        Str(session, "name") : fallbackName,
                    Source = "Mojang 玩家",
                    TextureUrl = CanonicalMojangTexture(Str(skin, "url")),
                    SlimArms = Str(Object(skin, "metadata"), "model")
                        .Equals("slim", StringComparison.OrdinalIgnoreCase),
                    Attribution = "Minecraft 玩家当前公开皮肤 · textures.minecraft.net"
                };
            }
            throw new InvalidDataException("此 Mojang 账号没有可用的皮肤贴图");
        }

        public static async Task<IReadOnlyList<OnlineSkinEntry>> FindPlayerAsync(
            string input, CancellationToken ct)
        {
            string player = (input ?? "").Trim();
            if (!ValidPlayerInput(player))
                throw new ArgumentException("请输入 3～16 位 Minecraft Java 玩家名，或完整 UUID");
            string uuid = player.Replace("-", "");
            if (!IsUuid(uuid))
            {
                Dictionary<string, object> identity = null;
                Exception last = null;
                foreach (var endpoint in new[] {
                    "https://api.minecraftservices.com/minecraft/profile/lookup/name/",
                    "https://api.mojang.com/users/profiles/minecraft/"
                })
                {
                    try
                    {
                        identity = await GetJson(new Uri(endpoint + Uri.EscapeDataString(player)), ct)
                            .ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { last = error; }
                }
                if (identity == null)
                    throw new InvalidOperationException("无法查询玩家；可能不存在或网络不可用：" +
                        last?.Message);
                uuid = Str(identity, "id").Replace("-", "");
                if (!IsUuid(uuid)) throw new InvalidDataException("玩家 UUID 格式无效");
                string canonical = Str(identity, "name");
                if (!string.IsNullOrEmpty(canonical)) player = canonical;
            }
            var profile = await GetJson(
                new Uri("https://sessionserver.mojang.com/session/minecraft/profile/" + uuid),
                ct).ConfigureAwait(false);
            return new[] { ParseSessionProfile(profile, player) };
        }

        public static async Task<IReadOnlyList<OnlineSkinEntry>> SearchCommunityAsync(
            string text, CancellationToken ct)
        {
            string query = (text ?? "").Trim();
            if (query.Length < 2 || query.Length > 60)
                throw new ArgumentException("请输入 2～60 个字符搜索社区公开皮肤");
            var root = await GetJson(new Uri("https://dpplugins.com/api/skins?search=" +
                Uri.EscapeDataString(query)), ct).ConfigureAwait(false);
            if (!root.TryGetValue("skins", out var value) || !(value is object[] skins))
                throw new InvalidDataException("社区皮肤搜索结果结构已变化");
            var result = new List<OnlineSkinEntry>();
            foreach (var skin in skins.Select(Dict).Where(x => x != null).Take(40))
            {
                string url = Str(skin, "skinUrl");
                string name = Str(skin, "name");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;
                try
                {
                    string owner = Str(Object(skin, "owner"), "username");
                    string license = Str(skin, "license");
                    var normalized = SafePublicSkinUri(url).AbsoluteUri;
                    result.Add(new OnlineSkinEntry
                    {
                        Name = name.Length > 70 ? name.Substring(0, 70) : name,
                        Source = "社区皮肤",
                        TextureUrl = normalized,
                        SlimArms = Str(skin, "model").Equals(
                            "slim", StringComparison.OrdinalIgnoreCase),
                        Attribution = "来源：DP-Plugins" +
                            (owner.Length > 0 ? " · 作者：" + owner : "") +
                            (license.Length > 0 ? " · 许可：" + license : "")
                    });
                }
                catch (InvalidDataException)
                {
                    // Skip malformed or non-HTTPS third-party assets.
                }
            }
            return result;
        }

        public static async Task<byte[]> DownloadPngAsync(
            OnlineSkinEntry skin, CancellationToken ct)
        {
            if (skin == null) throw new ArgumentNullException(nameof(skin));
            var uri = SafePublicSkinUri(skin.TextureUrl);
            byte[] png = await GetBytes(uri, MaxPngBytes, ct).ConfigureAwait(false);
            VerifyPng(png);
            return png;
        }

        internal static void VerifyPng(byte[] data)
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            if (data == null || data.Length < 24 || data.Length > MaxPngBytes ||
                !signature.SequenceEqual(data.Take(8)))
                throw new InvalidDataException("下载的文件不是有效的 Minecraft 皮肤 PNG");
            using (var stream = new MemoryStream(data, false))
            {
                var decoder = new PngBitmapDecoder(stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count < 1 ||
                    decoder.Frames[0].PixelWidth != 64 ||
                    (decoder.Frames[0].PixelHeight != 64 &&
                     decoder.Frames[0].PixelHeight != 32))
                    throw new InvalidDataException("皮肤图片必须为 64×64 或 64×32 PNG");
            }
        }

        internal static void RunOfflineSelfTest()
        {
            void Require(bool value, string why)
            {
                if (!value) throw new InvalidOperationException("在线皮肤离线自检失败：" + why);
            }
            Require(ValidPlayerInput("jeb_") && ValidPlayerInput(
                "853c80ef3c3749fdaa49938b674adae6"), "玩家名和 UUID");
            Require(!ValidPlayerInput("../secret") && !ValidPlayerInput("bad?"),
                "拒绝非法玩家名");
            const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            Require(CanonicalMojangTexture("http://textures.minecraft.net/texture/" +
                hash) == "https://textures.minecraft.net/texture/" + hash,
                "只升级官方皮肤 URL");
            try { CanonicalMojangTexture("http://localhost/texture/" + hash); throw new Exception(); }
            catch (InvalidDataException) { }
            var textures = "{\"textures\":{\"SKIN\":{\"url\":\"http://textures.minecraft.net/texture/" +
                hash + "\",\"metadata\":{\"model\":\"slim\"}}}}";
            var profile = new Dictionary<string, object>
            {
                ["name"] = "jeb_",
                ["properties"] = new object[] {
                    new Dictionary<string, object> {
                        ["name"] = "textures",
                        ["value"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(textures))
                    }
                }
            };
            var found = ParseSessionProfile(profile, "fallback");
            Require(found.SlimArms && found.Name == "jeb_" &&
                found.TextureUrl.StartsWith("https://textures.minecraft.net/texture/"),
                "离线解析 Mojang textures 与 Alex 模型");
            VerifyPng(new SkinEditorDocument().ExportPngBytes());
        }
    }
}
