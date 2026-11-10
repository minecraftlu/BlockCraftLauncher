using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;
using CmlLib.Core.Installer.Forge.Versions;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class LoaderNetworkCatalog : IForgeVersionLoader, IDisposable
    {
        const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
        const string NeoForgeMetadata =
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";

        readonly NetworkDownloader network;
        readonly ForgeVersionLoader officialForge;
        readonly string mode, requested;
        readonly CancellationToken token;

        public LoaderNetworkCatalog(LauncherConfig settings, HttpClient client,
            CancellationToken token, string requested)
        {
            network = new NetworkDownloader(settings);
            officialForge = new ForgeVersionLoader(client);
            mode = NetworkDownloader.NormalizeMode(settings.NetworkMode);
            this.requested = requested;
            this.token = token;
        }

        internal static bool ValidVersionId(string value) =>
            !string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._+\-]{0,89}\z") &&
            !value.EndsWith(".", StringComparison.Ordinal);

        public async Task<IEnumerable<ForgeVersion>> GetForgeVersions(string minecraftVersion)
        {
            token.ThrowIfCancellationRequested();
            Exception last = null;
            var sources = mode == "official" ? new[] { false } : mode == "china" ?
                new[] { true, false } : new[] { false, true };
            foreach (bool mirror in sources)
            {
                try
                {
                    var list = mirror ? await network.GetMetadataWithFallbackAsync(
                            "https://bmclapi2.bangbang93.com/forge/minecraft/" +
                            Uri.EscapeDataString(minecraftVersion.Replace('-', '_')), "official",
                            body => ParseForgeMirror(body, minecraftVersion), token).ConfigureAwait(false)
                        : (await officialForge.GetForgeVersions(minecraftVersion).ConfigureAwait(false)).ToArray();
                    token.ThrowIfCancellationRequested();
                    if (list.Length > 0 &&
                        (string.IsNullOrWhiteSpace(requested) || list.Any(x => x.ForgeVersionName == requested)))
                        return list;
                    last = new InvalidDataException("来源未列出所选 Forge 版本");
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException ||
                    ex is InvalidDataException || ex is ArgumentException ||
                    ex is OperationCanceledException || ex is InvalidOperationException)
                {
                    token.ThrowIfCancellationRequested();
                    last = ex;
                }
            }
            throw new IOException("无法获取 Minecraft " + minecraftVersion + " 的 Forge 版本。请检查网络或选择支持 Forge 的游戏版本。", last);
        }

        internal static ForgeVersion[] ParseForgeMirror(string data, string minecraftVersion)
        {
            var rows = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                .DeserializeObject(data) as object[];
            if (rows == null) throw new InvalidDataException("BMCLAPI Forge 版本列表格式不正确");
            var result = new List<ForgeVersion>();
            foreach (var entry in rows.OfType<Dictionary<string, object>>())
            {
                string version = Text(entry, "version");
                string game = Text(entry, "mcversion");
                string branch = Text(entry, "branch");
                if (!ValidVersionId(version) || game.Replace('_', '-') != minecraftVersion ||
                    (branch.Length > 0 && !ValidVersionId(branch)) ||
                    !entry.TryGetValue("files", out var rawFiles) || !(rawFiles is object[] files))
                    continue;

                string coordinate = minecraftVersion + "-" + version + (branch.Length == 0 ? "" : "-" + branch);
                var artifacts = new List<ForgeVersionFile>();
                foreach (var file in files.OfType<Dictionary<string, object>>())
                {
                    string category = Text(file, "category");
                    string format = Text(file, "format");
                    if (category == "installer" ? format != "jar" :
                        (category != "universal" && category != "client") || (format != "jar" && format != "zip"))
                        continue;

                    string hash = Text(file, "hash");
                    artifacts.Add(new ForgeVersionFile
                    {
                        Type = category,
                        SHA1 = NetworkDownloader.ValidSha1(hash) ? hash : null,
                        MD5 = hash.Length == 32 && hash.All(Uri.IsHexDigit) ? hash : null,
                        DirectUrl = ForgeMaven + coordinate + "/forge-" + coordinate + "-" + category + "." + format
                    });
                }
                if (artifacts.Count == 0) continue;
                result.Add(new ForgeVersion(minecraftVersion, version)
                    { Files = artifacts, Time = Text(entry, "modified") });
            }
            return result.OrderByDescending(x => x.Time, StringComparer.Ordinal).ToArray();
        }

        public async Task<NeoForgeVersion> FindNeoForgeVersionAsync(string minecraftVersion)
        {
            token.ThrowIfCancellationRequested();
            string expected = NeoForgeVersionMapper.NormalizeMinecraftVersion(minecraftVersion);
            if (!string.IsNullOrWhiteSpace(requested))
            {
                var pinned = NeoForgeVersionMapper.MapVersion(requested);
                if (pinned.MinecraftVersionName != expected)
                    throw new InvalidDataException("整合包指定的 NeoForge 与 Minecraft 版本不兼容。");
                return pinned;
            }
            return await network.GetMetadataWithFallbackAsync(NeoForgeMetadata, mode,
                data => ParseNeoForgeMetadata(data, expected), token).ConfigureAwait(false);
        }

        internal static NeoForgeVersion ParseNeoForgeMetadata(string data, string expectedGame)
        {
            using var text = new StringReader(data);
            using var xml = XmlReader.Create(text, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(xml);
            foreach (var node in document.Root?.Element("versioning")?.Element("versions")
                ?.Elements("version").Reverse() ?? Enumerable.Empty<XElement>())
            {
                string version = node.Value.Trim();
                if (!ValidVersionId(version)) continue;
                NeoForgeVersion candidate;
                try { candidate = NeoForgeVersionMapper.MapVersion(version); }
                catch (FormatException) { continue; }
                if (candidate.MinecraftVersionName == expectedGame) return candidate;
            }
            throw new InvalidDataException("没有找到适用于 Minecraft " + expectedGame + " 的 NeoForge。");
        }

        static string Text(IDictionary<string, object> row, string key) =>
            row.TryGetValue(key, out var value) ? Convert.ToString(value) ?? "" : "";

        public void Dispose() => network.Dispose();
    }
}
