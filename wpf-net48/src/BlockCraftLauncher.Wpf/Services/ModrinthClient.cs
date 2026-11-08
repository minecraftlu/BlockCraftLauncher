using System;
using System.Collections.Generic;
using System.IO;
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
        public override string ToString() => Name + " — " + Description;
    }

    public sealed class ModrinthClient : IDisposable
    {
        readonly NetworkDownloader network;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        public ModrinthClient(LauncherConfig settings) { network = new NetworkDownloader(settings); }
        public async Task<IReadOnlyList<ResourceHit>> SearchAsync(string query, string type, CancellationToken ct)
        {
            var endpoint = "https://api.modrinth.com/v2/search?limit=20&query=" + Uri.EscapeDataString(query ?? "")
                + "&facets=" + Uri.EscapeDataString("[[\"project_type:" + type + "\"]]");
            string data = await network.GetStringWithFallbackAsync(endpoint, "official", ct);
            var parsed = json.DeserializeObject(data) as Dictionary<string, object>;
            var hits = new List<ResourceHit>();
            if (parsed != null && parsed.TryGetValue("hits", out var raw) && raw is object[] rows)
                foreach (var item in rows)
                {
                    if (!(item is Dictionary<string, object> p)) continue;
                    hits.Add(new ResourceHit { ProjectId = Get(p, "project_id"), Name = Get(p, "title"),
                        Description = Get(p, "description"), Kind = type });
                }
            return hits;
        }
        static string Get(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var x) && x != null ? Convert.ToString(x) : "";
        public async Task<string> DownloadLatestAsync(ResourceHit hit, GameProfile profile, CancellationToken ct)
        {
            if (hit == null || profile == null) throw new ArgumentNullException("请选择资源和游戏实例");
            if (hit.Kind != "mod" && hit.Kind != "shader" && hit.Kind != "resourcepack")
                throw new NotSupportedException("整合包安装需要安全解压及依赖处理，本预览版尚未迁移。请使用旧版。");
            string url = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(hit.ProjectId) + "/version";
            var data = await network.GetStringWithFallbackAsync(url, "official", ct);
            var versions = json.DeserializeObject(data) as object[];
            if (versions == null || versions.Length == 0) throw new IOException("找不到可下载的资源版本");
            var latest = versions.OfType<Dictionary<string, object>>().FirstOrDefault(v =>
            {
                var gameVersions = v.TryGetValue("game_versions", out var g) ? g as object[] : null;
                if (gameVersions == null || !gameVersions.Any(x => Convert.ToString(x) == profile.BaseVersion)) return false;
                if (hit.Kind != "mod") return true;
                // Modrinth versions can target different mod loaders. An
                // incompatible jar must never be installed into the profile.
                var loaderValues = v.TryGetValue("loaders", out var l) ? l as object[] : null;
                var loader = (profile.Loader ?? "").ToLowerInvariant();
                if (loader == "vanilla" || loader == "") return false;
                return loaderValues != null && loaderValues.Any(x =>
                    string.Equals(Convert.ToString(x), loader, StringComparison.OrdinalIgnoreCase));
            });
            if (latest == null) throw new IOException("没有与当前 Minecraft 版本和加载器都兼容的资源文件。请检查 Fabric/Forge/NeoForge/Quilt 选择。");
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
            await network.DownloadVerifiedAsync(Get(file, "url"), destination, sha1, ct);
            return destination;
        }
        public void Dispose() => network.Dispose();
    }
}
