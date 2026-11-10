using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>A directory listing is not an endorsement or a verified live ping.</summary>
    internal sealed class PublicServerHit
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Source { get; set; }
        public string SourceUrl { get; set; }
        public string Tags { get; set; }
        public int Players { get; set; }
        public int MaxPlayers { get; set; }
        public bool? Online { get; set; }
        public string VersionLabel => "版本：" + (string.IsNullOrWhiteSpace(Version) ? "未知" : Version);
        public string PlayerLabel => "玩家：" + Players.ToString("N0", CultureInfo.InvariantCulture) +
            (MaxPlayers > 0 ? " / " + MaxPlayers.ToString("N0", CultureInfo.InvariantCulture) : "");
        public string StatusLabel => Online == true ? "在线（目录缓存）" :
            Online == false ? "离线（目录缓存）" : "状态未知";
        public string SourceLabel => "来源：" + Source;
        public string Summary => string.IsNullOrWhiteSpace(Description) ?
            "Minecraft Java 服务器" : Description;
        public override string ToString() => Name + " · " + Address;
    }

    internal sealed class PublicServerPage
    {
        public IReadOnlyList<PublicServerHit> Servers { get; set; }
        public int Page { get; set; }
        public int Pages { get; set; }
        public int Total { get; set; }
        public string Warning { get; set; }
    }

    /// <summary>
    /// Opt-in, read-only Minecraft server discovery. Never probes addresses,
    /// scrapes websites, sends Microsoft credentials or attempts to join.
    /// Public directories cache their status and require a modest request rate.
    /// </summary>
    internal static class PublicServerDirectoryService
    {
        const int MaxJsonBytes = 768 * 1024;
        static readonly HttpClient Client = CreateClient();
        static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = MaxJsonBytes };
        static readonly object CacheGate = new object();
        static readonly Dictionary<string, Tuple<DateTime, PublicServerPage>> Cache =
            new Dictionary<string, Tuple<DateTime, PublicServerPage>>(StringComparer.Ordinal);
        static readonly string[] Providers = { "MC Monitor", "minecraftserve.rs", "MSID" };

        static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher/0.9.36");
            return client;
        }

        static Dictionary<string, object> Dict(object value) =>
            value as Dictionary<string, object>;

        static string Str(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out object value) || value == null)
                return "";
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        static Dictionary<string, object> Child(Dictionary<string, object> obj, string key)
        {
            return obj != null && obj.TryGetValue(key, out object val) ? Dict(val) : null;
        }

        static int Number(Dictionary<string, object> obj, string key, int fallback = 0)
        {
            return int.TryParse(Str(obj, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int result) ?
                Math.Max(0, result) : fallback;
        }

        static bool? Boolean(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.TryGetValue(key, out object raw) || raw == null)
                return null;
            if (raw is bool value) return value;
            return bool.TryParse(Convert.ToString(raw), out bool parsed) ?
                (bool?)parsed : null;
        }

        static string Clean(string value, int max)
        {
            var text = Regex.Replace(value ?? "", @"§[0-9a-fk-or]", "",
                RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"[\x00-\x1f\x7f]", " ").Trim();
            return text.Length <= max ? text : text.Substring(0, max);
        }

        static string Tags(Dictionary<string, object> obj)
        {
            if (obj == null || !obj.TryGetValue("tags", out object raw) ||
                !(raw is object[] tags)) return "";
            return Clean(string.Join(" · ", tags.OfType<string>().Take(5)), 110);
        }

        internal static string SafeDirectoryAddress(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
                return null;
            try
            {
                var parsed = MultiplayerService.ParseAddress(host);
                if (host.Contains(":") && !host.StartsWith("[", StringComparison.Ordinal) &&
                    !IPAddress.TryParse(host, out _))
                    port = parsed.Item2;
                string domain = parsed.Item1;
                if (string.Equals(domain, "localhost", StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                    domain.Equals("0", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (IPAddress.TryParse(domain, out IPAddress ip))
                {
                    if (IPAddress.IsLoopback(ip) ||
                        ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast ||
                        ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                        return null;
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var bytes = ip.GetAddressBytes();
                        if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127 ||
                            bytes[0] >= 224 ||
                            (bytes[0] == 169 && bytes[1] == 254) ||
                            (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                            (bytes[0] == 192 && bytes[1] == 168) ||
                            (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127))
                            return null;
                    }
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6 &&
                        (ip.GetAddressBytes()[0] & 0xfe) == 0xfc)
                        return null;
                }
                if (port < 1 || port > 65535) return null;
                var address = domain.Contains(":") ? "[" + domain + "]" : domain;
                return port == 25565 ? address : address + ":" + port;
            }
            catch (ArgumentException) { return null; }
        }

        static PublicServerHit Entry(string source, string name, string host, int port,
            string description, string version, int online, int max, bool? status,
            string tags, string sourceUrl)
        {
            string address = SafeDirectoryAddress(host, port);
            if (address == null) return null;
            return new PublicServerHit
            {
                Name = Clean(name, 90).Length == 0 ? address : Clean(name, 90),
                Address = address,
                Source = source, SourceUrl = sourceUrl,
                Description = Clean(description, 190),
                Version = Clean(version, 52), Tags = Clean(tags, 110),
                Players = Math.Min(10000000, Math.Max(0, online)),
                MaxPlayers = Math.Min(10000000, Math.Max(0, max)),
                Online = status
            };
        }

        internal static PublicServerPage ParseDirectory(string source,
            Dictionary<string, object> root, int requestedPage)
        {
            Dictionary<string, object> data = source == "MSID" ? Child(root, "data") : root;
            if (data == null || !data.TryGetValue("servers", out object raw) ||
                !(raw is object[] servers))
                throw new InvalidDataException(source + " 服务器目录结构发生变化");
            var listed = new List<PublicServerHit>();
            foreach (var item in servers.Select(Dict).Where(x => x != null).Take(50))
            {
                PublicServerHit hit;
                if (source == "MSID")
                {
                    string edition = Str(item, "edition").ToUpperInvariant();
                    if (edition != "JAVA" && edition != "CROSS_PLAY") continue;
                    string slug = Str(item, "slug");
                    hit = Entry(source, Str(item, "name"),
                        Str(item, "address"), Number(item, "port", 25565),
                        Str(item, "description"), Str(item, "version"),
                        Number(item, "currentPlayers"), Number(item, "maxPlayers"),
                        Str(item, "status").Equals("ONLINE", StringComparison.OrdinalIgnoreCase) ?
                            (bool?)true :
                        Str(item, "status").Equals("OFFLINE", StringComparison.OrdinalIgnoreCase) ?
                            (bool?)false : null,
                        Tags(item), "https://minecraftserver.id/en/");
                }
                else if (source == "minecraftserve.rs")
                {
                    if (Str(item, "edition").Equals("bedrock", StringComparison.OrdinalIgnoreCase))
                        continue;
                    hit = Entry(source, Str(item, "name"),
                        Str(item, "host"), Number(item, "port", 25565),
                        Str(item, "tagline"), Str(item, "version"),
                        Number(Child(item, "players"), "online"),
                        Number(Child(item, "players"), "max"),
                        Boolean(item, "online"), Tags(item),
                        "https://minecraftserve.rs/");
                }
                else if (source == "MC Monitor")
                {
                    string tags = Tags(item);
                    if (tags.Split('·').Any(x => x.Trim().Equals("bedrock",
                        StringComparison.OrdinalIgnoreCase)) &&
                        !tags.Contains("crossplay")) continue;
                    hit = Entry(source, Str(item, "name"),
                        Str(item, "ip").Length > 0 ? Str(item, "ip") :
                            Str(item, "identifier"), 25565,
                        Str(item, "description"), Str(item, "version"),
                        Number(item, "players_online"), Number(item, "players_max"),
                        Boolean(item, "online"), tags, "https://www.mc-monitor.app/");
                }
                else throw new ArgumentException("未知的服务器目录");
                if (hit != null) listed.Add(hit);
            }
            int page = Math.Max(1, Number(data, "page", requestedPage));
            int pages = Math.Max(1, Number(data, source == "MSID" ? "totalPages" : "pages", 1));
            return new PublicServerPage
            {
                Page = page, Pages = Math.Min(200, pages),
                Total = Number(data, "total"),
                Servers = listed
                    .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).ToList(),
                Warning = ""
            };
        }

        static async Task<Dictionary<string, object>> GetJsonAsync(Uri url, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await Client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 429)
                    throw new InvalidOperationException("服务器目录请求过于频繁，请至少间隔一分钟重试");
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("服务器目录 HTTP " + (int)response.StatusCode);
                if (response.Content.Headers.ContentLength > MaxJsonBytes)
                    throw new InvalidDataException("服务器目录响应过大");
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int length;
                    while ((length = await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                        .ConfigureAwait(false)) > 0)
                    {
                        if (output.Length + length > MaxJsonBytes)
                            throw new InvalidDataException("服务器目录响应超过大小限制");
                        output.Write(buffer, 0, length);
                    }
                    try
                    {
                        return Dict(Json.DeserializeObject(Encoding.UTF8.GetString(
                            output.ToArray()))) ??
                            throw new InvalidDataException("服务器目录未返回 JSON 对象");
                    }
                    catch (ArgumentException error)
                    {
                        throw new InvalidDataException("服务器目录 JSON 无效", error);
                    }
                }
            }
        }

        static Uri DirectoryUrl(string source, string text, string version,
            string sort, bool onlineOnly, int page)
        {
            var args = new List<string>();
            if (source == "MSID")
            {
                args.Add("edition=JAVA");
                args.Add("limit=20");
                args.Add("page=" + page);
                if (text.Length >= 2) args.Add("q=" + Uri.EscapeDataString(text));
                if (onlineOnly) args.Add("status=ONLINE");
                args.Add("sort=" + (sort == "players" ? "players" :
                    sort == "newest" ? "newest" : "score"));
                return new Uri("https://api.minecraftserver.id/api/v1/public/servers?" +
                    string.Join("&", args));
            }
            if (source == "minecraftserve.rs")
            {
                args.Add("edition=java");
                args.Add("per=20");
                args.Add("page=" + page);
                if (text.Length > 0) args.Add("q=" + Uri.EscapeDataString(text));
                if (version.Length > 0)
                    args.Add("version=" + Uri.EscapeDataString(version));
                args.Add("sort=" + (sort == "players" ? "players" :
                    sort == "newest" ? "new" : "votes"));
                return new Uri("https://minecraftserve.rs/api/servers?" +
                    string.Join("&", args));
            }
            if (source == "MC Monitor")
            {
                args.Add("limit=20");
                args.Add("page=" + page);
                if (text.Length > 0)
                    args.Add("search=" + Uri.EscapeDataString(text));
                if (version.Length > 0)
                    args.Add("version=" + Uri.EscapeDataString(version));
                args.Add("sort=" + (sort == "players" ? "players" :
                    sort == "newest" ? "newest" : "default"));
                return new Uri("https://www.mc-monitor.app/api/servers?" +
                    string.Join("&", args));
            }
            throw new ArgumentException("未知服务器来源");
        }

        static async Task<PublicServerPage> QuerySourceAsync(string source, string text,
            string version, string sort, bool onlineOnly, int page, CancellationToken ct)
        {
            var url = DirectoryUrl(source, text, version, sort, onlineOnly, page);
            var root = await GetJsonAsync(url, ct).ConfigureAwait(false);
            var data = ParseDirectory(source, root, page);
            var list = data.Servers;
            // Some directories do not have an 'onlineOnly' flag or version
            // filter. Apply the remaining restrictions to their returned page.
            if (onlineOnly)
                list = list.Where(x => x.Online == true).ToList();
            if (version.Length > 0)
                list = list.Where(x => x.Version.IndexOf(version,
                    StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            data.Servers = list;
            return data;
        }

        public static async Task<PublicServerPage> SearchAsync(string provider, string query,
            string version, string sort, bool onlineOnly, int page, CancellationToken ct)
        {
            query = (query ?? "").Trim();
            version = (version ?? "").Trim();
            if (query.Length == 1 || query.Length > 80)
                throw new ArgumentException("关键词请留空或输入 2～80 个字符");
            if (version.Length > 18 ||
                (version.Length > 0 &&
                 !Regex.IsMatch(version, @"^[A-Za-z0-9_.-]{1,18}$")))
                throw new ArgumentException("版本筛选格式无效");
            if (page < 1 || page > 200)
                throw new ArgumentOutOfRangeException(nameof(page));
            if (sort != "score" && sort != "players" && sort != "newest")
                throw new ArgumentException("排序方式无效");
            if (provider != "全部来源" && !Providers.Contains(provider))
                throw new ArgumentException("未知服务器目录");
            string key = provider + "|" + query + "|" + version + "|" + sort +
                "|" + onlineOnly + "|" + page;
            lock (CacheGate)
            {
                if (Cache.TryGetValue(key, out var cached) &&
                    DateTime.UtcNow - cached.Item1 < TimeSpan.FromSeconds(60))
                    return cached.Item2;
            }
            var selected = provider == "全部来源" ? Providers : new[] { provider };
            var jobs = selected.Select(async source =>
            {
                try
                {
                    return Tuple.Create(source, await QuerySourceAsync(source,
                        query, version, sort, onlineOnly, page, ct).ConfigureAwait(false),
                        (string)null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return Tuple.Create(source, (PublicServerPage)null,
                        Clean(ex.GetBaseException().Message, 180));
                }
            }).ToArray();
            var responses = await Task.WhenAll(jobs).ConfigureAwait(false);
            var succeeded = responses.Where(x => x.Item2 != null).ToArray();
            if (succeeded.Length == 0)
                throw new InvalidOperationException("服务器目录目前不可访问：" +
                    string.Join("；", responses.Select(x => x.Item1 + " " + x.Item3)));
            var merged = succeeded.SelectMany(x => x.Item2.Servers)
                .GroupBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).Take(60).ToList();
            var failures = responses.Where(x => x.Item3 != null)
                .Select(x => x.Item1 + "：" + x.Item3);
            var output = new PublicServerPage
            {
                Servers = merged,
                Page = page,
                Pages = Math.Min(200, succeeded.Max(x => x.Item2.Pages)),
                Total = succeeded.Sum(x => x.Item2.Total),
                Warning = string.Join("；", failures)
            };
            lock (CacheGate)
            {
                if (Cache.Count > 40) Cache.Clear();
                Cache[key] = Tuple.Create(DateTime.UtcNow, output);
            }
            return output;
        }

        internal static void RunOfflineSelfTest()
        {
            void Require(bool valid, string what)
            {
                if (!valid) throw new InvalidOperationException("服务器搜索自检失败：" + what);
            }
            Require(SafeDirectoryAddress("play.example.com", 25565) == "play.example.com" &&
                SafeDirectoryAddress("play.example.com", 25566) == "play.example.com:25566",
                "地址与端口");
            foreach (var bad in new[] { "127.0.0.1", "192.168.1.1", "10.0.0.2",
                "172.16.0.1", "localhost", "myserver.local", "169.254.0.1",
                "host/../etc/passwd", "http://example.com" })
                Require(SafeDirectoryAddress(bad, 25565) == null, "拒绝私有地址：" + bad);
            var msidServer = new Dictionary<string, object> {
                ["name"] = "Java SMP", ["address"] = "play.example.org",
                ["port"] = 25565, ["edition"] = "JAVA", ["status"] = "ONLINE",
                ["version"] = "1.21.4", ["currentPlayers"] = 12, ["maxPlayers"] = 100
            };
            var bedrockServer = new Dictionary<string, object> {
                ["name"] = "Bedrock only", ["address"] = "bedrock.example.org",
                ["port"] = 19132, ["edition"] = "BEDROCK"
            };
            var msid = new Dictionary<string, object> {
                ["data"] = new Dictionary<string, object> {
                    ["total"] = 2, ["page"] = 1, ["totalPages"] = 4,
                    ["servers"] = new object[] { msidServer, bedrockServer }
                }
            };
            var parsed = ParseDirectory("MSID", msid, 1);
            Require(parsed.Servers.Count == 1 &&
                parsed.Servers[0].Address == "play.example.org" &&
                parsed.Servers[0].Players == 12 && parsed.Pages == 4,
                "MSID Java 过滤、人数和翻页");
            var servers = new Dictionary<string, object> {
                ["page"] = 1, ["pages"] = 2, ["total"] = 1,
                ["servers"] = new object[] {
                    new Dictionary<string, object> {
                        ["name"] = "Survival", ["host"] = "survival.example.org",
                        ["port"] = 25565, ["edition"] = "java",
                        ["players"] = new Dictionary<string, object> {
                            ["online"] = 8, ["max"] = 40
                        }, ["online"] = true
                    }
                }
            };
            Require(ParseDirectory("minecraftserve.rs", servers, 1).Servers.Count == 1,
                "minecraftserve.rs 公开列表");
            var monitor = new Dictionary<string, object> {
                ["page"] = 1, ["pages"] = 1, ["total"] = 1,
                ["servers"] = new object[] {
                    new Dictionary<string, object> {
                        ["name"] = "Creative", ["ip"] = "creative.example.net",
                        ["players_online"] = 27, ["players_max"] = 70,
                        ["online"] = true
                    }
                }
            };
            Require(ParseDirectory("MC Monitor", monitor, 1).Servers[0].Players == 27,
                "MC Monitor 公开目录");
            Require(DirectoryUrl("MC Monitor", "smp", "1.21", "players", true, 2)
                .AbsoluteUri.Contains("page=2"), "搜索参数正确转义");
        }
    }
}
