using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class CurseForgeScreenshot
    {
        public string Title { get; set; }
        public string ThumbnailUrl { get; set; }
        public string ImageUrl { get; set; }
    }

    public sealed class CurseForgeHit
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
        public string IconUrl { get; set; }
        public string AuthorLabel { get; set; }
        public string DownloadsLabel { get; set; }
        public long DownloadCount { get; set; }
        public string CategoryLabel { get; set; }
        public string VersionsLabel { get; set; }
        public string OfficialUrl { get; set; }
        public IReadOnlyList<CurseForgeScreenshot> Screenshots { get; set; } =
            Array.Empty<CurseForgeScreenshot>();
        public override string ToString() => Name + " — " + Summary;
    }

    public sealed class CurseForgeFileOption
    {
        public long FileId { get; set; }
        public string FileName { get; set; }
        public string DisplayName { get; set; }
        public string DateLabel { get; set; }
        public override string ToString() => DisplayName + "  ·  " + DateLabel;
    }

    /// <summary>
    /// Official owner-local CurseForge API when the owner sets a private key.
    /// Optional MCIM remains available only without a configured private key.
    /// NEVER embed, log, cache, mirror or distribute the owner's key.
    /// </summary>
    public sealed class CurseForgeMirrorClient : IDisposable
    {
        const string Api = "https://mod.mcimirror.top/curseforge/v1";
        const int MaxDependencies = 32;
        readonly LauncherConfig config;
        readonly NetworkDownloader net;
        readonly string officialKey;
        readonly HttpClient official;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 };

        // Owner-only configuration. A public EXE cannot protect an embedded
        // developer key: it is intentionally not present in source or builds.
        const string OwnerKeyVariable = "BLOCKCRAFT_CURSEFORGE_API_KEY";
        internal static bool HasLocalOfficialKey => ReadOwnerKey() != null;
        static string ReadOwnerKey()
        {
            string key = Environment.GetEnvironmentVariable(OwnerKeyVariable);
            if (string.IsNullOrWhiteSpace(key))
                key = Environment.GetEnvironmentVariable(
                    OwnerKeyVariable, EnvironmentVariableTarget.User);
            if (string.IsNullOrWhiteSpace(key)) return null;
            key = key.Trim();
            if (key.Length > 512 || key.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidOperationException("本地 CurseForge API Key 格式无效");
            return key;
        }

        // The private header can ONLY go to these exact official endpoints,
        // never to MCIM, Modrinth, arbitrary redirects or image URLs.
        internal static bool MaySendOwnerKey(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                return false;
            return string.Equals(uri.Host, "api.curseforge.com",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "edge.forgecdn.net",
                    StringComparison.OrdinalIgnoreCase);
        }

        public CurseForgeMirrorClient(LauncherConfig settings)
        {
            config = settings ?? throw new ArgumentNullException(nameof(settings));
            net = new NetworkDownloader(settings);
            officialKey = ReadOwnerKey();
            if (officialKey != null)
            {
                official = new HttpClient(
                    NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false))
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };
                official.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-CurseForge/1.0");
            }
        }
        static Dictionary<string, object> Dict(object obj) => obj as Dictionary<string, object>;
        static string Text(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var x) && x != null ? Convert.ToString(x) : "";
        static long Id(IDictionary<string, object> d, string key)
        {
            if (!long.TryParse(Text(d, key), out long n) || n < 1 || n > 999999999999)
                throw new InvalidDataException("无效的 CurseForge 项目或文件 ID");
            return n;
        }
        void RequireEnabled()
        {
            if (officialKey == null && !config.McimEnabled)
                throw new InvalidOperationException(
                    "请在本机配置私有 CurseForge API Key，或在设置中主动启用 MCIM。");
        }

        async Task<object> OfficialGetAsync(string path, CancellationToken ct)
        {
            // Only our own, carefully constructed /mods/* endpoints can
            // receive this key; no HTTP redirect is followed automatically.
            if (path == null || !path.StartsWith("/mods/", StringComparison.Ordinal))
                throw new InvalidOperationException("CurseForge 官方接口路径无效");
            string url = "https://api.curseforge.com/v1" + path;
            if (!MaySendOwnerKey(url))
                throw new InvalidOperationException("拒绝向非官方地址发送 API Key");

            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("x-api-key", officialKey);
                    try
                    {
                        using (var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(
                            official, request, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
                        using (var memory = new MemoryStream())
                        {
                            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                                response.StatusCode == HttpStatusCode.Forbidden)
                                throw new InvalidOperationException(
                                    "CurseForge 官方 API 拒绝本机密钥（401/403）。请检查批准状态或申请更换密钥。");
                            if (!response.IsSuccessStatusCode)
                                throw new IOException("CurseForge 官方 API 暂不可用（HTTP " +
                                    (int)response.StatusCode + "）");
                            await NetworkDownloader.CopyBodyAsync(
                                response, memory, 12 * 1024 * 1024, 0,
                                response.Content.Headers.ContentLength, null,
                                TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                            var envelope = Dict(json.DeserializeObject(
                                Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF')));
                            if (envelope == null || !envelope.TryGetValue("data", out var result))
                                throw new InvalidDataException("CurseForge 官方 API 返回了无效数据");
                            return result;
                        }
                    }
                    catch (IOException) when (attempt == 0)
                    {
                        await Task.Delay(550, ct).ConfigureAwait(false);
                    }
                }
            }
            throw new IOException("CurseForge 官方 API 多次连接失败");
        }

        async Task<object> ApiGetAsync(string path, CancellationToken ct)
        {
            RequireEnabled();
            if (officialKey != null)
                return await OfficialGetAsync(path, ct).ConfigureAwait(false);
            string url = Api + path;
            return await net.GetMetadataWithFallbackAsync(url, "official", body =>
            {
                var envelope = Dict(json.DeserializeObject(body));
                if (envelope == null || !envelope.TryGetValue("data", out var result))
                    throw new InvalidDataException("MCIM 镜像返回了无效数据");
                return result;
            }, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Read-only CurseForge search presentation metadata, compatible with
        /// the official CurseForge Mod schema and optional MCIM responses.
        /// Only HTTPS artwork from known Forge CDN / enabled MCIM hosts is
        /// displayed; never load arbitrary third-party image URLs.
        /// </summary>
        internal static string TrustedArtworkUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
            string host = uri.Host.ToLowerInvariant();
            return host == "media.forgecdn.net" ||
                   host == "mediafilez.forgecdn.net" ||
                   host == "edge.forgecdn.net" ||
                   host == "mod.mcimirror.top"
                ? uri.AbsoluteUri : null;
        }

        internal static string TrustedOfficialProjectUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
                !string.Equals(uri.Host, "www.curseforge.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/minecraft/mc-mods/", StringComparison.OrdinalIgnoreCase))
                return null;
            return uri.AbsoluteUri;
        }

        static string Truncated(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            value = value.Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "…";
        }

        internal static IReadOnlyList<CurseForgeHit> ParseSearch(object data)
        {
            var entries = data as object[];
            if (entries == null || entries.Length > 50)
                throw new InvalidDataException("CurseForge 搜索结果格式或条数异常");

            var hits = new List<CurseForgeHit>();
            foreach (var item in entries)
            {
                var obj = Dict(item);
                if (obj == null) continue;
                var id = Id(obj, "id");
                var logo = Dict(obj.TryGetValue("logo", out var rawLogo) ? rawLogo : null);
                string icon = TrustedArtworkUrl(Text(logo, "thumbnailUrl")) ??
                    TrustedArtworkUrl(Text(logo, "url"));

                var authors = obj.TryGetValue("authors", out var rawAuthors)
                    ? rawAuthors as object[] : null;
                string author = authors == null ? "" :
                    string.Join("、", authors.Select(Dict).Where(x => x != null)
                        .Select(x => Truncated(Text(x, "name"), 44))
                        .Where(x => x.Length > 0).Take(2));
                var categories = obj.TryGetValue("categories", out var rawCategories)
                    ? rawCategories as object[] : null;
                string category = categories == null ? "" :
                    string.Join(" · ", categories.Select(Dict).Where(x => x != null)
                        .Select(x => Truncated(Text(x, "name"), 24))
                        .Where(x => x.Length > 0).Distinct().Take(2));

                string versions = "";
                var indexes = obj.TryGetValue("latestFilesIndexes", out var rawIndexes)
                    ? rawIndexes as object[] : null;
                if (indexes != null)
                {
                    versions = string.Join(" / ", indexes.Select(Dict).Where(x => x != null)
                        .Select(x => Text(x, "gameVersion"))
                        .Where(x => Regex.IsMatch(x, @"^\d+\.\d+(?:\.\d+)?$"))
                        .Distinct().Take(3));
                }
                string downloads = "";
                if (long.TryParse(Text(obj, "downloadCount"), out var count) && count >= 0)
                    downloads = ModrinthSearchMetadata.DownloadCountLabel(count);

                var screenshots = new List<CurseForgeScreenshot>();
                var pictures = obj.TryGetValue("screenshots", out var rawScreens)
                    ? rawScreens as object[] : null;
                if (pictures != null)
                    foreach (var shot in pictures.Select(Dict).Where(x => x != null).Take(12))
                    {
                        string full = TrustedArtworkUrl(Text(shot, "url"));
                        string thumb = TrustedArtworkUrl(Text(shot, "thumbnailUrl")) ?? full;
                        if (full == null || thumb == null) continue;
                        screenshots.Add(new CurseForgeScreenshot
                        {
                            Title = Truncated(Text(shot, "title"), 80),
                            ThumbnailUrl = thumb, ImageUrl = full
                        });
                        if (screenshots.Count == 4) break;
                    }

                var links = Dict(obj.TryGetValue("links", out var rawLinks) ? rawLinks : null);
                hits.Add(new CurseForgeHit
                {
                    Id = id,
                    Name = Truncated(Text(obj, "name"), 120),
                    Summary = Truncated(Text(obj, "summary"), 360),
                    IconUrl = icon,
                    AuthorLabel = string.IsNullOrWhiteSpace(author) ? "作者未提供" : author,
                    DownloadsLabel = string.IsNullOrWhiteSpace(downloads) ? "下载量未知" : downloads,
                    DownloadCount = Math.Max(0, count),
                    CategoryLabel = string.IsNullOrWhiteSpace(category) ? "Minecraft 模组" : category,
                    VersionsLabel = string.IsNullOrWhiteSpace(versions)
                        ? "版本需安装时确认" : "近期版本：" + versions,
                    OfficialUrl = TrustedOfficialProjectUrl(Text(links, "websiteUrl")),
                    Screenshots = screenshots
                });
            }
            return hits;
        }

        // HMCL-inspired server-side filtering. CurseForge modLoaderType is
        // accepted only when gameVersion is also provided, per official API.
        internal static string BuildSearchPath(string query, string gameVersion,
            string loader, int sort, int page)
        {
            query = (query ?? "").Trim();
            gameVersion = (gameVersion ?? "").Trim();
            loader = (loader ?? "").Trim();
            if (query.Length > 150) throw new ArgumentException("搜索关键词过长");
            if (page < 0 || page > 499)
                throw new ArgumentOutOfRangeException(nameof(page), "CurseForge 最多支持前 10000 条搜索结果");
            if (gameVersion.Length > 0 && !Regex.IsMatch(gameVersion, @"^[a-zA-Z0-9._-]{1,64}$"))
                throw new ArgumentException("Minecraft 版本号格式无效");
            if (loader.Length > 0 && gameVersion.Length == 0)
                throw new ArgumentException("按加载器筛选需要先选择 Minecraft 游戏版本");
            if (sort < 0 || sort > 3) throw new ArgumentOutOfRangeException(nameof(sort));
            string path = "/mods/search?gameId=432&classId=6&index=" + (page * 20) +
                "&pageSize=20&searchFilter=" + Uri.EscapeDataString(query);
            if (gameVersion.Length > 0)
            {
                path += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
                if (loader.Length > 0) path += "&modLoaderType=" + LoaderCode(loader);
            }
            // 13 relevance, 6 total downloads, 3 recently updated, 4 name.
            int field = sort == 1 ? 6 : sort == 2 ? 3 : sort == 3 ? 4 : query.Length > 0 ? 13 : 2;
            return path + "&sortField=" + field +
                "&sortOrder=" + (sort == 3 ? "asc" : "desc");
        }

        public Task<IReadOnlyList<CurseForgeHit>> SearchAsync(string query, CancellationToken ct) =>
            SearchAsync(query, "", "", 0, 0, ct);

        public async Task<IReadOnlyList<CurseForgeHit>> SearchAsync(string query,
            string gameVersion, string loader, int sort, int page, CancellationToken ct)
        {
            var entries = await ApiGetAsync(
                BuildSearchPath(query, gameVersion, loader, sort, page), ct);
            return ParseSearch(entries);
        }

        static int LoaderCode(string loader)
        {
            switch ((loader ?? "").ToLowerInvariant())
            {
                case "forge": return 1;
                case "fabric": return 4;
                case "quilt": return 5;
                case "neoforge": return 6;
                default: throw new InvalidOperationException("当前实例未使用受支持的模组加载器");
            }
        }
        static bool Compatible(Dictionary<string, object> file, GameProfile profile)
        {
            if (file.TryGetValue("isAvailable", out var isAvailable) && isAvailable is bool available && !available) return false;
            if (file.TryGetValue("isServerPack", out var isServerPack) && isServerPack is bool server && server) return false;
            if (!Text(file, "fileName").EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) return false;
            if (file.TryGetValue("modLoaderType", out var code) && code != null && Convert.ToInt32(code) != 0
                && Convert.ToInt32(code) != LoaderCode(profile.Loader)) return false;
            var versions = file.TryGetValue("gameVersions", out var raw) ? raw as object[] : null;
            if (versions != null && versions.Length > 0 &&
                !versions.Any(x => Convert.ToString(x) == profile.BaseVersion)) return false;
            return true;
        }

        async Task<object[]> CompatibleFilesRawAsync(long project, GameProfile profile, CancellationToken ct)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (project < 1) throw new ArgumentOutOfRangeException(nameof(project));
            string version = profile.BaseVersion ?? "";
            if (!Regex.IsMatch(version, @"^[A-Za-z0-9._-]{1,64}$"))
                throw new InvalidDataException("Minecraft 版本号无效");
            int loader = LoaderCode(profile.Loader);
            var url = "/mods/" + project + "/files?gameVersion=" + Uri.EscapeDataString(version)
                + "&modLoaderType=" + loader + "&pageSize=50&index=0";
            return await ApiGetAsync(url, ct) as object[] ??
                throw new InvalidDataException("CurseForge 文件列表格式异常");
        }

        internal static IReadOnlyList<CurseForgeFileOption> ParseCompatibleFiles(
            object[] raw, GameProfile profile)
        {
            if (raw == null || raw.Length > 50 || profile == null)
                throw new InvalidDataException("模组版本列表格式异常");
            return raw.Select(Dict).Where(x => x != null && Compatible(x, profile))
                .OrderByDescending(x => Text(x, "fileDate"))
                .Take(40)
                .Select(x => new CurseForgeFileOption
                {
                    FileId = Id(x, "id"),
                    FileName = Text(x, "fileName"),
                    DisplayName = Truncated(Text(x, "displayName"), 95).Length > 0 ?
                        Truncated(Text(x, "displayName"), 95) :
                        Truncated(Text(x, "fileName"), 95),
                    DateLabel = Truncated(Text(x, "fileDate"), 10) + "  ·  " +
                        (Text(x, "releaseType") == "1" ? "正式版" :
                         Text(x, "releaseType") == "2" ? "Beta" : "Alpha")
                }).ToList();
        }

        public async Task<IReadOnlyList<CurseForgeFileOption>> ListCompatibleFilesAsync(
            long project, GameProfile profile, CancellationToken ct) =>
            ParseCompatibleFiles(await CompatibleFilesRawAsync(project, profile, ct), profile);

        async Task<Dictionary<string, object>> CompatibleFileAsync(long project, GameProfile profile, CancellationToken ct)
        {
            var files = await CompatibleFilesRawAsync(project, profile, ct);
            var selected = files.Select(Dict).Where(x => x != null && Compatible(x, profile))
                .OrderByDescending(x => Text(x, "fileDate")).FirstOrDefault();
            if (selected == null) throw new IOException("项目 " + project + " 没有兼容当前版本/加载器的文件");
            return selected;
        }

        sealed class Planned
        {
            public long Project;
            public string Name, Url, Sha1;
            public long FileId;
        }
        static string SafeFilename(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                name.Contains("/") || name.Contains("\\") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name == "." || name == ".." || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CurseForge 返回了无效 JAR 文件名");
            return name;
        }
        static bool AllowedDownload(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                !uri.IsDefaultPort) return false;
            string host = uri.Host.ToLowerInvariant();
            return new[] { "mod.mcimirror.top", "mcim-files.pysio.online",
                "edge.forgecdn.net", "mediafilez.forgecdn.net", "media.forgecdn.net" }.Contains(host);
        }

        // Official CDN downloads require the same owner key (since July 2026).
        // Disable automatic redirects so x-api-key can NEVER be forwarded to
        // a third-party destination. Redirects to approved CDN URLs are
        // followed manually and only the official edge host receives a key.
        async Task DownloadFromOfficialCdnAsync(string url, string path, string sha1,
            CancellationToken ct)
        {
            const long cap = 200L * 1024 * 1024;
            string partial = path + ".blockcraft-official.part";
            Exception last = null;
            try
            {
                for (int attempt = 0; attempt < Math.Min(3, Math.Max(1, config.DownloadRetries)); attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        string current = url;
                        for (int redirects = 0; redirects < 5; redirects++)
                        {
                            if (!AllowedDownload(current))
                                throw new InvalidDataException("CurseForge 文件来源不是允许的官方 CDN");
                            var uri = new Uri(current);
                            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                            {
                                if (string.Equals(uri.Host, "edge.forgecdn.net",
                                    StringComparison.OrdinalIgnoreCase) && MaySendOwnerKey(current))
                                    request.Headers.TryAddWithoutValidation("x-api-key", officialKey);
                                using (var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(
                                    official, request, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
                                {
                                    int status = (int)response.StatusCode;
                                    if (status == 301 || status == 302 || status == 303 ||
                                        status == 307 || status == 308)
                                    {
                                        var next = response.Headers.Location ??
                                            throw new IOException("CurseForge CDN 重定向缺少目标");
                                        current = (next.IsAbsoluteUri ? next :
                                            new Uri(uri, next)).AbsoluteUri;
                                        continue;
                                    }
                                    if (status == 401 || status == 403)
                                        throw new InvalidOperationException(
                                            "CurseForge CDN 拒绝密钥或文件禁止第三方下载（HTTP " +
                                            status + "）");
                                    if (!response.IsSuccessStatusCode)
                                        throw new IOException("CurseForge CDN 下载失败（HTTP " + status + "）");
                                    using (var output = new FileStream(partial, FileMode.Create,
                                        FileAccess.Write, FileShare.None, 128 * 1024, true))
                                        await NetworkDownloader.CopyBodyAsync(response, output,
                                            cap, 0, response.Content.Headers.ContentLength,
                                            null, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                                }
                            }
                            using (var file = File.OpenRead(partial))
                            using (var sha = SHA1.Create())
                            {
                                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                                if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                                    throw new InvalidDataException("CurseForge 官方 CDN 下载 SHA-1 校验失败");
                            }
                            File.Move(partial, path);
                            return;
                        }
                        throw new IOException("CurseForge CDN 重定向次数过多");
                    }
                    catch (IOException ex) when (attempt + 1 < Math.Min(3, Math.Max(1, config.DownloadRetries)))
                    {
                        last = ex;
                        if (File.Exists(partial)) File.Delete(partial);
                        await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
                    }
                }
                throw last ?? new IOException("CurseForge CDN 下载失败");
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }

        async Task Visit(long project, Dictionary<string, object> picked, GameProfile profile,
            HashSet<long> seen, List<Planned> results, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 12 || seen.Count >= MaxDependencies) throw new InvalidDataException("依赖模组太多，拒绝安装");
            if (!seen.Add(project)) return;
            if (!Compatible(picked, profile)) throw new InvalidDataException("模组加载器或游戏版本不兼容");
            if (picked.TryGetValue("dependencies", out var raw) && raw is object[] dependencies)
                foreach (var item in dependencies.Select(Dict).Where(x => x != null))
                {
                    if (Text(item, "relationType") != "3") continue;
                    long dependency = Id(item, "modId");
                    if (!seen.Contains(dependency))
                        await Visit(dependency, await CompatibleFileAsync(dependency, profile, ct),
                            profile, seen, results, depth + 1, ct);
                }
            string filename = SafeFilename(Text(picked, "fileName"));
            string hash = "";
            var hashes = picked.TryGetValue("hashes", out var h) ? h as object[] : null;
            if (hashes != null)
                foreach (var item in hashes.Select(Dict).Where(x => x != null))
                    if (Text(item, "algo") == "1") hash = Text(item, "value");
            if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$"))
                throw new InvalidDataException("该模组缺少可靠的 SHA-1，拒绝自动安装");
            long fileId = Id(picked, "id");
            string direct = null;
            try { direct = Convert.ToString(await ApiGetAsync("/mods/" + project + "/files/" + fileId + "/download-url", ct)); }
            catch (IOException) { /* Mirror may omit per-file download URLs. */ }
            if (!AllowedDownload(direct)) direct = Text(picked, "downloadUrl");
            if (!AllowedDownload(direct))
                throw new IOException("项目 " + project + " 未提供允许的镜像/官方 CDN 下载 URL，请手动下载");
            results.Add(new Planned { Project = project, Name = filename, Url = direct, Sha1 = hash, FileId = fileId });
        }

        public async Task<IReadOnlyList<string>> InstallAsync(long project, GameProfile profile,
            IProgress<string> progress, CancellationToken ct, long? selectedFileId = null)
        {
            RequireEnabled();
            if (profile == null || string.IsNullOrWhiteSpace(profile.Directory))
                throw new InvalidOperationException("请先选择游戏实例");
            Dictionary<string, object> compatible;
            if (selectedFileId.HasValue)
            {
                if (selectedFileId.Value < 1)
                    throw new ArgumentOutOfRangeException(nameof(selectedFileId));
                compatible = Dict(await ApiGetAsync("/mods/" + project + "/files/" +
                    selectedFileId.Value, ct));
                if (compatible == null || Id(compatible, "id") != selectedFileId.Value ||
                    !Compatible(compatible, profile))
                    throw new InvalidDataException("选择的 CurseForge 文件与当前实例不兼容");
            }
            else
                compatible = await CompatibleFileAsync(project, profile, ct);
            var plan = new List<Planned>();
            await Visit(project, compatible, profile, new HashSet<long>(), plan, 0, ct);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan)
                if (!names.Add(item.Name)) throw new IOException("模组依赖有同名文件冲突");
            string baseDir = Path.GetFullPath(profile.Directory);
            string mods = Path.Combine(baseDir, "mods");
            Directory.CreateDirectory(mods);
            string stage = Path.Combine(baseDir, ".blockcraft-cf-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var committed = new List<string>();
            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = plan[i];
                    var dest = Path.Combine(mods, item.Name);
                    if (File.Exists(dest)) throw new IOException("模组文件已存在，拒绝覆盖：" + item.Name);
                    progress?.Report("下载 CurseForge 资源 " + (i + 1) + "/" + plan.Count + "：" + item.Name);
                    string stageFile = Path.Combine(stage, item.Name);
                    if (officialKey != null)
                        await DownloadFromOfficialCdnAsync(item.Url, stageFile, item.Sha1, ct);
                    else
                        await net.DownloadVerifiedAsync(item.Url, stageFile, item.Sha1, ct);
                    if (new FileInfo(stageFile).Length > 200L * 1024 * 1024)
                        throw new InvalidDataException("下载文件超过大小限制");
                    using (var jar = ZipFile.OpenRead(stageFile))
                        if (jar.Entries.Count == 0) throw new InvalidDataException("下载的 JAR 文件无效");
                }
                foreach (var item in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = Path.Combine(mods, item.Name);
                    if (File.Exists(target)) throw new IOException("资源与已有文件冲突：" + item.Name);
                    File.Move(Path.Combine(stage, item.Name), target);
                    committed.Add(target);
                }
                progress?.Report("CurseForge 已安装 " + committed.Count + " 个文件（含必要依赖）");
                return committed;
            }
            catch
            {
                // All files were absent before installation. Remove only new files
                // created by this transaction; never remove existing user files.
                foreach (var path in committed) if (File.Exists(path)) File.Delete(path);
                throw;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }

        public void Dispose()
        {
            official?.Dispose();
            net.Dispose();
        }
    }
}