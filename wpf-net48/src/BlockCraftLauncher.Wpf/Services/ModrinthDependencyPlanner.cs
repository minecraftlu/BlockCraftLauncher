using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// A validated, read-only installation plan for one Modrinth mod and its
    /// REQUIRED dependencies. A plan does not touch existing game files.
    /// All metadata must come from the official HTTPS Modrinth API.
    /// </summary>
    public sealed class PlannedModrinthFile
    {
        public string ProjectId { get; set; }
        public string VersionId { get; set; }
        public string Title { get; set; }
        public string Filename { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha1 { get; set; }
    }

    public sealed class ModrinthDependencyPlanner
    {
        const int MaxNodes = 32;
        const int MaxDepth = 16;
        readonly Func<string, CancellationToken, Task<string>> getJson;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        readonly Dictionary<string, Dictionary<string, object>> versions =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> selected =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> traversing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<PlannedModrinthFile> ordered = new List<PlannedModrinthFile>();
        string minecraftVersion;
        string loader;

        public ModrinthDependencyPlanner(Func<string, CancellationToken, Task<string>> requestJson)
        {
            getJson = requestJson ?? throw new ArgumentNullException(nameof(requestJson));
        }

        static Dictionary<string, object> Dict(object value) => value as Dictionary<string, object>;
        static string Field(IDictionary<string, object> value, string name)
        {
            return value != null && value.TryGetValue(name, out var field) && field != null ?
                Convert.ToString(field) : "";
        }
        static object[] Array(IDictionary<string, object> value, string name)
        {
            return value != null && value.TryGetValue(name, out var raw) ? raw as object[] : null;
        }
        static bool ValidId(string id)
        {
            return !string.IsNullOrEmpty(id) && id.Length <= 64 &&
                id.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
        }

        static string SafeFilename(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 180 ||
                name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.IndexOfAny(new[] {'/', '\\', ':'}) >= 0 ||
                name.EndsWith(".", StringComparison.Ordinal) ||
                name.EndsWith(" ", StringComparison.Ordinal) ||
                !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Modrinth 模组文件名不安全或不是 JAR 文件");
            return name;
        }
        static string TrustedUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "cdn.modrinth.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("模组下载地址不是受信任的 Modrinth HTTPS CDN");
            return uri.AbsoluteUri;
        }
        static string Sha1(string value)
        {
            if (value.Length != 40 || !value.All(Uri.IsHexDigit))
                throw new InvalidDataException("必需依赖缺少有效 SHA-1，不能自动安装");
            return value.ToLowerInvariant();
        }

        bool Compatible(Dictionary<string, object> version)
        {
            var supported = Array(version, "game_versions");
            var loaders = Array(version, "loaders");
            return supported != null &&
                supported.Any(item => string.Equals(Convert.ToString(item), minecraftVersion,
                    StringComparison.Ordinal)) &&
                loaders != null &&
                loaders.Any(item => string.Equals(Convert.ToString(item), loader,
                    StringComparison.OrdinalIgnoreCase));
        }

        async Task<Dictionary<string, object>> FetchVersionById(string id, CancellationToken ct)
        {
            if (!ValidId(id)) throw new InvalidDataException("Modrinth 依赖版本 ID 不合法");
            if (versions.TryGetValue(id, out var cached)) return cached;
            var raw = await getJson("https://api.modrinth.com/v2/version/" +
                Uri.EscapeDataString(id), ct);
            var result = Dict(json.DeserializeObject(raw ?? ""));
            if (result == null || !string.Equals(Field(result, "id"), id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Modrinth 依赖版本返回数据不一致");
            versions[id] = result;
            return result;
        }

        async Task<Dictionary<string, object>> FindVersion(string projectId, string explicitVersion,
            CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(explicitVersion))
            {
                var version = await FetchVersionById(explicitVersion, ct);
                if (!string.IsNullOrEmpty(projectId) &&
                    !string.Equals(Field(version, "project_id"), projectId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("必需依赖的项目 ID 与固定版本不匹配");
                if (!Compatible(version))
                    throw new InvalidDataException("必需依赖的固定版本与 Minecraft / 加载器不兼容");
                return version;
            }
            if (!ValidId(projectId)) throw new InvalidDataException("Modrinth 项目 ID 不合法");
            // Ask Modrinth to filter BEFORE fetching a project's version list.
            // Popular open-source mods can have thousands of old versions; an
            // unfiltered 200-row hard cap previously blocked valid downloads.
            string url = "https://api.modrinth.com/v2/project/" +
                Uri.EscapeDataString(projectId) + "/version?loaders=" +
                Uri.EscapeDataString(json.Serialize(new[] { loader })) +
                "&game_versions=" +
                Uri.EscapeDataString(json.Serialize(new[] { minecraftVersion })) +
                "&include_changelog=false";
            var data = await getJson(url, ct);
            var rows = json.DeserializeObject(data ?? "") as object[];
            if (rows == null || rows.Length > 500)
                throw new InvalidDataException("Modrinth 兼容版本列表无效或过长");
            var matching = rows.OfType<Dictionary<string, object>>().FirstOrDefault(v =>
            {
                if (!string.Equals(Field(v, "project_id"), projectId,
                    StringComparison.OrdinalIgnoreCase) || !Compatible(v)) return false;
                try { ResolveFile(v); return true; }
                catch (InvalidDataException) { return false; }
            });
            if (matching == null)
                throw new InvalidDataException("模组或必需依赖没有兼容当前 Minecraft 版本和加载器的版本：" + projectId);
            return matching;
        }

        PlannedModrinthFile ResolveFile(Dictionary<string, object> version)
        {
            var files = Array(version, "files")?.OfType<Dictionary<string, object>>().ToArray();
            if (files == null || files.Length == 0 || files.Length > 30)
                throw new InvalidDataException("模组版本没有合法的 JAR 下载文件");
            // A primary download may be a sources/dev/Javadoc JAR on some
            // open-source projects. Select a validated runnable JAR instead
            // of failing or accidentally installing a source archive.
            foreach (var candidate in files.OrderByDescending(f =>
                f.TryGetValue("primary", out var primary) &&
                primary is bool isPrimary && isPrimary))
            {
                string name = Field(candidate, "filename");
                string kind = Field(candidate, "file_type");
                if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    kind == "sources-jar" || kind == "dev-jar" ||
                    kind == "javadoc-jar" ||
                    name.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("-dev.jar", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    string filename = SafeFilename(name);
                    var hashes = Dict(candidate.TryGetValue("hashes", out var rawHashes) ?
                        rawHashes : null);
                    return new PlannedModrinthFile {
                        ProjectId = Field(version, "project_id"),
                        VersionId = Field(version, "id"),
                        Title = Field(version, "name"),
                        Filename = filename,
                        DownloadUrl = TrustedUrl(Field(candidate, "url")),
                        Sha1 = Sha1(Field(hashes, "sha1"))
                    };
                }
                catch (InvalidDataException)
                {
                    // Try the next provider-declared file, without weakening
                    // checksum or trusted CDN validation.
                }
            }
            throw new InvalidDataException("该版本没有带有效哈希的可安装模组 JAR 文件");
        }

        async Task Visit(string projectId, string versionId, int depth, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > MaxDepth) throw new InvalidDataException("Modrinth 必需依赖层级超过 16");
            if (selected.Count >= MaxNodes && !selected.ContainsKey(projectId))
                throw new InvalidDataException("必需依赖超过 32 个，拒绝无限递归下载");
            var version = await FindVersion(projectId, versionId, ct);
            string actualProjectId = Field(version, "project_id");
            string actualVersionId = Field(version, "id");
            if (!ValidId(actualProjectId) || !ValidId(actualVersionId))
                throw new InvalidDataException("Modrinth 依赖缺少有效项目/版本 ID");
            if (selected.TryGetValue(actualProjectId, out var earlierVersion))
            {
                if (!string.Equals(earlierVersion, actualVersionId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("多个模组依赖要求同一项目的不同固定版本");
                return; // Already resolved, or a cycle back-edge.
            }
            if (selected.Count >= MaxNodes)
                throw new InvalidDataException("必需依赖数量超过 32");
            selected.Add(actualProjectId, actualVersionId);
            traversing.Add(actualProjectId);
            var file = ResolveFile(version);
            if (!filenames.Add(file.Filename))
                throw new InvalidDataException("两个不同模组的下载文件名相同，拒绝覆盖：" + file.Filename);

            var dependencies = Array(version, "dependencies");
            if (dependencies != null)
            {
                if (dependencies.Length > MaxNodes * 4)
                    throw new InvalidDataException("模组声明的依赖数量异常");
                foreach (var dependency in dependencies)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = Dict(dependency);
                    if (item == null) throw new InvalidDataException("模组依赖条目结构错误");
                    if (Field(item, "dependency_type") != "required") continue;
                    string depProject = Field(item, "project_id");
                    string depVersion = Field(item, "version_id");
                    if (string.IsNullOrEmpty(depProject) && string.IsNullOrEmpty(depVersion))
                        throw new InvalidDataException("必需依赖缺少 Modrinth 项目或版本 ID");
                    if (string.IsNullOrEmpty(depProject))
                    {
                        var fixedVersion = await FetchVersionById(depVersion, ct);
                        depProject = Field(fixedVersion, "project_id");
                    }
                    if (!ValidId(depProject) ||
                        (!string.IsNullOrEmpty(depVersion) && !ValidId(depVersion)))
                        throw new InvalidDataException("必需依赖的 ID 不合法");
                    await Visit(depProject, depVersion, depth + 1, ct);
                }
            }
            traversing.Remove(actualProjectId);
            completed.Add(actualProjectId);
            ordered.Add(file); // Dependencies first, selected mod last.
        }

        public async Task<IReadOnlyList<PlannedModrinthFile>> PlanAsync(string rootProjectId,
            string gameVersion, string modLoader, CancellationToken ct)
        {
            if (ordered.Count != 0 || selected.Count != 0)
                throw new InvalidOperationException("每份安装计划只能使用一次");
            if (!ValidId(rootProjectId) || string.IsNullOrWhiteSpace(gameVersion) ||
                gameVersion.Length > 64 || gameVersion.Contains("/") ||
                string.IsNullOrWhiteSpace(modLoader) ||
                string.Equals(modLoader, "vanilla", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请为模组选择有效的 Minecraft 版本和 Fabric/Forge/NeoForge/Quilt 实例");
            minecraftVersion = gameVersion;
            loader = modLoader;
            await Visit(rootProjectId, null, 0, ct);
            return ordered.AsReadOnly();
        }
    }
}
