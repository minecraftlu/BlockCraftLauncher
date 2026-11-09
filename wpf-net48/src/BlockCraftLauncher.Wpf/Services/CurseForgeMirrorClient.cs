using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

    /// <summary>
    /// Optional MCIM metadata mirror for CurseForge Minecraft mods.
    /// No borrowed or embedded CurseForge API key. Only downloads provider-
    /// issued HTTPS URLs with a real SHA-1 and explicit user confirmation.
    /// </summary>
    public sealed class CurseForgeMirrorClient : IDisposable
    {
        const string Api = "https://mod.mcimirror.top/curseforge/v1";
        const int MaxDependencies = 32;
        readonly LauncherConfig config;
        readonly NetworkDownloader net;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 };

        public CurseForgeMirrorClient(LauncherConfig settings)
        {
            config = settings;
            net = new NetworkDownloader(settings);
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
            if (!config.McimEnabled)
                throw new InvalidOperationException("请先在设置中选择启用 MCIM；非官方镜像可能有访问或分发限制。");
        }

        async Task<object> ApiGetAsync(string path, CancellationToken ct)
        {
            RequireEnabled();
            string url = Api + path;
            string jsonText = await net.GetStringWithFallbackAsync(url, "official", ct);
            var envelope = Dict(json.DeserializeObject(jsonText));
            if (envelope == null || !envelope.ContainsKey("data"))
                throw new InvalidDataException("MCIM 镜像返回了无效数据");
            return envelope["data"];
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

        public async Task<IReadOnlyList<CurseForgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            if ((query ?? "").Length > 150) throw new ArgumentException("搜索关键词过长");
            var url = "/mods/search?gameId=432&classId=6&index=0&pageSize=20&searchFilter="
                + Uri.EscapeDataString(query ?? "");
            var entries = await ApiGetAsync(url, ct);
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
        bool Compatible(Dictionary<string, object> file, GameProfile profile)
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

        async Task<Dictionary<string, object>> CompatibleFileAsync(long project, GameProfile profile, CancellationToken ct)
        {
            string version = profile.BaseVersion ?? "";
            if (!Regex.IsMatch(version, @"^[A-Za-z0-9._-]{1,64}$"))
                throw new InvalidDataException("Minecraft 版本号无效");
            int loader = LoaderCode(profile.Loader);
            var url = "/mods/" + project + "/files?gameVersion=" + Uri.EscapeDataString(version)
                + "&modLoaderType=" + loader + "&pageSize=50&index=0";
            var files = await ApiGetAsync(url, ct) as object[];
            var selected = files?.Select(Dict).Where(x => x != null && Compatible(x, profile))
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
            IProgress<string> progress, CancellationToken ct)
        {
            RequireEnabled();
            if (profile == null || string.IsNullOrWhiteSpace(profile.Directory))
                throw new InvalidOperationException("请先选择游戏实例");
            var compatible = await CompatibleFileAsync(project, profile, ct);
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

        public void Dispose() => net.Dispose();
    }
}