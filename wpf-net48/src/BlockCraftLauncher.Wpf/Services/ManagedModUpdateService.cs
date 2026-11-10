using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class ManagedResource
    {
        public string ProjectId { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string VersionId { get; set; }
        public string Filename { get; set; }
        public string Sha1 { get; set; }
    }
    public sealed class ResourceUpgrade
    {
        public ManagedResource Old { get; set; }
        public string VersionId { get; set; }
        public string VersionName { get; set; }
        public string Filename { get; set; }
        public string Url { get; set; }
        public string Sha1 { get; set; }
        public override string ToString() => Old.Title + "  " + Old.VersionId + " → " + VersionName;
    }

    /// <summary>
    /// Only manages resources previously installed through the WPF Modrinth UI.
    /// Never identifies or overwrites hand-copied mods. Updates retain a backup.
    /// </summary>
    public sealed class ManagedModUpdateService
    {
        readonly LauncherConfig config;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public ManagedModUpdateService(LauncherConfig config) { this.config = config; }

        static string RegistryPath(GameProfile profile) =>
            Path.Combine(profile.Directory, ".blockcraft-wpf-modrinth.json");
        static string Get(IDictionary<string, object> row, string key)
            => row != null && row.TryGetValue(key, out var x) && x != null ? Convert.ToString(x) : "";
        static string Folder(string kind)
        {
            switch (kind)
            {
                case "mod": return "mods";
                case "shader": return "shaderpacks";
                case "resourcepack": return "resourcepacks";
                default: throw new InvalidDataException("无效的资源类型");
            }
        }
        static string FileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value != Path.GetFileName(value) ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                value.Contains("\\") || value.Contains("/") || value == "." || value == "..")
                throw new InvalidDataException("无效的文件名称");
            return value;
        }
        static string FileSha1(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public List<ManagedResource> Load(GameProfile profile)
        {
            var path = RegistryPath(profile);
            if (!File.Exists(path)) return new List<ManagedResource>();
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("资源记录文件过大");
            var list = json.Deserialize<List<ManagedResource>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<ManagedResource>();
            foreach (var item in list)
            {
                FileName(item.Filename); Folder(item.Kind);
                if (item.ProjectId == null || item.ProjectId.Length > 128 || item.ProjectId.Contains("/") ||
                    item.ProjectId.Contains("\\"))
                    throw new InvalidDataException("资源记录存在无效项目 ID");
            }
            return list;
        }
        void Save(GameProfile profile, List<ManagedResource> records)
        {
            var path = RegistryPath(profile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, json.Serialize(records), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void Record(GameProfile profile, string projectId, string kind, string title,
            string versionId, string filename, string sha1)
        {
            FileName(filename); Folder(kind);
            if (sha1 == null || sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
                throw new InvalidDataException("无效 SHA-1");
            var path = Path.Combine(profile.Directory, Folder(kind), filename);
            if (!File.Exists(path) || !FileSha1(path).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("资源文件不存在或校验不符，不登记安装");
            var current = Load(profile);
            current.RemoveAll(x => x.ProjectId == projectId && x.Kind == kind);
            current.Add(new ManagedResource { ProjectId = projectId, Kind = kind,
                Title = title, VersionId = versionId, Filename = filename, Sha1 = sha1.ToLowerInvariant() });
            Save(profile, current);
        }

        /// <summary>
        /// Atomically register a completed dependency set. Existing manual
        /// files are never registered by this method; callers supply only
        /// newly committed files. If any dependency is invalid, no registry
        /// changes are saved.
        /// </summary>
        public void RecordBatch(GameProfile profile, IEnumerable<ManagedResource> items)
        {
            if (profile == null || items == null)
                throw new ArgumentNullException("请选择有效的实例和资源登记列表");
            var additions = items.ToList();
            if (additions.Count > 32)
                throw new InvalidDataException("单次依赖登记超过 32 个");
            if (additions.Count == 0) return;
            var current = Load(profile);
            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in additions)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ProjectId) ||
                    item.ProjectId.Length > 64 ||
                    item.ProjectId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_') ||
                    string.IsNullOrWhiteSpace(item.VersionId) ||
                    item.VersionId.Length > 64 ||
                    item.VersionId.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
                    throw new InvalidDataException("资源登记包含无效的 Modrinth 项目或版本 ID");
                string name = FileName(item.Filename);
                string folder = Folder(item.Kind);
                if (!filenames.Add(folder + "/" + name))
                    throw new InvalidDataException("资源安装包含重复文件名");
                if (item.Sha1 == null || item.Sha1.Length != 40 || !item.Sha1.All(Uri.IsHexDigit))
                    throw new InvalidDataException("资源登记的 SHA-1 不合法");
                string file = Path.Combine(profile.Directory, folder, name);
                if (!File.Exists(file) ||
                    !FileSha1(file).Equals(item.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("依赖资源文件缺失或 SHA-1 验证失败");
                if (current.Any(old => old.Kind == item.Kind &&
                    string.Equals(old.Filename, name, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(old.ProjectId, item.ProjectId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("已有资源登记占用相同文件名");
            }
            foreach (var item in additions)
            {
                current.RemoveAll(x => x.ProjectId == item.ProjectId && x.Kind == item.Kind);
                current.Add(new ManagedResource {
                    ProjectId = item.ProjectId, Kind = item.Kind,
                    Title = item.Title, VersionId = item.VersionId,
                    Filename = item.Filename, Sha1 = item.Sha1.ToLowerInvariant()
                });
            }
            Save(profile, current);
        }

        public async Task<IReadOnlyList<ResourceUpgrade>> CheckAsync(GameProfile profile, CancellationToken ct)
        {
            var records = Load(profile);
            var updates = new List<ResourceUpgrade>();
            using (var net = new NetworkDownloader(config))
            {
                foreach (var old in records)
                {
                    ct.ThrowIfCancellationRequested();
                    string oldPath = Path.Combine(profile.Directory, Folder(old.Kind), old.Filename);
                    if (!File.Exists(oldPath) || !FileSha1(oldPath).Equals(old.Sha1, StringComparison.OrdinalIgnoreCase))
                        continue; // Unknown/manual modification must not be updated.
                    var url = "https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(old.ProjectId) + "/version";
                    var data = await net.GetStringWithFallbackAsync(url, config.NetworkMode, ct);
                    var versions = json.DeserializeObject(data) as object[];
                    if (versions == null) continue;
                    foreach (var item in versions.OfType<Dictionary<string, object>>())
                    {
                        var game = item.TryGetValue("game_versions", out var g) ? g as object[] : null;
                        if (game == null || !game.Any(x => Convert.ToString(x) == profile.BaseVersion)) continue;
                        if (old.Kind == "mod")
                        {
                            var loaders = item.TryGetValue("loaders", out var l) ? l as object[] : null;
                            if (loaders == null || !loaders.Any(x => string.Equals(Convert.ToString(x), profile.Loader, StringComparison.OrdinalIgnoreCase)))
                                continue;
                        }
                        string id = Get(item, "id");
                        if (id == old.VersionId) break;
                        var files = item.TryGetValue("files", out var raw) ? raw as object[] : null;
                        var selected = files?.OfType<Dictionary<string, object>>().FirstOrDefault(x =>
                            x.TryGetValue("primary", out var p) && p is bool primary && primary)
                            ?? files?.OfType<Dictionary<string, object>>().FirstOrDefault();
                        if (selected == null) continue;
                        var hashes = selected.TryGetValue("hashes", out var h) ? h as Dictionary<string, object> : null;
                        string sha1 = Get(hashes, "sha1");
                        if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit)) continue;
                        string filename = FileName(Get(selected, "filename"));
                        string download = Get(selected, "url");
                        if (!Uri.TryCreate(download, UriKind.Absolute, out var parsed) ||
                            parsed.Scheme != Uri.UriSchemeHttps || parsed.Host != "cdn.modrinth.com") continue;
                        updates.Add(new ResourceUpgrade { Old = old, VersionId = id,
                            VersionName = Get(item, "version_number"), Filename = filename, Sha1 = sha1, Url = download });
                        break;
                    }
                }
            }
            return updates;
        }

        public async Task<string> ApplyAsync(GameProfile profile, ResourceUpgrade update, CancellationToken ct)
        {
            if (update == null || update.Old == null) throw new ArgumentNullException(nameof(update));
            var records = Load(profile);
            var record = records.FirstOrDefault(x =>
                x.ProjectId == update.Old.ProjectId && x.Kind == update.Old.Kind && x.VersionId == update.Old.VersionId);
            if (record == null) throw new InvalidOperationException("资源登记已变化，请重新检查更新");
            string folder = Path.Combine(profile.Directory, Folder(record.Kind));
            string original = Path.Combine(folder, FileName(record.Filename));
            string destination = Path.Combine(folder, FileName(update.Filename));
            if (!File.Exists(original) || !FileSha1(original).Equals(record.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("旧文件被修改或丢失，拒绝覆盖");
            if (!string.Equals(original, destination, StringComparison.OrdinalIgnoreCase) && File.Exists(destination))
                throw new IOException("新资源与其他已有文件名称冲突");
            string staging = Path.Combine(profile.Directory, ".blockcraft-wpf-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string temp = Path.Combine(staging, update.Filename);
            try
            {
                using (var net = new NetworkDownloader(config))
                    await net.DownloadVerifiedAsync(update.Url, temp, update.Sha1, ct);
                ct.ThrowIfCancellationRequested();
                string backupDir = Path.Combine(profile.Directory, ".blockcraft-backups",
                    "wpf-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"), Folder(record.Kind));
                Directory.CreateDirectory(backupDir);
                string backup = Path.Combine(backupDir, record.Filename);
                File.Copy(original, backup, false);
                try
                {
                    if (string.Equals(original, destination, StringComparison.OrdinalIgnoreCase))
                        File.Replace(temp, original, null);
                    else
                    {
                        File.Move(temp, destination);
                        File.Delete(original);
                    }
                    record.VersionId = update.VersionId;
                    record.Filename = update.Filename;
                    record.Sha1 = update.Sha1.ToLowerInvariant();
                    Save(profile, records);
                }
                catch
                {
                    File.Copy(backup, original, true);
                    if (!string.Equals(original, destination, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(destination)) File.Delete(destination);
                    throw;
                }
                return destination;
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }
    }
}