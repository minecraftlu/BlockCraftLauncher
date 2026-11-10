using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class MinecraftVersionEntry
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public DateTime Released { get; set; }
        public string Category
        {
            get
            {
                switch (Type)
                {
                    case "release": return "正式版";
                    case "snapshot": return "快照版";
                    case "old_beta": return "远古 Beta";
                    case "old_alpha": return "远古 Alpha";
                    default: return "其他";
                }
            }
        }
        public override string ToString() =>
            Id + "    " + Category + (Released == DateTime.MinValue ? "" : "    " + Released.ToString("yyyy-MM-dd"));
    }

    /// <summary>
    /// Authoritative Mojang version manifest, with guarded local cache and
    /// BMCLAPI fallback only for the known Mojang metadata endpoint.
    /// </summary>
    public sealed class MinecraftVersionCatalog
    {
        internal const string Official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        readonly LauncherConfig settings;
        readonly string cache;
        public bool UsingCachedManifest { get; private set; }

        public MinecraftVersionCatalog(LauncherConfig settings, string cachePath = null)
        {
            this.settings = settings;
            cache = cachePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "downloads", "version-manifest-v2.json");
        }

        static string Field(Dictionary<string, object> item, string name)
            => item.TryGetValue(name, out var value) && value != null ? Convert.ToString(value) : "";

        public static IReadOnlyList<MinecraftVersionEntry> Parse(string manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest) || manifest.Length > 10 * 1024 * 1024)
                throw new InvalidDataException("Minecraft 版本索引为空或过大");
            var parsed = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }.DeserializeObject(manifest)
                as Dictionary<string, object>;
            if (parsed == null || !parsed.TryGetValue("versions", out var value) ||
                !(value is object[] items) || items.Length == 0 || items.Length > 10000)
                throw new InvalidDataException("Mojang 版本索引格式不正确");
            var output = new List<MinecraftVersionEntry>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in items)
            {
                if (!(raw is Dictionary<string, object> item)) continue;
                string id = Field(item, "id");
                string type = Field(item, "type");
                if (id.Length == 0 || id.Length > 120 || id.Any(char.IsControl) ||
                    id.IndexOfAny(new[] { '/', '\\', ':', '<', '>', '?' }) >= 0 ||
                    !new[] { "release", "snapshot", "old_beta", "old_alpha" }.Contains(type) ||
                    !ids.Add(id)) continue;
                DateTime release;
                if (!DateTime.TryParse(Field(item, "releaseTime"), out release))
                    release = DateTime.MinValue;
                output.Add(new MinecraftVersionEntry { Id = id, Type = type, Released = release });
            }
            if (output.Count == 0)
                throw new InvalidDataException("Minecraft 版本索引没有可用记录");
            return output.OrderByDescending(x => x.Released).ToList();
        }

        public async Task<IReadOnlyList<MinecraftVersionEntry>> LoadAsync(bool forceRefresh, CancellationToken ct)
        {
            UsingCachedManifest = false;
            ct.ThrowIfCancellationRequested();
            if (!forceRefresh && TryReadCache(out var current, TimeSpan.FromMinutes(30)))
            {
                UsingCachedManifest = true;
                return current;
            }

            try
            {
                using (var net = new NetworkDownloader(settings))
                {
                    var result = await net.GetMetadataWithFallbackAsync(Official, settings.NetworkMode,
                        data => (Raw: data, Versions: Parse(data)), ct);
                    SaveCache(result.Raw);
                    return result.Versions;
                }
            }
            catch (IOException) when (!ct.IsCancellationRequested)
            {
                if (!TryReadCache(out var cached)) throw;
                UsingCachedManifest = true;
                return cached;
            }
        }

        bool TryReadCache(out IReadOnlyList<MinecraftVersionEntry> versions, TimeSpan? maxAge = null)
        {
            versions = null;
            try
            {
                if (!File.Exists(cache) || (maxAge.HasValue &&
                    DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) > maxAge.Value))
                    return false;
                versions = Parse(File.ReadAllText(cache, Encoding.UTF8));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException ||
                ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return false;
            }
        }

        void SaveCache(string data)
        {
            string fullPath = Path.GetFullPath(cache);
            string temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllText(temp, data, new UTF8Encoding(false));
                if (File.Exists(fullPath)) File.Replace(temp, fullPath, null);
                else File.Move(temp, fullPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A read-only cache must not discard an otherwise valid live list.
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
