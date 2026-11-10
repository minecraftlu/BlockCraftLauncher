using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media;
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
    internal sealed class OnlineSkinEntry : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public string TextureUrl { get; set; }
        public string ThumbnailUrl { get; set; }
        public string DetailUrl { get; set; }
        public string Author { get; set; }
        public string License { get; set; }
        public string Category { get; set; }
        public string TagsLabel { get; set; }
        public long Downloads { get; set; }
        public bool SlimArms { get; set; }
        public string Attribution { get; set; }

        ImageSource thumbnail;
        public ImageSource Thumbnail
        {
            get => thumbnail;
            set
            {
                if (ReferenceEquals(thumbnail, value)) return;
                thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;

        public bool OpenLicense => OnlineSkinSearchService.IsFreeLicense(License);
        // Downloadability means that a provider serves a public PNG; it does
        // NOT assert a copyright licence, upload permission or redistribution.
        public bool CanDownload => !string.IsNullOrWhiteSpace(TextureUrl) &&
            (Source == "Mosaicary" || Source == "DP-Plugins" ||
             Source == "Mojang 玩家");
        public bool RequiresLicenseNotice => CanDownload && !OpenLicense;
        public string LicenseLabel => OpenLicense ? "自由许可：" + License :
            Source == "Mojang 玩家" ? "公开玩家皮肤 · 授权未知" :
            string.IsNullOrWhiteSpace(License) ? "许可未声明 · 下载不代表获授权" :
            "非自由许可：" + License + " · 使用受限制";
        public string ModelLabel => SlimArms ? "Alex · 细手臂" : "Steve · 经典手臂";
        public string AuthorLabel => string.IsNullOrWhiteSpace(Author) ?
            "作者未知" : "作者：" + Author;
        public string Summary => string.IsNullOrWhiteSpace(Description) ?
            "公开 Minecraft Java 皮肤" : Description;
        public string DownloadsLabel => Downloads <= 0 ? "" :
            "下载 " + Downloads.ToString("N0", CultureInfo.InvariantCulture);
        public override string ToString() =>
            Name + " · " + Source + " · " + ModelLabel;
    }

    internal sealed class OnlineSkinPage
    {
        public IReadOnlyList<OnlineSkinEntry> Entries { get; set; }
        public int Page { get; set; }
        public int Total { get; set; }
        public int TotalPages { get; set; }
    }

    internal static class OnlineSkinSearchService
    {
        const int MaxJsonBytes = 1024 * 1024;
        const int MaxPngBytes = 4 * 1024 * 1024;
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = MaxJsonBytes
        };
        static readonly HttpClient Http = CreateHttp();
        // 24 thumbnails per page should not become 24 simultaneous requests
        // to the same gallery. Limit connections and reuse recent previews.
        static readonly SemaphoreSlim ThumbnailSlots = new SemaphoreSlim(8, 8);
        const int ThumbnailCacheLimit = 80;
        const int ThumbnailCacheMaxBytes = 256 * 1024;
        static readonly ConcurrentDictionary<string, CachedThumbnail> ThumbnailCache =
            new ConcurrentDictionary<string, CachedThumbnail>(StringComparer.Ordinal);
        sealed class CachedThumbnail
        {
            internal byte[] Content;
            internal DateTime ExpiresUtc;
        }

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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher/0.9.36");
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

        static async Task<byte[]> GetBytes(Uri url, int maximum, CancellationToken ct,
            IProgress<DownloadBytes> progress = null)
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
                        progress?.Report(new DownloadBytes(buffer.Length,
                            response.Content.Headers.ContentLength ?? 0));
                    }
                    return buffer.ToArray();
                }
            }
        }

        static Dictionary<string, object> ParseJson(string body)
        {
            try
            {
                // Separate serializer per concurrent request; the shared
                // instance is not used on multiple network worker threads.
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes };
                return Dict(serializer.DeserializeObject(body)) ??
                    throw new InvalidDataException("皮肤服务返回的 JSON 结构无效");
            }
            catch (ArgumentException error)
            {
                throw new InvalidDataException("皮肤服务没有返回有效的 JSON", error);
            }
        }

        static async Task<Dictionary<string, object>> GetJson(Uri uri, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string key = PublicSearchCache.CacheKey(uri.AbsoluteUri, "skin", "", false);
            if (PublicSearchCache.TryGet(key, out var cached))
            {
                try { return ParseJson(cached); }
                catch (InvalidDataException) { PublicSearchCache.Remove(key); }
            }
            var bytes = await GetBytes(uri, MaxJsonBytes, ct).ConfigureAwait(false);
            var body = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            var result = ParseJson(body);
            PublicSearchCache.Store(key, body);
            return result;
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
                    Author = fallbackName,
                    License = "",
                    Description = "该玩家当前公开的 Minecraft Java 皮肤；未声明自由使用许可",
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


        // 'Published' and 'open source' are not synonyms: CC-NC, CC-ND,
        // 'all rights reserved', and missing permissions do not qualify.
        internal static bool IsFreeLicense(string license)
        {
            string value = (license ?? "").Trim().ToLowerInvariant();
            return new[] { "cc0", "cc0-1.0", "cc-by", "cc-by-3.0", "cc-by-4.0",
                "cc-by-sa", "cc-by-sa-3.0", "cc-by-sa-4.0",
                "mit", "apache-2.0", "gpl-3.0", "gpl-3.0-only" }
                .Contains(value);
        }

        static int Number(Dictionary<string, object> obj, string key, int fallback = 0)
        {
            return int.TryParse(Str(obj, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var result) ? result : fallback;
        }

        static string Short(string value, int max)
        {
            value = (value ?? "").Trim();
            return value.Length <= max ? value : value.Substring(0, max);
        }

        static string JoinTags(Dictionary<string, object> obj)
        {
            if (obj == null || !obj.TryGetValue("tags", out var raw) ||
                !(raw is object[] tags)) return "";
            return Short(string.Join(" · ", tags.OfType<string>().Take(6)), 110);
        }

        /// <summary>
        /// Mosaicary is a browsable, paginated, public skin gallery. We
        /// restrict to PUBLISHED standard 64px skins, never harvested players.
        /// Each result includes an explicit per-skin license and author.
        /// </summary>
        internal static OnlineSkinPage ParseMosaicaryPage(
            Dictionary<string, object> data, int requestedPage, bool freeOnly,
            bool includeFound = false)
        {
            if (data == null || !data.TryGetValue("skins", out var raw) ||
                !(raw is object[] skins))
                throw new InvalidDataException("Mosaicary 皮肤目录数据结构错误");
            int page = Math.Max(1, Math.Min(10000, Number(data, "page", requestedPage)));
            int pages = Math.Max(page, Math.Min(10000, Number(data, "pages", page)));
            var entries = new List<OnlineSkinEntry>();
            foreach (var model in skins.Select(Dict).Where(x => x != null).Take(50))
            {
                string id = Str(model, "id"), name = Short(Str(model, "name"), 80);
                if (!Regex.IsMatch(id, @"^[a-zA-Z0-9]{12}$") ||
                    name.Length == 0 || Number(model, "width") != 64 ||
                    (Number(model, "height") != 64 && Number(model, "height") != 32) ||
                    Str(model, "png").Length == 0) continue;
                // Free-only views filter by licence. Other PUBLIC views can
                // show restricted entries; the UI warns before manual downloads.
                string license = Short(Str(model, "license"), 48);
                if (freeOnly && !IsFreeLicense(license)) continue;
                bool foundSkin = model.TryGetValue("found", out var found) &&
                    found is bool isFound && isFound;
                // Found player skins are opt-in and must never be labelled
                // open-licence. Individual downloads require a rights notice.
                if (foundSkin && !includeFound) continue;
                string author = foundSkin ? "原作者未知" :
                    Short(Str(Object(model, "author"), "name"), 70);
                if (author.Length == 0 && !foundSkin)
                    author = Short(Str(Object(model, "publisher"), "name"), 70);
                if (author.Length == 0) author = "未署名";
                string detail = "https://mosaicary.com/models/" + id;
                entries.Add(new OnlineSkinEntry
                {
                    Id = id, Name = name, Source = "Mosaicary",
                    Author = author, License = foundSkin ? "" : license,
                    Description = Short(Str(model, "category") +
                        (JoinTags(model).Length > 0 ? " · " + JoinTags(model) : ""), 130),
                    Category = Short(Str(model, "category"), 30),
                    TagsLabel = JoinTags(model),
                    TextureUrl = "https://mosaicary.com/api/skins/" + id + ".png",
                    ThumbnailUrl = "https://mosaicary.com/api/skins/" + id + "/render.png",
                    DetailUrl = detail,
                    Downloads = Math.Max(0, Number(model, "downloads")),
                    SlimArms = Str(model, "arms") == "slim",
                    Attribution = "来源：Mosaicary · 作者：" + author +
                        " · 许可证：" + (foundSkin ? "未声明（非自由许可）" :
                            (license.Length > 0 ? license : "未声明")) +
                        " · 详情：" + detail
                });
            }
            return new OnlineSkinPage
            {
                Entries = entries,
                Page = page,
                Total = Math.Max(0, Number(data, "total")),
                TotalPages = pages
            };
        }

        public static async Task<OnlineSkinPage> BrowseMosaicaryAsync(
            string text, int page, string category, string arms,
            string sort, bool freeOnly, CancellationToken ct,
            bool includeFound = false)
        {
            string query = (text ?? "").Trim();
            if (query.Length > 60) throw new ArgumentException("搜索关键词不得超过 60 个字符");
            if (page < 1 || page > 10000)
                throw new ArgumentOutOfRangeException(nameof(page));
            var args = new List<string> {
                includeFound ? "source=all" : "source=published", "size=64", "per=24",
                "page=" + page.ToString(CultureInfo.InvariantCulture) };
            if (query.Length > 0) args.Add("q=" + Uri.EscapeDataString(query));
            if (new[] { "girls", "boys", "characters", "creatures" }.Contains(category))
                args.Add("category=" + category);
            if (arms == "slim" || arms == "classic") args.Add("arms=" + arms);
            if (sort == "popular" || sort == "downloads") args.Add("sort=" + sort);
            return ParseMosaicaryPage(await GetJson(new Uri(
                "https://mosaicary.com/api/skins?" + string.Join("&", args)), ct)
                .ConfigureAwait(false), page, freeOnly, includeFound);
        }

        internal static async Task<byte[]> DownloadThumbnailAsync(
            OnlineSkinEntry item, CancellationToken ct)
        {
            if (item == null || item.Source != "Mosaicary" ||
                !Regex.IsMatch(item.Id ?? "", @"^[a-zA-Z0-9]{12}$"))
                return null;
            // The fixed host and ID prohibit a catalog from injecting a
            // third-party BitmapImage URL; HTTP never follows redirects.
            ct.ThrowIfCancellationRequested();
            if (ThumbnailCache.TryGetValue(item.Id, out var cached) &&
                cached.ExpiresUtc > DateTime.UtcNow)
                return cached.Content;
            await ThumbnailSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another thumbnail request may have populated the cache
                // while waiting for a network slot.
                ct.ThrowIfCancellationRequested();
                if (ThumbnailCache.TryGetValue(item.Id, out cached) &&
                    cached.ExpiresUtc > DateTime.UtcNow)
                    return cached.Content;
                var url = new Uri("https://mosaicary.com/api/skins/" + item.Id + "/render.png");
                var bytes = await GetBytes(url, 1024 * 1024, ct).ConfigureAwait(false);
                if (bytes.Length > 8 && bytes.Length <= ThumbnailCacheMaxBytes &&
                    bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 &&
                    bytes[3] == 71 && bytes[4] == 13 && bytes[5] == 10 &&
                    bytes[6] == 26 && bytes[7] == 10)
                {
                    if (ThumbnailCache.Count >= ThumbnailCacheLimit)
                    {
                        foreach (var entry in ThumbnailCache
                            .OrderBy(x => x.Value.ExpiresUtc).Take(ThumbnailCacheLimit / 4))
                            ThumbnailCache.TryRemove(entry.Key, out _);
                    }
                    ThumbnailCache[item.Id] = new CachedThumbnail
                    {
                        Content = bytes,
                        ExpiresUtc = DateTime.UtcNow.AddMinutes(8)
                    };
                }
                return bytes;
            }
            finally { ThumbnailSlots.Release(); }
        }

        // Run on the WPF UI dispatcher rather than decoding a WPF bitmap on a
        // pooled MTA worker. Never use Source=third-party-URL in XAML.
        internal static ImageSource DecodeThumbnail(byte[] bytes)
        {
            if (bytes == null) return null;
            using (var stream = new MemoryStream(bytes, false))
            {
                var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0 || decoder.Frames[0].PixelWidth < 1 ||
                    decoder.Frames[0].PixelHeight < 1 ||
                    decoder.Frames[0].PixelWidth > 512 || decoder.Frames[0].PixelHeight > 512)
                    throw new InvalidDataException("皮肤缩略图超出允许尺寸");
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
        }

        static string LicenseLink(string license)
        {
            var value = (license ?? "").Trim().ToLowerInvariant();
            if (value == "cc0" || value == "cc0-1.0")
                return "https://creativecommons.org/publicdomain/zero/1.0/";
            if (value.StartsWith("cc-by-sa"))
                return "https://creativecommons.org/licenses/by-sa/" +
                    (value.EndsWith("3.0") ? "3.0" : "4.0") + "/";
            if (value.StartsWith("cc-by"))
                return "https://creativecommons.org/licenses/by/" +
                    (value.EndsWith("3.0") ? "3.0" : "4.0") + "/";
            if (value == "mit")
                return "https://opensource.org/license/mit";
            if (value == "apache-2.0")
                return "https://www.apache.org/licenses/LICENSE-2.0";
            return "";
        }

        internal static string SkinSourceNote(OnlineSkinEntry item)
        {
            if (item == null) return "";
            return "Minecraft skin attribution\n" +
                "Name: " + Short(item.Name, 80) + "\n" +
                "Author: " + Short(item.Author, 70) + "\n" +
                "Source: " + Short(item.Source, 40) + "\n" +
                "License: " + (string.IsNullOrWhiteSpace(item.License) ?
                    "Unknown / not stated" : Short(item.License, 48)) + "\n" +
                "Permissions: " + (item.OpenLicense ? "See listed licence" :
                    "Not verified; saving does not grant reuse or redistribution rights") + "\n" +
                "License link: " + LicenseLink(item.License) + "\n" +
                "Original page: " + Short(item.DetailUrl, 512) + "\n" +
                "Creative Commons licenses may require credit and share-alike on redistribution.\n";
        }

        public static async Task<IReadOnlyList<OnlineSkinEntry>> SearchCommunityAsync(
            string text, CancellationToken ct)
        {
            string query = (text ?? "").Trim();
            if (query.Length > 60)
                throw new ArgumentException("社区皮肤关键词不得超过 60 个字符");
            var root = await GetJson(new Uri("https://dpplugins.com/api/skins?search=" +
                Uri.EscapeDataString(query)), ct).ConfigureAwait(false);
            if (!root.TryGetValue("skins", out var value) || !(value is object[] skins))
                throw new InvalidDataException("社区皮肤搜索结果结构已变化");
            var result = new List<OnlineSkinEntry>();
            foreach (var skin in skins.Select(Dict).Where(x => x != null).Take(200))
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
                    string slug = Short(Str(skin, "slug"), 100);
                    string page = Regex.IsMatch(slug, @"^[a-zA-Z0-9_-]{1,100}$") ?
                        "https://dpplugins.com/skins/" + slug : "https://dpplugins.com";
                    result.Add(new OnlineSkinEntry
                    {
                        Name = Short(name, 70),
                        Description = Short(Str(skin, "description"), 130),
                        Source = "DP-Plugins",
                        Author = Short(owner, 70),
                        License = Short(license, 48),
                        Category = "community",
                        TagsLabel = JoinTags(skin),
                        DetailUrl = page,
                        TextureUrl = normalized,
                        SlimArms = Str(skin, "model").Equals(
                            "slim", StringComparison.OrdinalIgnoreCase),
                        Attribution = "来源：DP-Plugins" +
                            (owner.Length > 0 ? " · 作者：" + owner : "") +
                            " · 许可：" + (license.Length > 0 ? license : "未声明") +
                            " · 详情：" + page
                    });
                }
                catch (InvalidDataException)
                {
                    // Skip malformed or non-HTTPS third-party assets.
                }
            }
            return result;
        }

        // Listing thumbnails do not trigger skin-file downloads. For the
        // selected preview, Mosaicary requests ?for=view; the explicit Save
        // and Use actions call DownloadPngAsync without that suffix.
        public static async Task<byte[]> PreviewPngAsync(
            OnlineSkinEntry skin, CancellationToken ct)
        {
            if (skin == null) throw new ArgumentNullException(nameof(skin));
            if (skin.Source == "Mosaicary" &&
                Regex.IsMatch(skin.Id ?? "", @"^[a-zA-Z0-9]{12}$"))
            {
                byte[] preview = await GetBytes(new Uri(
                    "https://mosaicary.com/api/skins/" + skin.Id + ".png?for=view"),
                    MaxPngBytes, ct).ConfigureAwait(false);
                VerifyPng(preview);
                return preview;
            }
            // A selected public community entry may be previewed without
            // implying a licence or automatically saving a copy.
            var previewUrl = SafePublicSkinUri(skin.TextureUrl);
            byte[] raw = await GetBytes(previewUrl, MaxPngBytes, ct).ConfigureAwait(false);
            VerifyPng(raw);
            return raw;
        }

        public static async Task<byte[]> DownloadPngAsync(
            OnlineSkinEntry skin, CancellationToken ct,
            IProgress<DownloadBytes> progress = null)
        {
            if (skin == null) throw new ArgumentNullException(nameof(skin));
            if (!skin.CanDownload)
                throw new InvalidOperationException("此来源没有公开可访问的皮肤 PNG");
            var uri = SafePublicSkinUri(skin.TextureUrl);
            byte[] png = await GetBytes(uri, MaxPngBytes, ct, progress).ConfigureAwait(false);
            VerifyPng(png);
            return png;
        }

        /// <summary>
        /// Shared skin-download task target: isolated staging, normalized 64x64
        /// PNG, unique destination (never overwrite an existing skin),
        /// attribution sidecar, cancellation and full retry through the
        /// launcher's existing DownloadTaskCenter.
        /// </summary>
        public static async Task<string> DownloadToLibraryAsync(
            OnlineSkinEntry skin, CancellationToken ct,
            IProgress<DownloadBytes> progress = null)
        {
            if (skin == null || !skin.CanDownload)
                throw new InvalidOperationException("没有受支持的公开 PNG 下载地址");
            byte[] png = await DownloadPngAsync(skin, ct, progress).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            string directory = SkinEditorDocument.LocalLibrary;
            Directory.CreateDirectory(directory);
            string slug = Regex.Replace(skin.Name ?? "", @"[\\/:*?""<>|\p{C}]", "-")
                .Trim().TrimEnd('.', ' ');
            if (slug.Length > 25) slug = slug.Substring(0, 25);
            if (slug.Length == 0) slug = "Minecraft";
            string stage = Path.Combine(directory, ".skin-" +
                Guid.NewGuid().ToString("N") + ".png");
            string stagingInput = Path.Combine(Path.GetTempPath(),
                "BlockCraft-Skin-Import-" + Guid.NewGuid().ToString("N") + ".png");
            string noteStage = stage + ".source.txt";
            string committed = null;
            try
            {
                File.WriteAllBytes(stagingInput, png);
                var normalized = new SkinEditorDocument();
                normalized.LoadPng(stagingInput, skin.SlimArms);
                normalized.SavePng(stage);
                File.WriteAllText(noteStage, SkinSourceNote(skin), Encoding.UTF8);
                ct.ThrowIfCancellationRequested();
                // File.Move is non-overwriting, preventing race conditions and
                // accidental user data loss even with parallel/retried jobs.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = "在线-" + slug + "-" +
                        DateTime.UtcNow.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture) +
                        "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    string target = SkinEditorDocument.SafeLibraryPath(name);
                    try
                    {
                        File.Move(stage, target);
                        committed = target;
                        break;
                    }
                    catch (IOException) when (File.Exists(target))
                    {
                        // A simultaneous download won that exact name.
                    }
                }
                if (committed == null)
                    throw new IOException("无法取得唯一的本地皮肤文件名");
                ct.ThrowIfCancellationRequested();
                File.Move(noteStage, committed + ".source.txt");
                return committed;
            }
            catch
            {
                if (committed != null)
                {
                    try { File.Delete(committed); } catch (IOException) { }
                }
                throw;
            }
            finally
            {
                foreach (string temporary in new[] { stage, noteStage, stagingInput })
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (IOException) { }
                }
            }
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
            Require(IsFreeLicense("cc0") && IsFreeLicense("cc-by") &&
                IsFreeLicense("cc-by-sa-4.0") && !IsFreeLicense("cc-by-nc") &&
                !IsFreeLicense("arr") && !IsFreeLicense(null),
                "禁止将未授权和非商业许可皮肤伪装为开源");
            var sample = new Dictionary<string, object> {
                ["page"] = 1, ["pages"] = 3, ["total"] = 3,
                ["skins"] = new object[] {
                    new Dictionary<string, object> {
                        ["id"] = "ABCDef012345", ["name"] = "Open hood",
                        ["width"] = 64, ["height"] = 64, ["png"] = "https://mosaicary.com/",
                        ["license"] = "cc-by", ["arms"] = "slim",
                        ["publisher"] = new Dictionary<string, object> { ["name"] = "Artist" }
                    },
                    new Dictionary<string, object> {
                        ["id"] = "ABCDef012346", ["name"] = "Restricted",
                        ["width"] = 64, ["height"] = 64, ["png"] = "https://mosaicary.com/",
                        ["license"] = "arr"
                    },
                    new Dictionary<string, object> {
                        ["id"] = "ABCDef012347", ["name"] = "Found player",
                        ["width"] = 64, ["height"] = 64, ["png"] = "https://mosaicary.com/",
                        ["found"] = true
                    }
                }
            };
            var catalog = ParseMosaicaryPage(sample, 1, true);
            Require(catalog.TotalPages == 3 && catalog.Entries.Count == 1 &&
                catalog.Entries[0].OpenLicense && catalog.Entries[0].SlimArms &&
                catalog.Entries[0].TextureUrl.Contains("/ABCDef012345.png"),
                "Mosaicary 分页、许可过滤、可信主机、模型及作者");
            var publicCatalog = ParseMosaicaryPage(sample, 1, false, true);
            Require(publicCatalog.Entries.Count == 3 &&
                publicCatalog.Entries.Any(x => x.Name == "Found player" &&
                    !x.OpenLicense && x.CanDownload && x.RequiresLicenseNotice) &&
                publicCatalog.Entries.Any(x => x.Name == "Restricted" &&
                    !x.OpenLicense && x.RequiresLicenseNotice) &&
                publicCatalog.Entries.Any(x => x.Name == "Open hood" &&
                    x.OpenLicense && !x.RequiresLicenseNotice),
                "公开 PNG 可单张手动下载，但未授权内容必须标注风险，不能伪装开源");
            Require(!new OnlineSkinEntry { Source = "Mosaicary",
                TextureUrl = "" }.CanDownload &&
                !new OnlineSkinEntry { Source = "任意来源",
                TextureUrl = "https://example.org/skin.png" }.CanDownload,
                "无公开来源或无图片地址必须禁用下载");
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
