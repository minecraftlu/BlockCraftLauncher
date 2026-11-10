using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class ResourceHit
    {
        public string ProjectId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Kind { get; set; }
        public string TargetGameVersion { get; set; }
        public string Author { get; set; }
        public string IconUrl { get; set; }
        public long DownloadCount { get; set; }
        public string DownloadsLabel { get; set; }
        public string UpdatedLabel { get; set; }
        public string CategoryLabel { get; set; }
        public string LoaderLabel { get; set; }
        public string VersionsLabel { get; set; }
        public string Source { get; set; } = "Modrinth";
        public string SourceLabel => Source == "GitHub" ? "GitHub 开源" : "Modrinth";
        public string IconDescription => Source == "GitHub"
            ? "Github 开源项目作者头像（仅作来源识别，不代表模组官方图标）"
            : "Modrinth 项目图标；无法获取时显示默认模组图标";
        public string KindLabel
        {
            get
            {
                if (Source == "GitHub") return "社区开源仓库 · GitHub Releases";
                switch (Kind)
                {
                    case "mod": return "模组 · Modrinth";
                    case "shader": return "光影包 · Modrinth";
                    case "resourcepack": return "资源包 · Modrinth";
                    case "modpack": return "整合包 · Modrinth";
                    default: return "社区资源 · Modrinth";
                }
            }
        }
        public override string ToString() => Name + " — " + Description;
    }

    public sealed class ModrinthSearchPage
    {
        public IReadOnlyList<ResourceHit> Hits { get; set; }
        public int TotalHits { get; set; }
    }

    public static class ModrinthSearchMetadata
    {
        public static string DownloadCountLabel(long count)
        {
            if (count < 0) return "下载量未知";
            if (count >= 100000000) return (count / 100000000.0).ToString("0.##", CultureInfo.InvariantCulture) + " 亿次下载";
            if (count >= 10000) return (count / 10000.0).ToString("0.##", CultureInfo.InvariantCulture) + " 万次下载";
            return count.ToString("N0", CultureInfo.InvariantCulture) + " 次下载";
        }

        public static string TrustedIconUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                return null;
            return uri.AbsoluteUri;
        }

        public static string CategoryName(string category)
        {
            switch (category)
            {
                case "optimization": return "性能优化";
                case "adventure": return "冒险";
                case "technology": return "科技";
                case "decoration": return "装饰";
                case "utility": return "实用工具";
                case "worldgen": return "世界生成";
                case "mobs": return "生物";
                case "library": return "支持库";
                case "management": return "管理";
                case "equipment": return "装备";
                case "gameplay": return "玩法";
                case "storage": return "存储";
                default: return category ?? "";
            }
        }
    }

    public sealed class ModrinthClient : IDisposable
    {
        // Do not interleave commits and registry writes for one game instance.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        readonly NetworkDownloader network;
        readonly LauncherConfig settings;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public ModrinthClient(LauncherConfig settings) { this.settings = settings; network = new NetworkDownloader(settings); }
        public async Task<IReadOnlyList<ResourceHit>> SearchAsync(string query, string type, CancellationToken ct,
            string gameVersion = null, string loader = null, string sort = "relevance", int offset = 0)
        {
            var page = await SearchPageAsync(query, type, ct, gameVersion, loader, sort, offset);
            return page.Hits;
        }

        /// <summary>
        /// Modrinth v2 official facets; search fields are data, not install
        /// authorization. The returned metadata drives PCL-like result rows.
        /// </summary>
        public async Task<ModrinthSearchPage> SearchPageAsync(string query, string type, CancellationToken ct,
            string gameVersion = null, string loader = null, string sort = "relevance",
            int offset = 0, string category = null)
        {
            if (type != "mod" && type != "shader" && type != "resourcepack" && type != "modpack")
                throw new ArgumentException("不支持的 Modrinth 搜索类别");
            if ((query ?? "").Length > 150 || offset < 0 || offset > 2000)
                throw new ArgumentException("搜索关键词或分页参数无效");
            if (sort != "relevance" && sort != "downloads" && sort != "updated" && sort != "newest")
                sort = "relevance";
            if (!string.IsNullOrEmpty(gameVersion) &&
                (gameVersion.Length > 50 || gameVersion.Any(char.IsControl)))
                throw new ArgumentException("Minecraft 版本号不合法");
            if (string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) || loader == "原版")
                loader = null;
            if (!string.IsNullOrEmpty(loader) &&
                !new[] { "fabric", "forge", "neoforge", "quilt", "liteloader", "rift" }
                    .Contains(loader, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("加载器筛选条件不合法");
            if (!string.IsNullOrEmpty(category) &&
                !new[] { "optimization", "technology", "adventure", "decoration", "utility",
                    "worldgen", "mobs", "library", "gameplay", "equipment", "storage",
                    "management" }.Contains(category, StringComparer.Ordinal))
                throw new ArgumentException("资源分类筛选条件不合法");
            var facets = new List<object> { new[] { "project_type:" + type } };
            if (!string.IsNullOrWhiteSpace(gameVersion))
                facets.Add(new[] { "versions:" + gameVersion.Trim() });
            if (type == "mod" && !string.IsNullOrWhiteSpace(loader) && loader != "vanilla")
                facets.Add(new[] { "categories:" + loader.Trim().ToLowerInvariant() });
            if (type == "mod" && !string.IsNullOrWhiteSpace(category))
                facets.Add(new[] { "categories:" + category });
            string endpoint = "https://api.modrinth.com/v2/search?limit=20&offset=" + offset +
                "&index=" + Uri.EscapeDataString(sort) +
                "&query=" + Uri.EscapeDataString(query ?? "") +
                "&facets=" + Uri.EscapeDataString(json.Serialize(facets));
            var page = await network.GetMetadataWithFallbackAsync(endpoint, settings.NetworkMode,
                data => ParseSearchResponse(data, type), ct);
            if (type == "modpack" && !string.IsNullOrWhiteSpace(gameVersion))
                foreach (var hit in page.Hits)
                    hit.TargetGameVersion = gameVersion.Trim();
            return page;
        }

        /// <summary>
        /// Separately testable parser for Modrinth search metadata. Never
        /// trusts icon hosts, download counts or project IDs as install URLs.
        /// </summary>
        public static ModrinthSearchPage ParseSearchResponse(string data, string type)
        {
            if (string.IsNullOrWhiteSpace(data) || data.Length > 12 * 1024 * 1024)
                throw new InvalidDataException("Modrinth 搜索响应为空或超出大小限制");
            var parsed = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                .DeserializeObject(data) as Dictionary<string, object>;
            if (parsed == null || !(parsed.TryGetValue("hits", out var raw) && raw is object[] rows))
                throw new InvalidDataException("Modrinth 搜索响应缺少项目列表");
            var hits = new List<ResourceHit>();
            foreach (var item in rows.Take(20))
            {
                if (!(item is Dictionary<string, object> p)) continue;
                string projectId = Get(p, "project_id");
                if (string.IsNullOrWhiteSpace(projectId) || projectId.Length > 128) continue;
                var cats = p.TryGetValue("categories", out var categories)
                    ? categories as object[] : null;
                var loaders = cats == null ? new string[0] :
                    cats.Select(Convert.ToString)
                        .Where(v => v == "fabric" || v == "forge" || v == "neoforge" || v == "quilt")
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var versions = p.TryGetValue("versions", out var rawVersions)
                    ? rawVersions as object[] : null;
                string updatedText = "更新日期未知";
                if (DateTime.TryParse(Get(p, "date_modified"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var updated))
                    updatedText = "更新 " + updated.ToLocalTime().ToString("yyyy-MM-dd");
                long downloads = -1;
                if (p.TryGetValue("downloads", out var rawDownloads))
                    long.TryParse(Convert.ToString(rawDownloads, CultureInfo.InvariantCulture), out downloads);
                hits.Add(new ResourceHit {
                    ProjectId = projectId,
                    Name = Get(p, "title"), Description = Get(p, "description"),
                    Kind = type, Author = Get(p, "author"),
                    IconUrl = ModrinthSearchMetadata.TrustedIconUrl(Get(p, "icon_url")),
                    DownloadCount = downloads,
                    DownloadsLabel = ModrinthSearchMetadata.DownloadCountLabel(downloads),
                    UpdatedLabel = updatedText,
                    CategoryLabel = cats == null ? "" :
                        string.Join(" · ", cats.Select(Convert.ToString)
                            .Where(x => x != "fabric" && x != "forge" &&
                                x != "neoforge" && x != "quilt")
                            .Take(2).Select(ModrinthSearchMetadata.CategoryName)),
                    LoaderLabel = loaders.Length > 0 ? string.Join(" / ", loaders) : "加载器未指定",
                    VersionsLabel = versions != null && versions.Length > 0
                        ? string.Join(" / ", versions.Skip(Math.Max(0, versions.Length - 3))
                            .Select(Convert.ToString)) : "版本未指定"
                });
            }
            int total = hits.Count;
            if (parsed.TryGetValue("total_hits", out var totalObject) &&
                int.TryParse(Convert.ToString(totalObject, CultureInfo.InvariantCulture),
                    out var totalParsed) && totalParsed >= 0)
                total = totalParsed;
            return new ModrinthSearchPage { Hits = hits, TotalHits = total };
        }
        Task<object[]> GetVersionRowsAsync(string url, CancellationToken ct) =>
            network.GetMetadataWithFallbackAsync(url, settings.NetworkMode,
                data => json.DeserializeObject(data) as object[] ??
                    throw new InvalidDataException("Modrinth 版本列表格式无效"), ct);

        static bool MatchesGameVersion(Dictionary<string, object> version, string gameVersion) =>
            string.IsNullOrWhiteSpace(gameVersion) ||
            version.TryGetValue("game_versions", out var values) && values is object[] list &&
            list.Any(x => string.Equals(Convert.ToString(x), gameVersion, StringComparison.OrdinalIgnoreCase));

        static string Get(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var x) && x != null ? Convert.ToString(x) : "";
        public async Task<string> DownloadLatestAsync(ResourceHit hit, GameProfile profile, CancellationToken ct, IProgress<DownloadBytes> progress = null)
        {
            if (hit == null || profile == null) throw new ArgumentNullException("请选择资源和游戏实例");
            if (hit.Kind == "mod")
                return await DownloadModWithDependenciesAsync(hit, profile, ct, progress);
            if (hit.Kind != "mod" && hit.Kind != "shader" && hit.Kind != "resourcepack")
                throw new NotSupportedException("请通过「整合包」页面安装 Modrinth 整合包。");
            string url = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(hit.ProjectId) + "/version";
            var versions = await GetVersionRowsAsync(url, ct);
            if (versions == null || versions.Length == 0) throw new IOException("找不到可下载的资源版本");
            var latest = versions.OfType<Dictionary<string, object>>()
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(profile.BaseVersion) && MatchesGameVersion(v, profile.BaseVersion));
            if (latest == null) throw new IOException("没有适用于 Minecraft " + profile.BaseVersion + " 的资源文件。");
            var files = latest.TryGetValue("files", out var f) ? f as object[] : null;
            var file = files?.OfType<Dictionary<string, object>>().FirstOrDefault(x => x.TryGetValue("primary", out var primary) && primary is bool b && b)
                ?? files?.OfType<Dictionary<string, object>>().FirstOrDefault();
            if (file == null) throw new IOException("版本没有下载文件");
            string filename = Get(file, "filename");
            if (string.IsNullOrWhiteSpace(filename) || Path.GetFileName(filename) != filename || filename.Contains("\\") || filename == "." || filename == "..")
                throw new IOException("文件名不安全，已取消下载");
            string folder = hit.Kind == "mod" ? "mods" : hit.Kind == "shader" ? "shaderpacks" : "resourcepacks";
            var hashFields = file.TryGetValue("hashes", out var hashes) ? hashes as Dictionary<string, object> : null;
            string sha1 = hashFields != null ? Get(hashFields, "sha1") : "";
            if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
                throw new IOException("服务端没有提供有效的 SHA-1 校验值，已取消下载。");
            string destination = Path.Combine(profile.Directory, folder, filename);
            if (File.Exists(destination))
                throw new IOException("同名资源已存在，拒绝覆盖已安装或手动导入的文件：" + filename);
            string downloadUrl = Get(file, "url");
            if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var downloadUri) ||
                downloadUri.Scheme != Uri.UriSchemeHttps || downloadUri.Host != "cdn.modrinth.com" ||
                downloadUri.UserInfo.Length != 0 || !downloadUri.IsDefaultPort)
                throw new InvalidDataException("下载源不是可信的 Modrinth HTTPS CDN");
            await network.DownloadVerifiedDetailedAsync(downloadUrl, destination, sha1, ct, progress);
            new ManagedModUpdateService(settings).Record(profile, hit.ProjectId, hit.Kind,
                hit.Name, Get(latest, "id"), filename, sha1);
            return destination;
        }
        static string FileHash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "")
                    .ToLowerInvariant();
        }

        /// <summary>
        /// Download root mod and every transitive *required* Modrinth
        /// dependency in a bounded plan. Stage all files before publishing,
        /// then register the completed set in one atomic registry update.
        /// No existing mod/JAR is overwritten, even on retries.
        /// </summary>
        public async Task<string> DownloadModWithDependenciesAsync(ResourceHit hit,
            GameProfile profile, CancellationToken ct, IProgress<DownloadBytes> progress = null,
            IProgress<string> stage = null)
        {
            if (hit == null || hit.Kind != "mod" || profile == null)
                throw new ArgumentException("请先选择模组和有效游戏实例");
            if (string.IsNullOrWhiteSpace(profile.Directory))
                throw new InvalidDataException("游戏实例缺少有效目录");
            string root = Path.GetFullPath(profile.Directory);
            string mods = Path.Combine(root, "mods");
            var gate = InstallLocks.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            string staging = null;
            var committed = new List<string>();
            try
            {
                stage?.Report("正在检查必需依赖、循环引用及版本兼容性...");
                var planner = new ModrinthDependencyPlanner(
                    (uri, token) => network.GetStringWithFallbackAsync(uri, settings.McimEnabled ? settings.NetworkMode : "official", token));
                var plan = await planner.PlanAsync(hit.ProjectId,
                    profile.BaseVersion, profile.Loader, ct);
                if (plan.Count == 0)
                    throw new InvalidDataException("Modrinth 未返回可安装的资源");
                // All metadata must pass validation BEFORE the first download.
                var toInstall = new List<PlannedModrinthFile>();
                foreach (var row in plan)
                {
                    ct.ThrowIfCancellationRequested();
                    string target = Path.Combine(mods, row.Filename);
                    if (File.Exists(target))
                    {
                        if (!FileHash(target).Equals(row.Sha1, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("实例已有同名但不同版本的模组；"
                                + "为保护已有文件，安装已中止：" + row.Filename);
                        // Already present and hash-correct: it can satisfy a
                        // dependency, but must NOT be reclassified as a managed
                        // mod if a user installed the JAR manually.
                        continue;
                    }
                    toInstall.Add(row);
                }
                if (toInstall.Count == 0)
                    return Path.Combine(mods, plan.Last().Filename);

                Directory.CreateDirectory(mods);
                staging = Path.Combine(root, ".blockcraft-mod-stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                int current = 0;
                foreach (var row in toInstall)
                {
                    ct.ThrowIfCancellationRequested();
                    current++;
                    stage?.Report("下载必需模组 " + current + "/" + toInstall.Count
                        + "：" + row.Filename);
                    await network.DownloadVerifiedDetailedAsync(row.DownloadUrl,
                        Path.Combine(staging, row.Filename), row.Sha1, ct, progress);
                }

                ct.ThrowIfCancellationRequested();
                foreach (var row in toInstall)
                {
                    // File.Move will throw rather than overwrite a user JAR.
                    // Recheck immediately before publishing because another
                    // process may have changed the destination after planning.
                    string destination = Path.Combine(mods, row.Filename);
                    if (File.Exists(destination))
                        throw new IOException("安装过程中检测到另一个同名模组，"
                            + "为避免覆盖已中止：" + row.Filename);
                    File.Move(Path.Combine(staging, row.Filename), destination);
                    committed.Add(destination);
                }
                ct.ThrowIfCancellationRequested();
                var records = toInstall.Select(row => new ManagedResource {
                    ProjectId = row.ProjectId, Kind = "mod",
                    Title = row.Title, VersionId = row.VersionId,
                    Filename = row.Filename, Sha1 = row.Sha1
                }).ToArray();
                new ManagedModUpdateService(settings).RecordBatch(profile, records);
                stage?.Report("已完成模组及 " + (plan.Count - 1)
                    + " 项必需依赖的兼容性校验与安装。");
                return Path.Combine(mods, plan.Last().Filename);
            }
            catch
            {
                // Only remove files created by THIS operation. Existing mods
                // and manually imported resources must never be touched.
                foreach (var file in committed)
                {
                    try { if (File.Exists(file)) File.Delete(file); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
            finally
            {
                if (staging != null)
                {
                    try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                gate.Release();
            }
        }

        public async Task<string> DownloadModpackArchiveAsync(ResourceHit hit, string destination, CancellationToken ct, IProgress<DownloadBytes> progress = null)
        {
            if (hit == null || hit.Kind != "modpack") throw new ArgumentException("请选择 Modrinth 整合包");
            if (string.IsNullOrWhiteSpace(hit.ProjectId)) throw new ArgumentException("整合包项目 ID 不正确");
            string url = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(hit.ProjectId) + "/version";
            var versions = await GetVersionRowsAsync(url, ct);
            if (versions == null || versions.Length == 0) throw new IOException("整合包没有可用版本");
            foreach (var version in versions.OfType<Dictionary<string, object>>())
            {
                if (!MatchesGameVersion(version, hit.TargetGameVersion)) continue;
                var files = version.TryGetValue("files", out var f) ? f as object[] : null;
                var archive = files?.OfType<Dictionary<string, object>>().FirstOrDefault(x =>
                    Get(x, "filename").EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
                if (archive == null) continue;
                var fields = archive.TryGetValue("hashes", out var rawHash) ? rawHash as Dictionary<string, object> : null;
                string sha1 = fields == null ? "" : Get(fields, "sha1");
                if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
                    throw new InvalidDataException("整合包索引缺少有效 SHA-1 哈希");
                string downloadUrl = Get(archive, "url");
                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "cdn.modrinth.com")
                    throw new InvalidDataException("整合包下载源不是可信 Modrinth CDN");
                await network.DownloadVerifiedDetailedAsync(downloadUrl, destination, sha1, ct, progress);
                return destination;
            }
            throw new IOException(string.IsNullOrWhiteSpace(hit.TargetGameVersion)
                ? "没有找到可用的 .mrpack 文件"
                : "找不到适用于 Minecraft " + hit.TargetGameVersion + " 的 .mrpack 文件。");
        }

        public void Dispose() => network.Dispose();
    }
}
