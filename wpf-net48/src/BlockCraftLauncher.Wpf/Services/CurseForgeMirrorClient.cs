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

    public sealed class CurseForgeHit : ResourceHit
    {
        public long Id { get; set; }
        public string OfficialUrl { get; set; }
        public bool DistributionAllowed { get; set; } = true;
        public IReadOnlyList<CurseForgeScreenshot> Screenshots { get; set; } =
            Array.Empty<CurseForgeScreenshot>();
    }

    internal sealed class CurseForgeCategory
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    internal sealed class CurseForgeDownloadFile : ModDownloadFile
    {
        public IReadOnlyList<long> RequiredProjects { get; set; }
    }

    public sealed class CurseForgeMirrorClient : IDisposable
    {
        const string MirrorApi = "https://mod.mcimirror.top/curseforge/v1";
        const string OfficialApi = "https://api.curseforge.com/v1";
        internal const int SearchPageSize = 20;
        const int FilePageSize = 50;
        const int MaxResults = 10000;
        const int MaxDependencies = 32;
        static readonly SemaphoreSlim[] InstallLocks = Enumerable.Range(0, 64)
            .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        readonly LauncherConfig config;
        readonly bool useMirror;
        readonly NetworkDownloader net;
        readonly string officialKey;
        readonly HttpClient official;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 };

        // Owner-only configuration. A public EXE cannot protect an embedded
        // developer key: it is intentionally not present in source or builds.
        const string OwnerKeyVariable = "BLOCKCRAFT_CURSEFORGE_API_KEY";
        internal static bool HasLocalOfficialKey => LocalKey() != null;
        static string LocalKey()
        {
            string key = Environment.GetEnvironmentVariable(OwnerKeyVariable);
            if (string.IsNullOrWhiteSpace(key))
                key = Environment.GetEnvironmentVariable(
                    OwnerKeyVariable, EnvironmentVariableTarget.User);
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }

        static string ReadOwnerKey()
        {
            string key = LocalKey();
            if (key == null) return null;
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

        public CurseForgeMirrorClient(LauncherConfig settings, bool useMirror = false)
            : this(settings, useMirror, null, null) { }

        internal CurseForgeMirrorClient(LauncherConfig settings, bool useMirror,
            HttpMessageHandler officialHandler, string testKey)
        {
            config = settings ?? throw new ArgumentNullException(nameof(settings));
            this.useMirror = useMirror;
            if (useMirror)
            {
                net = new NetworkDownloader(settings);
                return;
            }
            officialKey = testKey ?? ReadOwnerKey();
            if (officialKey != null)
            {
                official = new HttpClient(officialHandler ??
                    NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false))
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };
                official.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-CurseForge/1.0");
            }
        }

        static Dictionary<string, object> Dict(object obj) => obj as Dictionary<string, object>;
        static object Value(IDictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var value) ? value : null;
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
            if (useMirror && !config.McimEnabled)
                throw new InvalidOperationException("请在下载设置中启用 MCIM。");
            if (!useMirror && officialKey == null)
                throw new InvalidOperationException("请配置本机环境变量 BLOCKCRAFT_CURSEFORGE_API_KEY。");
        }

        static Dictionary<string, object> ParseEnvelope(JavaScriptSerializer serializer, string body)
        {
            var envelope = Dict(serializer.DeserializeObject(body));
            if (envelope == null || !envelope.ContainsKey("data"))
                throw new InvalidDataException("CurseForge 接口返回的数据格式无效。");
            return envelope;
        }

        async Task<Dictionary<string, object>> OfficialRequestAsync(string path, CancellationToken ct,
            long[] modIds = null)
        {
            bool supported = modIds == null
                ? path.StartsWith("/mods/", StringComparison.Ordinal) || path.StartsWith("/categories?", StringComparison.Ordinal)
                : path == "/mods";
            if (!supported)
                throw new InvalidOperationException("CurseForge 官方接口路径无效。");
            string url = OfficialApi + path;
            if (!MaySendOwnerKey(url))
                throw new InvalidOperationException("CurseForge 官方接口地址无效。");

            int attempts = Math.Min(3, Math.Max(1, config.DownloadRetries));
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using (var request = new HttpRequestMessage(modIds == null ? HttpMethod.Get : HttpMethod.Post, url))
                    {
                        request.Headers.TryAddWithoutValidation("x-api-key", officialKey);
                        if (modIds != null)
                            request.Content = new StringContent(json.Serialize(new { modIds }),
                                Encoding.UTF8, "application/json");
                        using (var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(
                            official, request, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
                        using (var memory = new MemoryStream())
                        {
                            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                                response.StatusCode == HttpStatusCode.Forbidden)
                                throw new InvalidOperationException(
                                    "CurseForge 官方 API 拒绝访问（HTTP " + (int)response.StatusCode + "）。请检查密钥和授权。");
                            if (response.StatusCode == HttpStatusCode.NotFound)
                                throw new FileNotFoundException("CurseForge 官方接口未提供该项目或文件。");
                            if (!response.IsSuccessStatusCode)
                                throw new IOException("CurseForge 官方 API 请求失败（HTTP " +
                                    (int)response.StatusCode + "）。");
                            await NetworkDownloader.CopyBodyAsync(response, memory, 12 * 1024 * 1024,
                                0, response.Content.Headers.ContentLength, null,
                                TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                            return ParseEnvelope(json, Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF'));
                        }
                    }
                }
                catch (Exception ex) when (attempt + 1 < attempts && Retryable(ex))
                {
                    await Task.Delay(450 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }

        static bool Retryable(Exception ex) =>
            ex is HttpRequestException || (ex is IOException && !(ex is FileNotFoundException));

        async Task<Dictionary<string, object>> ApiGetAsync(string path, CancellationToken ct)
        {
            RequireEnabled();
            if (!useMirror)
                return await OfficialRequestAsync(path, ct).ConfigureAwait(false);
            return await net.GetMetadataWithFallbackAsync(MirrorApi + path, "official",
                body => ParseEnvelope(json, body), ct).ConfigureAwait(false);
        }

        async Task<object> ApiDataAsync(string path, CancellationToken ct) =>
            Value(await ApiGetAsync(path, ct).ConfigureAwait(false), "data");

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

        internal static IReadOnlyList<CurseForgeHit> ParseSearch(object data, bool useMirror = false)
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
                    ProjectId = id.ToString(),
                    Kind = "mod",
                    Source = useMirror ? "MCIM" : "CurseForge",
                    Name = Truncated(Text(obj, "name"), 120),
                    Description = Truncated(Text(obj, "summary"), 360),
                    IconUrl = icon,
                    Author = string.IsNullOrWhiteSpace(author) ? "作者未提供" : author,
                    DownloadsLabel = string.IsNullOrWhiteSpace(downloads) ? "下载量未知" : downloads,
                    DownloadCount = Math.Max(0, count),
                    CategoryLabel = string.IsNullOrWhiteSpace(category) ? "Minecraft 模组" : category,
                    VersionsLabel = string.IsNullOrWhiteSpace(versions)
                        ? "版本需安装时确认" : "近期版本：" + versions,
                    UpdatedLabel = Truncated(Text(obj, "dateModified"), 10),
                    DistributionAllowed = !Equals(Value(obj, "allowModDistribution"), false),
                    OfficialUrl = TrustedOfficialProjectUrl(Text(links, "websiteUrl")),
                    Screenshots = screenshots
                });
            }
            return hits;
        }

        internal static string BuildSearchPath(string query, string gameVersion,
            string loader, int sort, int page, int category = 0)
        {
            query = (query ?? "").Trim();
            gameVersion = (gameVersion ?? "").Trim();
            loader = (loader ?? "").Trim();
            if (query.Length > 150) throw new ArgumentException("搜索关键词过长");
            if (page < 0 || page >= MaxResults / SearchPageSize)
                throw new ArgumentOutOfRangeException(nameof(page), "CurseForge 最多支持前 10000 条搜索结果");
            if (gameVersion.Length > 0 && !Regex.IsMatch(gameVersion, @"^[a-zA-Z0-9._-]{1,64}$"))
                throw new ArgumentException("Minecraft 版本号格式无效");
            if (loader.Length > 0 && gameVersion.Length == 0)
                throw new ArgumentException("按 CurseForge 加载器筛选时，请选择 Minecraft 版本。");
            if (sort < 0 || sort > 3) throw new ArgumentOutOfRangeException(nameof(sort));
            string path = "/mods/search?gameId=432&classId=6&index=" + (page * SearchPageSize) +
                "&pageSize=" + SearchPageSize + "&searchFilter=" + Uri.EscapeDataString(query);
            if (category > 0) path += "&categoryId=" + category;
            if (gameVersion.Length > 0)
            {
                path += "&gameVersion=" + Uri.EscapeDataString(gameVersion);
                if (loader.Length > 0) path += "&modLoaderType=" + RequireLoader(loader);
            }
            int field = sort == 1 ? 6 : sort == 2 ? 3 : sort == 3 ? 11 : query.Length > 0 ? 1 : 2;
            return path + "&sortField=" + field + "&sortOrder=desc";
        }

        internal async Task<(IReadOnlyList<CurseForgeHit> Hits, bool HasMore)> SearchPageAsync(
            string query, string gameVersion, string loader, int sort, int page, int category,
            CancellationToken ct)
        {
            var envelope = await ApiGetAsync(BuildSearchPath(query, gameVersion, loader,
                sort, page, category), ct).ConfigureAwait(false);
            var hits = ParseSearch(Value(envelope, "data"), useMirror);
            return (hits, HasNextPage(envelope, page, SearchPageSize, hits.Count));
        }

        internal async Task<IReadOnlyList<CurseForgeCategory>> GetCategoriesAsync(CancellationToken ct)
        {
            var rows = await ApiDataAsync("/categories?gameId=432&classId=6", ct).ConfigureAwait(false) as object[];
            if (rows == null) throw new InvalidDataException("CurseForge 分类列表格式无效");
            return rows.OfType<Dictionary<string, object>>()
                .Where(row => !Equals(Value(row, "isClass"), true) && Text(row, "classId") == "6")
                .Select(row => new CurseForgeCategory
                {
                    Id = int.TryParse(Text(row, "id"), out int id) ? id : 0,
                    Name = Truncated(Text(row, "name"), 48)
                })
                .Where(row => row.Id > 0 && row.Name.Length > 0)
                .GroupBy(row => row.Id).Select(group => group.First())
                .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        internal async Task<IReadOnlyDictionary<long, string>> GetDependencyNamesAsync(
            IEnumerable<long> projectIds, CancellationToken ct)
        {
            RequireEnabled();
            if (useMirror) throw new InvalidOperationException("前置项目名称查询需要 CurseForge 官方来源。");
            var ids = projectIds.Where(id => id > 0).Distinct().Take(MaxDependencies).ToArray();
            var names = new Dictionary<long, string>();
            if (ids.Length == 0) return names;
            var response = await OfficialRequestAsync("/mods", ct, ids).ConfigureAwait(false);
            var rows = Value(response, "data") as object[];
            if (rows == null) throw new InvalidDataException("CurseForge 前置项目列表格式无效。");
            foreach (var row in rows.OfType<Dictionary<string, object>>())
            {
                if (!long.TryParse(Text(row, "id"), out long id) || !ids.Contains(id)) continue;
                string name = Truncated(Text(row, "name"), 95);
                if (name.Length > 0) names[id] = name;
            }
            return names;
        }

        static bool HasNextPage(Dictionary<string, object> envelope, int page, int pageSize, int count)
        {
            int nextIndex = (page + 1) * pageSize;
            if (nextIndex >= MaxResults || count == 0) return false;
            var pagination = Dict(Value(envelope, "pagination"));
            return int.TryParse(Text(pagination, "totalCount"), out int total) && total >= count
                ? nextIndex < total : count == pageSize;
        }

        static int LoaderCode(string loader)
        {
            switch (loader?.Trim().ToLowerInvariant())
            {
                case "forge": return 1;
                case "liteloader": return 2;
                case "cauldron": return 3;
                case "fabric": return 4;
                case "quilt": return 5;
                case "neoforge": return 6;
                default: return 0;
            }
        }

        static int RequireLoader(string loader)
        {
            int code = LoaderCode(loader);
            if (code == 0) throw new InvalidOperationException("请选择已安装模组加载器的实例");
            return code;
        }

        static bool IsModJar(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 180 &&
            name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name == Path.GetFileName(name) &&
            name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("-dev.jar", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase);

        static bool IsClientJar(Dictionary<string, object> file) =>
            !Equals(Value(file, "isAvailable"), false) && !Equals(Value(file, "isServerPack"), true) &&
            IsModJar(Text(file, "fileName"));

        static bool Compatible(Dictionary<string, object> file, GameProfile profile)
        {
            if (!IsClientJar(file)) return false;
            if (profile == null) return true;
            var tags = (Value(file, "gameVersions") as object[] ?? Array.Empty<object>())
                .OfType<string>().ToArray();
            if (!string.IsNullOrWhiteSpace(profile.BaseVersion) &&
                !tags.Contains(profile.BaseVersion, StringComparer.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(profile.Loader)) return true;
            int expectedLoader = RequireLoader(profile.Loader);
            if (int.TryParse(Text(file, "modLoaderType"), out int declaredLoader) &&
                declaredLoader != 0 && declaredLoader != expectedLoader) return false;
            var loaders = tags.Select(LoaderCode).Where(code => code > 0).ToArray();
            return loaders.Length == 0 || loaders.Contains(expectedLoader);
        }

        async Task<(object[] Rows, bool HasMore)> CompatibleFilesRawAsync(
            long project, GameProfile profile, int page, CancellationToken ct)
        {
            if (project < 1) throw new ArgumentOutOfRangeException(nameof(project));
            if (page < 0 || page >= MaxResults / FilePageSize) throw new ArgumentOutOfRangeException(nameof(page));
            string version = profile?.BaseVersion;
            var url = "/mods/" + project + "/files?pageSize=" + FilePageSize + "&index=" + (page * FilePageSize);
            if (!string.IsNullOrWhiteSpace(version))
            {
                if (!Regex.IsMatch(version, @"^[A-Za-z0-9._-]{1,64}$"))
                    throw new InvalidDataException("Minecraft 版本号无效");
                url += "&gameVersion=" + Uri.EscapeDataString(version);
                if (!string.IsNullOrWhiteSpace(profile.Loader))
                    url += "&modLoaderType=" + RequireLoader(profile.Loader);
            }
            var envelope = await ApiGetAsync(url, ct).ConfigureAwait(false);
            var rows = Value(envelope, "data") as object[];
            if (rows == null || rows.Length > FilePageSize)
                throw new InvalidDataException("CurseForge 文件列表格式异常");
            return (rows, HasNextPage(envelope, page, FilePageSize, rows.Length));
        }

        static IEnumerable<long> RequiredProjects(Dictionary<string, object> file) =>
            (Value(file, "dependencies") as object[] ?? Array.Empty<object>())
                .OfType<Dictionary<string, object>>()
                .Where(dependency => Text(dependency, "relationType") == "3")
                .Select(dependency => Id(dependency, "modId")).Distinct();

        internal static IReadOnlyList<ModDownloadFile> ParseCompatibleFiles(object[] raw, GameProfile profile)
        {
            if (raw == null || raw.Length > FilePageSize)
                throw new InvalidDataException("模组版本列表格式异常");
            return raw.OfType<Dictionary<string, object>>().Where(file => Compatible(file, profile))
                .OrderByDescending(file => Text(file, "fileDate"))
                .Select(file =>
                {
                    var dependencies = RequiredProjects(file).ToArray();
                    string name = Truncated(Text(file, "displayName"), 95);
                    return new CurseForgeDownloadFile
                    {
                        VersionId = Id(file, "id").ToString(),
                        FileName = Text(file, "fileName"),
                        DisplayName = name.Length > 0 ? name : Truncated(Text(file, "fileName"), 95),
                        Published = Truncated(Text(file, "fileDate"), 10),
                        Release = Text(file, "releaseType") == "1" ? "正式版" :
                            Text(file, "releaseType") == "2" ? "Beta" : "Alpha",
                        RequiredDependencies = dependencies.Length,
                        RequiredProjects = dependencies
                    };
                }).ToArray();
        }

        internal async Task<(IReadOnlyList<ModDownloadFile> Files, bool HasMore)> ListCompatibleFilesAsync(
            long project, GameProfile profile, int page, CancellationToken ct)
        {
            var result = await CompatibleFilesRawAsync(project, profile, page, ct).ConfigureAwait(false);
            return (ParseCompatibleFiles(result.Rows, profile), result.HasMore);
        }

        async Task<Dictionary<string, object>> CompatibleFileAsync(long project, GameProfile profile,
            string installedMods, CancellationToken ct)
        {
            for (int page = 0; ; page++)
            {
                var files = await CompatibleFilesRawAsync(project, profile, page, ct).ConfigureAwait(false);
                var candidates = files.Rows.OfType<Dictionary<string, object>>()
                    .Where(file => Compatible(file, profile))
                    .OrderByDescending(file => Text(file, "fileDate")).ToArray();
                if (candidates.Length > 0)
                {
                    if (installedMods != null)
                        foreach (var candidate in candidates)
                        {
                            ct.ThrowIfCancellationRequested();
                            string path = Path.Combine(installedMods, SafeFilename(Text(candidate, "fileName")));
                            if (File.Exists(path) && MatchesHash(path, FileSha1(candidate))) return candidate;
                        }
                    return candidates[0];
                }
                if (!files.HasMore)
                    throw new IOException("CurseForge 项目 " + project + " 没有兼容当前实例的文件。");
            }
        }

        sealed class Planned
        {
            public string Name, Url, Sha1;
        }

        static string SafeFilename(string name)
        {
            if (!IsModJar(name))
                throw new InvalidDataException("CurseForge 返回了无效的模组文件名。");
            return name;
        }

        internal static bool AllowedDownload(string url, bool allowMirror)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                uri.Fragment.Length > 0 || !uri.IsDefaultPort) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "edge.forgecdn.net" || host == "mediafilez.forgecdn.net" ||
                host == "media.forgecdn.net" ||
                (allowMirror && (host == "mod.mcimirror.top" || host == "mcim-files.pysio.online"));
        }

        static string FileSha1(Dictionary<string, object> file) =>
            (Value(file, "hashes") as object[] ?? Array.Empty<object>())
                .OfType<Dictionary<string, object>>()
                .Where(hash => Text(hash, "algo") == "1")
                .Select(hash => Text(hash, "value")).FirstOrDefault();

        static bool MatchesHash(string path, string expected)
        {
            if (!NetworkDownloader.ValidSha1(expected)) return false;
            using (var file = File.OpenRead(path))
            using (var sha = SHA1.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        async Task DownloadFromOfficialCdnAsync(string url, string destination, string sha1,
            IProgress<DownloadBytes> bytes, CancellationToken ct)
        {
            int attempts = Math.Min(3, Math.Max(1, config.DownloadRetries));
            for (int attempt = 0; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await TransferOfficialFileAsync(new Uri(url), destination, bytes, ct).ConfigureAwait(false);
                    if (!MatchesHash(destination, sha1))
                        throw new IOException("CurseForge 官方 CDN 下载 SHA-1 校验失败。");
                    return;
                }
                catch (Exception ex) when (attempt + 1 < attempts && Retryable(ex))
                {
                    await Task.Delay(400 * (attempt + 1), ct).ConfigureAwait(false);
                }
            }
        }

        async Task TransferOfficialFileAsync(Uri uri, string destination,
            IProgress<DownloadBytes> bytes, CancellationToken ct)
        {
            const long cap = 200L * 1024 * 1024;
            for (int redirects = 0; redirects < 5; redirects++)
            {
                if (!AllowedDownload(uri.AbsoluteUri, allowMirror: false))
                    throw new InvalidDataException("CurseForge 返回了非官方 CDN 地址。");
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    // Redirects are manual so the key is confined to the official edge host.
                    if (string.Equals(uri.Host, "edge.forgecdn.net", StringComparison.OrdinalIgnoreCase))
                        request.Headers.TryAddWithoutValidation("x-api-key", officialKey);
                    using (var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(
                        official, request, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        {
                            var next = response.Headers.Location ??
                                throw new IOException("CurseForge CDN 重定向缺少目标。");
                            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                            continue;
                        }
                        if (status == 401 || status == 403)
                            throw new InvalidOperationException(status == 401
                                ? "CurseForge CDN 拒绝本机 API Key（HTTP 401）。"
                                : "CurseForge CDN 拒绝此文件的第三方下载（HTTP 403）。");
                        if (!response.IsSuccessStatusCode)
                            throw new IOException("CurseForge CDN 下载失败（HTTP " + status + "）。");
                        using (var output = new FileStream(destination, FileMode.Create,
                            FileAccess.Write, FileShare.None, 128 * 1024, true))
                            await NetworkDownloader.CopyBodyAsync(response, output, cap,
                                0, response.Content.Headers.ContentLength, bytes,
                                TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
                        return;
                    }
                }
            }
            throw new IOException("CurseForge CDN 重定向次数过多。");
        }

        async Task<string> ResolveDownloadUrlAsync(long project, Dictionary<string, object> file,
            string filename, CancellationToken ct)
        {
            var details = Dict(await ApiDataAsync("/mods/" + project, ct).ConfigureAwait(false));
            if (details == null || Id(details, "id") != project)
                throw new InvalidDataException("CurseForge 项目信息与所选文件不一致。");
            if (Equals(Value(details, "allowModDistribution"), false))
                throw new InvalidOperationException("作者限制第三方分发：" + Text(details, "name"));

            string direct = Text(file, "downloadUrl");
            if (AllowedDownload(direct, useMirror)) return direct;
            try
            {
                direct = Convert.ToString(await ApiDataAsync("/mods/" + project + "/files/" +
                    Id(file, "id") + "/download-url", ct).ConfigureAwait(false));
            }
            catch (FileNotFoundException) { direct = null; }
            if (!AllowedDownload(direct, useMirror))
                throw new IOException("CurseForge 未提供可直接安装的下载地址：" + filename);
            return direct;
        }

        async Task<Dictionary<string, object>> GetFileAsync(long project, long fileId, CancellationToken ct)
        {
            if (project < 1) throw new ArgumentOutOfRangeException(nameof(project));
            if (fileId < 1) throw new ArgumentOutOfRangeException(nameof(fileId));
            var file = Dict(await ApiDataAsync("/mods/" + project + "/files/" + fileId, ct).ConfigureAwait(false));
            if (file == null || Id(file, "id") != fileId ||
                (Text(file, "modId").Length > 0 && Id(file, "modId") != project))
                throw new InvalidDataException("CurseForge 未返回所选项目的文件。");
            return file;
        }

        async Task DownloadJarAsync(Planned item, string target, IProgress<DownloadBytes> bytes, CancellationToken ct)
        {
            if (useMirror)
                await net.DownloadVerifiedDetailedAsync(item.Url, target, item.Sha1, ct, bytes).ConfigureAwait(false);
            else
                await DownloadFromOfficialCdnAsync(item.Url, target, item.Sha1, bytes, ct).ConfigureAwait(false);
            if (new FileInfo(target).Length > 200L * 1024 * 1024)
                throw new InvalidDataException("下载文件超过大小限制。");
            using (var jar = ZipFile.OpenRead(target))
                if (jar.Entries.Count == 0) throw new InvalidDataException("下载的 JAR 文件无效。");
        }

        internal async Task SaveFileAsync(long project, long fileId, string destination,
            IProgress<DownloadBytes> bytes, CancellationToken ct)
        {
            var file = await GetFileAsync(project, fileId, ct).ConfigureAwait(false);
            if (!IsClientJar(file))
                throw new InvalidDataException("所选文件无法作为模组 JAR 下载。");
            string filename = SafeFilename(Text(file, "fileName"));
            string sha1 = FileSha1(file);
            if (!NetworkDownloader.ValidSha1(sha1))
                throw new InvalidDataException(filename + " 缺少 SHA-1 校验值，无法下载。");
            string url = await ResolveDownloadUrlAsync(project, file, filename, ct).ConfigureAwait(false);
            string target = Path.GetFullPath(destination);
            string staging = Path.Combine(Path.GetDirectoryName(target), ".blockcraft-cf-save-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadJarAsync(new Planned { Name = filename, Url = url, Sha1 = sha1 },
                    staging, bytes, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                NetworkDownloader.CommitVerifiedFile(staging, target);
            }
            finally
            {
                try { File.Delete(staging); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        async Task Visit(long project, Dictionary<string, object> file, GameProfile profile,
            string mods, HashSet<long> seen, List<Planned> results, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(project)) return;
            if (depth > 12 || seen.Count > MaxDependencies)
                throw new InvalidDataException("必需前置数量或层级超过上限。");
            if (!Compatible(file, profile) ||
                (Text(file, "modId").Length > 0 && Id(file, "modId") != project))
                throw new InvalidDataException("模组文件不属于所选项目或与实例不兼容。");

            string filename = SafeFilename(Text(file, "fileName"));
            string sha1 = FileSha1(file);
            if (!NetworkDownloader.ValidSha1(sha1))
                throw new InvalidDataException(filename + " 缺少 SHA-1 校验值，无法自动安装。");
            string target = Path.Combine(mods, filename);
            string url = null;
            if (File.Exists(target))
            {
                if (!MatchesHash(target, sha1))
                    throw new IOException("实例内已有同名但内容不同的文件：" + filename);
            }
            else
                url = await ResolveDownloadUrlAsync(project, file, filename, ct).ConfigureAwait(false);

            foreach (long dependency in RequiredProjects(file))
                if (!seen.Contains(dependency))
                {
                    var child = await CompatibleFileAsync(dependency, profile, mods, ct).ConfigureAwait(false);
                    await Visit(dependency, child, profile, mods, seen, results, depth + 1, ct).ConfigureAwait(false);
                }
            results.Add(new Planned { Name = filename, Url = url, Sha1 = sha1 });
        }

        public async Task<IReadOnlyList<string>> InstallAsync(long project, GameProfile profile,
            IProgress<string> progress, CancellationToken ct, long? selectedFileId = null,
            IProgress<DownloadBytes> bytes = null)
        {
            RequireEnabled();
            if (profile == null || string.IsNullOrWhiteSpace(profile.Directory) ||
                string.IsNullOrWhiteSpace(profile.BaseVersion))
                throw new InvalidOperationException("请先选择游戏实例。");
            RequireLoader(profile.Loader);
            if (project < 1) throw new ArgumentOutOfRangeException(nameof(project));
            if (selectedFileId.HasValue && selectedFileId.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(selectedFileId));

            string root = Path.GetFullPath(profile.Directory);
            string lockPath = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            int hash = StringComparer.OrdinalIgnoreCase.GetHashCode(lockPath) & int.MaxValue;
            var gate = InstallLocks[hash % InstallLocks.Length];
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await InstallIntoInstanceAsync(project, profile, selectedFileId,
                    root, progress, bytes, ct).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        async Task<IReadOnlyList<string>> InstallIntoInstanceAsync(long project, GameProfile profile,
            long? selectedFileId, string root, IProgress<string> progress,
            IProgress<DownloadBytes> bytes, CancellationToken ct)
        {
            string mods = Path.Combine(root, "mods");
            progress?.Report("正在核对所选版本与必需前置模组…");
            var selected = selectedFileId.HasValue
                ? await GetFileAsync(project, selectedFileId.Value, ct).ConfigureAwait(false)
                : await CompatibleFileAsync(project, profile, null, ct).ConfigureAwait(false);

            var plan = new List<Planned>();
            await Visit(project, selected, profile, mods, new HashSet<long>(), plan, 0, ct).ConfigureAwait(false);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plan)
                if (!names.Add(item.Name))
                    throw new IOException("多个必需模组使用同一文件名：" + item.Name);

            var toDownload = plan.Where(file => file.Url != null).ToArray();
            if (toDownload.Length == 0) return Array.Empty<string>();
            Directory.CreateDirectory(mods);
            string stage = Path.Combine(root, ".blockcraft-cf-stage-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var committed = new List<string>();
            try
            {
                for (int i = 0; i < toDownload.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = toDownload[i];
                    progress?.Report("下载 CurseForge 模组 " + (i + 1) + "/" +
                        toDownload.Length + " · " + item.Name);
                    await DownloadJarAsync(item, Path.Combine(stage, item.Name), bytes, ct).ConfigureAwait(false);
                }
                foreach (var item in toDownload)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = Path.Combine(mods, item.Name);
                    if (File.Exists(target))
                    {
                        if (MatchesHash(target, item.Sha1)) continue;
                        throw new IOException("安装时发现同名文件：" + item.Name);
                    }
                    File.Move(Path.Combine(stage, item.Name), target);
                    committed.Add(target);
                }
                return committed;
            }
            catch
            {
                foreach (var path in committed)
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                throw;
            }
            finally
            {
                try { Directory.Delete(stage, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Dispose()
        {
            official?.Dispose();
            net?.Dispose();
        }
    }
}
