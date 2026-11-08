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
    public sealed class CurseForgeHit
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
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

        public async Task<IReadOnlyList<CurseForgeHit>> SearchAsync(string query, CancellationToken ct)
        {
            if ((query ?? "").Length > 150) throw new ArgumentException("搜索关键词过长");
            var url = "/mods/search?gameId=432&classId=6&index=0&pageSize=20&searchFilter="
                + Uri.EscapeDataString(query ?? "");
            var entries = await ApiGetAsync(url, ct) as object[];
            if (entries == null || entries.Length > 50) throw new InvalidDataException("搜索结果格式或条数异常");
            return entries.Select(Dict).Where(x => x != null).Select(x => new CurseForgeHit
            {
                Id = Id(x, "id"), Name = Text(x, "name"), Summary = Text(x, "summary")
            }).ToList();
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