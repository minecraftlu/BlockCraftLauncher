using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Optional public GitHub Releases provider for Minecraft Java mods absent
    /// from Modrinth. Never installs repository source ZIPs, executable setup
    /// programs, or unverified assets. Requires a recognized SPDX license,
    /// game-version hints, official release URL, GitHub SHA-256 digest and
    /// matching JAR mod-loader metadata.
    /// </summary>
    internal sealed class GitHubOpenSourceModClient : IDisposable
    {
        const int MaxJarBytes = 128 * 1024 * 1024;
        static readonly Regex RepoIdPattern = new Regex(
            @"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9_.-]{1,100}$",
            RegexOptions.CultureInvariant);
        static readonly HashSet<string> Licenses = new HashSet<string>(
            new[] { "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause",
                "ISC", "GPL-2.0", "GPL-3.0", "LGPL-2.1", "LGPL-3.0",
                "AGPL-3.0", "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only",
                "GPL-3.0-or-later", "LGPL-2.1-only", "LGPL-2.1-or-later",
                "LGPL-3.0-only", "LGPL-3.0-or-later", "AGPL-3.0-only",
                "AGPL-3.0-or-later", "MPL-2.0", "EUPL-1.2",
                "Unlicense", "CC0-1.0", "0BSD" }, StringComparer.OrdinalIgnoreCase);
        static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64)
            .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
        readonly NetworkDownloader metadata;
        readonly LauncherConfig settings;
        readonly JavaScriptSerializer json = new JavaScriptSerializer
            { MaxJsonLength = 12 * 1024 * 1024 };

        internal GitHubOpenSourceModClient(LauncherConfig settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            metadata = new NetworkDownloader(settings);
        }

        static Dictionary<string, object> Object(object value) =>
            value as Dictionary<string, object>;
        static string Text(IDictionary<string, object> obj, string field) =>
            obj != null && obj.TryGetValue(field, out var x) && x != null
                ? Convert.ToString(x) : "";
        static object[] List(IDictionary<string, object> obj, string field) =>
            obj != null && obj.TryGetValue(field, out var x) ? x as object[] : null;
        static bool IsOpenLicense(IDictionary<string, object> repo)
        {
            var license = Object(repo != null && repo.TryGetValue("license", out var value)
                ? value : null);
            return Licenses.Contains(Text(license, "spdx_id"));
        }
        static string RepoPath(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !RepoIdPattern.IsMatch(id) ||
                id.Contains("..") || id.EndsWith(".", StringComparison.Ordinal))
                throw new InvalidDataException("GitHub 开源模组仓库地址无效");
            return id;
        }

        internal static ModrinthSearchPage ParseSearch(string body)
        {
            var root = Object(new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                .DeserializeObject(body ?? ""));
            var rows = List(root, "items");
            if (rows == null || rows.Length > 50)
                throw new InvalidDataException("GitHub 项目搜索数据格式无效");
            var hits = new List<ResourceHit>();
            foreach (var entry in rows.Select(Object).Where(x => x != null))
            {
                if (!IsOpenLicense(entry) ||
                    string.Equals(Text(entry, "archived"), "True", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Text(entry, "fork"), "True", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Text(entry, "private"), "True", StringComparison.OrdinalIgnoreCase))
                    continue;
                string fullName = Text(entry, "full_name");
                try { RepoPath(fullName); }
                catch (InvalidDataException) { continue; }

                var owner = Object(entry.TryGetValue("owner", out var author) ? author : null);
                var license = Object(entry.TryGetValue("license", out var raw) ? raw : null);
                hits.Add(new ResourceHit {
                    ProjectId = fullName,
                    Name = Text(entry, "name"),
                    Description = Text(entry, "description"),
                    Kind = "mod",
                    Source = "GitHub",
                    Author = Text(owner, "login"),
                    DownloadCount = -1,
                    DownloadsLabel = "GitHub 仓库 · 不代表提供可安装文件",
                    LoaderLabel = "安装时验证 JAR 加载器",
                    VersionsLabel = "仅兼容版本的 Releases 可直装",
                    CategoryLabel = "开源许可 · " + Text(license, "spdx_id"),
                    UpdatedLabel = "GitHub Releases"
                });
            }
            int total = hits.Count;
            if (int.TryParse(Text(root, "total_count"), out var published))
                total = Math.Max(total, published);
            return new ModrinthSearchPage { Hits = hits, TotalHits = total };
        }

        internal async Task<ModrinthSearchPage> SearchAsync(string query,
            int page, CancellationToken ct)
        {
            query = (query ?? "").Trim();
            if (query.Length > 80 || query.Any(char.IsControl) ||
                page < 0 || page > 50)
                throw new ArgumentException("GitHub 开源模组搜索条件不合法");
            string q = "minecraft mod " + query + " in:name,description,topics fork:false archived:false";
            string url = "https://api.github.com/search/repositories?q=" +
                Uri.EscapeDataString(q) + "&per_page=40&page=" + (page + 1);
            return await metadata.GetMetadataWithFallbackAsync(url, "official",
                ParseSearch, ct).ConfigureAwait(false);
        }

        internal static bool CompatibleVersion(string value, string gameVersion)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(gameVersion) ||
                gameVersion.Length > 50) return false;
            return Regex.IsMatch(value, @"(^|[^0-9.])" +
                Regex.Escape(gameVersion) + @"($|[^0-9.])", RegexOptions.IgnoreCase);
        }

        static bool HasLoaderHint(string filename, string loader)
        {
            return Regex.IsMatch(filename,
                @"(?<![a-z])(fabric|forge|neoforge|quilt)(?![a-z])",
                RegexOptions.IgnoreCase) &&
                Regex.IsMatch(filename, @"(?<![a-z])" +
                    Regex.Escape(loader) + @"(?![a-z])", RegexOptions.IgnoreCase);
        }

        static bool HasOtherLoaderHint(string filename, string loader)
        {
            foreach (string other in new[] { "fabric", "forge", "neoforge", "quilt" })
                if (!string.Equals(other, loader, StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(filename, @"(?<![a-z])" + other +
                        @"(?![a-z])", RegexOptions.IgnoreCase))
                    return true;
            return false;
        }

        internal sealed class ReleaseJar
        {
            internal string Filename;
            internal string DownloadUrl;
            internal string Sha256;
            internal long Size;
        }

        internal static ReleaseJar ChooseReleaseJar(
            string repository, string gameVersion, string loader, string jsonBody)
        {
            string id = RepoPath(repository);
            var release = Object(new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                .DeserializeObject(jsonBody ?? ""));
            if (release == null ||
                string.Equals(Text(release, "draft"), "True", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Text(release, "prerelease"), "True", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub 没有可用的公开稳定发布版本");
            string tag = Text(release, "tag_name");
            string releaseName = Text(release, "name");
            var assets = List(release, "assets");
            if (assets == null || assets.Length > 100)
                throw new InvalidDataException("GitHub Releases 文件清单无效");
            var candidates = new List<ReleaseJar>();
            foreach (var a in assets.Select(Object).Where(x => x != null))
            {
                string name = Text(a, "name");
                // Release titles/tags can describe a different Minecraft
                // version than an individual asset. If the JAR filename
                // explicitly contains Minecraft-looking versions, those must
                // all agree with the selected profile before title fallback.
                var declaredVersions = Regex.Matches(name,
                    @"(?<![0-9.])1\.(?:[7-9]|1[0-9]|2[0-9])(?:\.[0-9]{1,3})?(?![0-9.])")
                    .Cast<Match>().Select(x => x.Value).Distinct().ToArray();
                if (declaredVersions.Length > 0 &&
                    declaredVersions.Any(x => !string.Equals(x,
                        gameVersion, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (name.Length < 5 || name.Length > 180 ||
                    name != Path.GetFileName(name) ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.Contains("/") || name.Contains("\\") ||
                    !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    Regex.IsMatch(name, @"[-_.](sources|javadoc|dev|api)[-_.]jar$",
                        RegexOptions.IgnoreCase) ||
                    HasOtherLoaderHint(name, loader) ||
                    (!CompatibleVersion(name, gameVersion) &&
                     !CompatibleVersion(tag, gameVersion) &&
                     !CompatibleVersion(releaseName, gameVersion)))
                    continue;

                string url = Text(a, "browser_download_url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
                    uri.Host != "github.com" || uri.UserInfo.Length != 0 ||
                    uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                    !uri.AbsolutePath.StartsWith("/" + id + "/releases/download/",
                        StringComparison.OrdinalIgnoreCase) ||
                    Uri.UnescapeDataString(uri.Segments.Last()) != name)
                    continue;
                string digest = Text(a, "digest");
                if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                    digest.Length != 71 ||
                    !digest.Substring(7).All(Uri.IsHexDigit))
                    continue;
                if (!long.TryParse(Text(a, "size"), out var bytes) ||
                    bytes < 128 || bytes > MaxJarBytes) continue;
                candidates.Add(new ReleaseJar {
                    Filename = name, DownloadUrl = url,
                    Sha256 = digest.Substring(7).ToLowerInvariant(), Size = bytes
                });
            }
            // Prefer an explicitly loader-labelled archive over a generic jar.
            // Ambiguous release layouts require manual inspection, never a
            // guessed "first file" (often dev/source or wrong loader).
            var matchingLoader = candidates.Where(x =>
                HasLoaderHint(x.Filename, loader)).ToArray();
            var available = matchingLoader.Length > 0 ? matchingLoader : candidates.ToArray();
            if (available.Length != 1)
                throw new InvalidDataException(available.Length == 0
                    ? "此开源项目没有匹配游戏版本、SHA-256 和可安装 JAR 的 Releases 文件；请打开项目页手动查看"
                    : "该开源项目存在多个可能的 JAR，不能安全猜测版本；请打开 GitHub Releases 手动选择");
            return available[0];
        }

        /// <summary>
        /// GitHub's "latest" Release only follows the newest project version,
        /// which often excludes users on older Minecraft versions. Traverse
        /// a bounded page of published releases in server order instead.
        /// Never infer an artifact from a tag or use repository source ZIPs.
        /// </summary>
        internal static ReleaseJar ChooseFromReleaseList(string repository,
            string gameVersion, string loader, string payload)
        {
            var rows = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                .DeserializeObject(payload ?? "") as object[];
            if (rows == null || rows.Length > 40)
                throw new InvalidDataException("GitHub Releases 列表格式无效");
            foreach (var item in rows)
            {
                if (!(item is Dictionary<string, object> release)) continue;
                if (string.Equals(Text(release, "draft"), "True",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Text(release, "prerelease"), "True",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    return ChooseReleaseJar(repository, gameVersion, loader,
                        new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                            .Serialize(release));
                }
                catch (InvalidDataException)
                {
                    // A newer project release may target a different Minecraft
                    // version, contain only sources or lack a GitHub digest.
                    // Keep searching older validated releases.
                }
            }
            return null;
        }

        internal static void ValidateJar(string file, string loader)
        {
            using (var zip = ZipFile.OpenRead(file))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > 50000)
                    throw new InvalidDataException("GitHub JAR 文件目录异常");
                bool has(string path) => zip.Entries.Any(x =>
                    string.Equals(x.FullName, path, StringComparison.OrdinalIgnoreCase));
                bool valid = string.Equals(loader, "fabric", StringComparison.OrdinalIgnoreCase)
                    ? has("fabric.mod.json")
                    : string.Equals(loader, "quilt", StringComparison.OrdinalIgnoreCase)
                    ? has("quilt.mod.json") || has("fabric.mod.json")
                    : string.Equals(loader, "neoforge", StringComparison.OrdinalIgnoreCase)
                    ? has("META-INF/neoforge.mods.toml") || has("META-INF/mods.toml")
                    : string.Equals(loader, "forge", StringComparison.OrdinalIgnoreCase) &&
                      (has("META-INF/mods.toml") || has("mcmod.info"));
                if (!valid)
                    throw new InvalidDataException("JAR 未声明与当前模组加载器匹配的元数据；未安装");
            }
        }

        internal async Task<string> DownloadAsync(ResourceHit hit,
            GameProfile profile, CancellationToken ct, IProgress<DownloadBytes> progress = null)
        {
            if (hit == null || hit.Source != "GitHub" || profile == null ||
                string.IsNullOrWhiteSpace(profile.Directory))
                throw new ArgumentException("请先选择 GitHub 开源模组和游戏实例");
            string repository = RepoPath(hit.ProjectId);
            string loader = (profile.Loader ?? "").ToLowerInvariant();
            if (!new[] { "fabric", "forge", "neoforge", "quilt" }.Contains(loader) ||
                string.IsNullOrWhiteSpace(profile.BaseVersion))
                throw new InvalidDataException("请先选择有受支持加载器的 Minecraft Java 实例");
            string repoUrl = "https://api.github.com/repos/" + repository;
            var repo = await metadata.GetMetadataWithFallbackAsync(repoUrl, "official",
                body => Object(json.DeserializeObject(body)) ??
                    throw new InvalidDataException("GitHub 仓库数据无效"), ct);
            if (!string.Equals(Text(repo, "full_name"), repository,
                    StringComparison.OrdinalIgnoreCase) || !IsOpenLicense(repo) ||
                string.Equals(Text(repo, "archived"), "True", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Text(repo, "private"), "True", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("仓库已改变、私有或缺少可识别的开源许可证，不能自动安装");
            // Two bounded pages (at most 60 releases) cover an older
            // supported Minecraft branch without exhausting GitHub's
            // unauthenticated API quota. Each returned JAR must still carry
            // a SHA-256 digest and match the selected loader.
            ReleaseJar asset = null;
            for (int page = 1; page <= 2 && asset == null; page++)
            {
                ct.ThrowIfCancellationRequested();
                string releasesUrl = repoUrl + "/releases?per_page=30&page=" + page;
                var releases = await metadata.GetMetadataWithFallbackAsync(releasesUrl,
                    "official", body => body, ct);
                asset = ChooseFromReleaseList(repository, profile.BaseVersion,
                    loader, releases);
                if (asset != null) break;
                // Do not fetch extra pages for empty or short first pages.
                var rows = json.DeserializeObject(releases) as object[];
                if (rows == null || rows.Length < 30) break;
            }
            if (asset == null)
                throw new InvalidDataException("GitHub 最近 60 个公开 Release 中没有符合 Minecraft 版本、加载器、SHA-256 的唯一可安装 JAR；请查看项目发布页");

            string baseDir = Path.GetFullPath(profile.Directory);
            string mods = Path.Combine(baseDir, "mods");
            string target = Path.Combine(mods, asset.Filename);
            var gate = Gates[(StringComparer.OrdinalIgnoreCase.GetHashCode(baseDir) & int.MaxValue) % Gates.Length];
            await gate.WaitAsync(ct).ConfigureAwait(false);
            string temporary = null;
            try
            {
                Directory.CreateDirectory(mods);
                if (File.Exists(target))
                    throw new IOException("已有同名模组，拒绝覆盖：" + asset.Filename);
                temporary = Path.Combine(mods, ".blockcraft-github-" +
                    Guid.NewGuid().ToString("N") + ".part");
                using (var client = new HttpClient(NetworkDownloader.CreateHttpHandler(settings))
                    { Timeout = TimeSpan.FromMinutes(6) })
                using (var response = await client.GetAsync(asset.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > MaxJarBytes)
                        throw new InvalidDataException("模组超过下载大小限制");
                    using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(temporary, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None, 128 * 1024, true))
                    {
                        var buffer = new byte[128 * 1024];
                        long received = 0;
                        int bytes;
                        while ((bytes = await source.ReadAsync(buffer, 0, buffer.Length, ct)
                            .ConfigureAwait(false)) != 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            received += bytes;
                            if (received > MaxJarBytes)
                                throw new InvalidDataException("模组超过下载大小限制");
                            await output.WriteAsync(buffer, 0, bytes, ct).ConfigureAwait(false);
                            progress?.Report(new DownloadBytes(received, asset.Size));
                        }
                        if (received != asset.Size)
                            throw new IOException("GitHub Releases 下载文件大小与发布元数据不符");
                    }
                }
                string actual;
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(temporary))
                    actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "")
                        .ToLowerInvariant();
                if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("GitHub Releases SHA-256 校验失败；已拒绝安装");
                ValidateJar(temporary, loader);
                ct.ThrowIfCancellationRequested();
                if (File.Exists(target))
                    throw new IOException("另一个模组已占用文件名：" + asset.Filename);
                File.Move(temporary, target);
                temporary = null;
                return target;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                gate.Release();
            }
        }

        public void Dispose() => metadata.Dispose();
    }
}
