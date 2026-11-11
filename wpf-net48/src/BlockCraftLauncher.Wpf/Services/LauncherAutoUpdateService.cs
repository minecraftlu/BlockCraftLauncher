using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    public sealed class LauncherUpdateOffer
    {
        public string Tag { get; internal set; }
        public string Description { get; internal set; }
        public string ReleaseUrl { get; internal set; }
        public string AssetName { get; internal set; }
        public string AssetUrl { get; internal set; }
        public string ManifestUrl { get; internal set; }
        public string Mode { get; internal set; }
        public long AssetSize { get; internal set; }
    }

    public sealed class LauncherUpdateProgress
    {
        public long Completed { get; internal set; }
        public long Total { get; internal set; }
        public string Message { get; internal set; }
    }

    public sealed class PreparedLauncherUpdate
    {
        public LauncherUpdateOffer Offer { get; internal set; }
        public string StagedPath { get; internal set; }
        public string Sha256 { get; internal set; }
        public string TargetPath { get; internal set; }
        public string UpdatesRoot { get; internal set; }
    }

    /// <summary>
    /// Fetch, stage and verify GitHub WPF Release assets. The main app NEVER
    /// replaces its own running files; a tiny external helper waits for exit,
    /// smoke-tests the replacement and rolls back on failure.
    /// </summary>
    public sealed class LauncherAutoUpdateService
    {
        internal const string CurrentTag = "v0.9.46";
        internal static string BuildChannelLabel => CurrentTag.Contains("-wpf-rc.") ? "预览版" : "正式版";
        internal const string ReleasesApi =
            "https://api.github.com/repos/minecraftlu/BlockCraftLauncher/releases?per_page=100";
        internal const string OfficialReleaseRoot =
            "https://github.com/minecraftlu/BlockCraftLauncher/releases/";
        const string UpdaterFile = "BlockCraftLauncher.Updater.exe";
        const string MainFile = "BlockCraftLauncher.Wpf.exe";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = 12 * 1024 * 1024
        };
        readonly LauncherConfig settings;
        string installedTarget;

        public LauncherAutoUpdateService(LauncherConfig settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            // Self-contained updates use one transient helper EXE. Once the
            // helper has exited, prune only old launcher-owned working folders.
            // No game instance, world, mod or user-selected download is scanned.
            TryCleanupStaleUpdateFolders(DownloadPathService.UpdatesRoot(null), false);
            // New user-selected download folders are safe cleanup candidates
            // only when a launcher-generated marker exists in the stage.
            string custom = DownloadPathService.UpdatesRoot(settings.DownloadDirectory);
            if (!string.Equals(custom, DownloadPathService.UpdatesRoot(null),
                StringComparison.OrdinalIgnoreCase))
                TryCleanupStaleUpdateFolders(custom, true);
            TryCleanupLegacyExeBackups();
        }

        static void TryCleanupLegacyExeBackups()
        {
            // Older versions of the updater left versioned rollback EXEs
            // beside the originally launched OneFile. After 48 hours, a
            // healthy newly launched app can prune only its own older copies.
            // This does NOT scan arbitrary Downloads or delete user EXEs.
            try
            {
                string bootstrap = Environment.GetEnvironmentVariable(
                    "BLOCKCRAFT_SINGLEFILE_BOOTSTRAP");
                if (!IsOneFileEnvironment(AppDomain.CurrentDomain.BaseDirectory, bootstrap))
                    return;
                string full = Path.GetFullPath(bootstrap);
                string dir = Path.GetDirectoryName(full);
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    return;
                DateTime cutoff = DateTime.UtcNow.AddDays(-2);
                foreach (string entry in Directory.GetFiles(dir,
                    Path.GetFileName(full) + ".blockcraft-previous-*.exe"))
                {
                    try
                    {
                        string name = Path.GetFileName(entry);
                        string pattern = "^" + Regex.Escape(Path.GetFileName(full)) +
                            @"\.blockcraft-previous-[0-9a-f]{32}\.exe$";
                        if (Regex.IsMatch(name, pattern, RegexOptions.IgnoreCase) &&
                            (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0 &&
                            File.GetLastWriteTimeUtc(entry) < cutoff)
                            File.Delete(entry);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        internal static bool IsOwnedUpdateWorkFolder(string updateRoot, string candidate)
        {
            if (string.IsNullOrWhiteSpace(updateRoot) ||
                string.IsNullOrWhiteSpace(candidate)) return false;
            string root = Path.GetFullPath(updateRoot).TrimEnd('\\');
            string full = Path.GetFullPath(candidate).TrimEnd('\\');
            return string.Equals(Path.GetDirectoryName(full), root,
                StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(full),
                    @"^(staged|runner)-[a-f0-9]{32}$", RegexOptions.IgnoreCase);
        }

        static bool AnyProcessUsingDirectory(string directory)
        {
            foreach (string name in new[] {
                "BlockCraftLauncher.Wpf", "BlockCraftLauncher.Updater" })
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            string executable = process.MainModule.FileName;
                            if (executable.StartsWith(directory.TrimEnd('\\') + "\\",
                                StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        catch (System.ComponentModel.Win32Exception) { return true; }
                        catch (InvalidOperationException) { return true; }
                    }
                }
            return false;
        }

        static bool HasLinkedOrUnexpectedChildren(string root)
        {
            // Inspect only real directories; do not recursively enumerate
            // through reparse points, which could lead outside update storage.
            var pending = new Stack<string>();
            pending.Push(root);
            int checkedEntries = 0;
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string item in Directory.GetDirectories(directory))
                {
                    if (++checkedEntries > 2048 ||
                        (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                        return true;
                    pending.Push(item);
                }
                foreach (string item in Directory.GetFiles(directory))
                    if (++checkedEntries > 2048 ||
                        (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                        return true;
            }
            return false;
        }

        static void TryCleanupStaleUpdateFolders(string root, bool requireMarker)
        {
            try
            {
                if (!Directory.Exists(root) ||
                    (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    return;
                DateTime cutoff = DateTime.UtcNow.AddDays(-2);
                foreach (string folder in Directory.GetDirectories(root))
                {
                    try
                    {
                        if (!IsOwnedUpdateWorkFolder(root, folder) ||
                            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
                            (requireMarker && !File.Exists(Path.Combine(folder,
                                ".blockcraft-stage"))) ||
                            Directory.GetLastWriteTimeUtc(folder) >= cutoff ||
                            AnyProcessUsingDirectory(folder))
                            continue;
                        if (HasLinkedOrUnexpectedChildren(folder)) continue;
                        Directory.Delete(folder, true);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool IsOneFileEnvironment(string baseDir, string bootstrap)
        {
            if (string.IsNullOrWhiteSpace(bootstrap) || !File.Exists(bootstrap) ||
                !bootstrap.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string root = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher", "onefile");
            string full = Path.GetFullPath(baseDir);
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\",
                StringComparison.OrdinalIgnoreCase)) return false;
            return File.Exists(Path.Combine(full, ".blockcraft-ready"));
        }

        public static string InstallMode
        {
            get
            {
                var bootstrap = Environment.GetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP");
                return IsOneFileEnvironment(AppDomain.CurrentDomain.BaseDirectory, bootstrap) ?
                    "onefile" : "zip";
            }
        }

        static Dictionary<string, object> Dict(object item) =>
            item as Dictionary<string, object>;

        static string S(Dictionary<string, object> obj, string key) =>
            obj != null && obj.TryGetValue(key, out var value) && value != null ?
                Convert.ToString(value) ?? "" : "";

        static bool B(Dictionary<string, object> obj, string key) =>
            obj != null && obj.TryGetValue(key, out var value) &&
                value is bool flag && flag;

        static long Number(Dictionary<string, object> obj, string key)
        {
            return long.TryParse(S(obj, key), out var result) ? result : 0;
        }

        internal static int CompareTags(string left, string right)
        {
            var pattern = @"^v(\d+)\.(\d+)\.(\d+)(?:-wpf-rc\.(\d+))?$";
            var a = Regex.Match(left ?? "", pattern, RegexOptions.IgnoreCase);
            var b = Regex.Match(right ?? "", pattern, RegexOptions.IgnoreCase);
            if (!a.Success || !b.Success) throw new ArgumentException("无效的 WPF 版本号");
            for (int i = 1; i <= 3; i++)
            {
                if (!int.TryParse(a.Groups[i].Value, out var x) ||
                    !int.TryParse(b.Groups[i].Value, out var y))
                    throw new ArgumentException("版本号超出范围");
                if (x != y) return x.CompareTo(y);
            }
            // Official Stable is newer than RC of the same numerical version.
            bool ap = a.Groups[4].Success, bp = b.Groups[4].Success;
            if (ap != bp) return ap ? -1 : 1;
            if (!ap) return 0;
            if (!int.TryParse(a.Groups[4].Value, out var an) ||
                !int.TryParse(b.Groups[4].Value, out var bn))
                throw new ArgumentException("RC 版本号超出范围");
            return an.CompareTo(bn);
        }

        static bool ExactGithubReleaseAsset(string url, string tag, string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('/') >= 0 ||
                name.IndexOf('\\') >= 0 || name.Length > 180)
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 ||
                !uri.IsDefaultPort || uri.Fragment.Length > 0 ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return false;
            string expected = "/minecraftlu/BlockCraftLauncher/releases/download/" +
                Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
            return string.Equals(uri.AbsolutePath, expected, StringComparison.Ordinal);
        }

        static bool ExactReleasePage(string url, string tag) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.UserInfo.Length == 0 && uri.IsDefaultPort &&
            uri.Fragment.Length == 0 &&
            string.Equals(uri.AbsolutePath,
                "/minecraftlu/BlockCraftLauncher/releases/tag/" + tag,
                StringComparison.Ordinal);

        static string ExpectedAsset(string tag, string mode)
        {
            string version = tag.Split('-')[0]; // v0.9.37-wpf-rc.5 -> v0.9.37
            if (mode == "onefile")
                return "BlockCraftLauncher-" + version + "-WPF-OneFile-Windows-x64.exe";
            if (mode == "zip")
                return "BlockCraftLauncher-" + version + (tag.Contains("-wpf-rc.") ?
                    "-WPF-net48-Alpha-Windows-x64.zip" :
                    "-WPF-net48-Windows-x64.zip");
            throw new ArgumentException("未知 WPF 打包格式");
        }

        public static LauncherUpdateOffer SelectOffer(string json, string channel,
            string mode, string installedTag = CurrentTag)
        {
            if (channel != "stable" && channel != "preview")
                throw new ArgumentException("未知更新通道");
            var releases = Json.DeserializeObject(json) as object[]
                ?? throw new InvalidDataException("GitHub Releases 数据不是数组");
            LauncherUpdateOffer newest = null;
            foreach (var raw in releases)
            {
                var item = Dict(raw);
                if (item == null || B(item, "draft")) continue;
                string tag = S(item, "tag_name");
                bool rc = Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+-wpf-rc\.\d+$");
                bool stable = Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$");
                if (channel == "stable" ? (!stable || B(item, "prerelease")) :
                    (!rc || !B(item, "prerelease"))) continue;
                try { if (CompareTags(tag, installedTag) <= 0) continue; }
                catch (ArgumentException) { continue; }
                if (newest != null && CompareTags(tag, newest.Tag) <= 0) continue;
                string url = S(item, "html_url");
                if (!ExactReleasePage(url, tag)) continue;

                string name = ExpectedAsset(tag, mode);
                string manifest = "RELEASE-SHA256SUMS.txt";
                string assetUrl = null, manifestUrl = null;
                long size = 0;
                if (item.TryGetValue("assets", out var list) && list is object[] files)
                {
                    foreach (var entry in files.Select(Dict).Where(x => x != null))
                    {
                        string fileName = S(entry, "name");
                        string link = S(entry, "browser_download_url");
                        if (!ExactGithubReleaseAsset(link, tag, fileName)) continue;
                        if (fileName == name)
                        {
                            long length = Number(entry, "size");
                            if (length < 100000 || length > 256L * 1024 * 1024) continue;
                            size = length;
                            assetUrl = link;
                        }
                        else if (fileName == manifest) manifestUrl = link;
                    }
                }
                if (assetUrl == null || manifestUrl == null) continue;
                string description = S(item, "body");
                newest = new LauncherUpdateOffer
                {
                    Tag = tag, ReleaseUrl = url, AssetName = name, AssetUrl = assetUrl,
                    ManifestUrl = manifestUrl, AssetSize = size, Mode = mode,
                    Description = description.Length > 2000 ?
                        description.Substring(0, 2000) : description
                };
            }
            return newest;
        }

        public Task<LauncherUpdateOffer> CheckAsync(CancellationToken ct) => CheckAsync(ct, refresh: false);

        internal async Task<LauncherUpdateOffer> CheckAsync(CancellationToken ct, bool refresh)
        {
            string channel = settings.UpdateChannel;
            using (var network = new NetworkDownloader(settings))
                return await network.GetMetadataWithFallbackAsync(ReleasesApi, "official",
                    body => SelectOffer(body, channel, InstallMode), ct, refresh).ConfigureAwait(false);
        }

        internal static string ChecksumFromManifest(string content, string assetName)
        {
            if (content == null || content.Length > 32768)
                throw new InvalidDataException("更新校验清单过大或缺失");
            string sha = null;
            foreach (var line in content.Split(new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ') continue;
                if (!string.Equals(line.Substring(66).Trim(), assetName, StringComparison.Ordinal))
                    continue;
                string candidate = line.Substring(0, 64);
                if (candidate.Any(c => !Uri.IsHexDigit(c)) || sha != null)
                    throw new InvalidDataException("更新校验清单有重复或非法 SHA-256");
                sha = candidate.ToLowerInvariant();
            }
            return sha ?? throw new InvalidDataException("GitHub 发布页没有目标安装包的 SHA-256");
        }

        static string Sha256(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream))
                    .Replace("-", "").ToLowerInvariant();
        }

        internal static void VerifyFile(string file, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected) || expected.Length != 64 ||
                expected.Any(c => !Uri.IsHexDigit(c)) || !File.Exists(file) ||
                !string.Equals(Sha256(file), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件 SHA-256 不匹配，未执行更新");
        }

        static bool TrustedRedirect(Uri uri)
        {
            if (uri == null || uri.Scheme != Uri.UriSchemeHttps ||
                uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Fragment.Length > 0)
                return false;
            return new[] { "github.com", "release-assets.githubusercontent.com",
                "objects.githubusercontent.com", "github-releases.githubusercontent.com" }
                .Contains(uri.Host.ToLowerInvariant());
        }

        async Task DownloadOfficialAsync(string url, string destination,
            long maxBytes, CancellationToken ct, IProgress<LauncherUpdateProgress> progress)
        {
            using (var handler = NetworkDownloader.CreateHttpHandler(settings, allowRedirects: false))
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(12) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF-Update/0.9.36");
                Uri current = new Uri(url);
                if (!TrustedRedirect(current) ||
                    !current.AbsolutePath.StartsWith(
                        "/minecraftlu/BlockCraftLauncher/releases/download/", StringComparison.Ordinal))
                    throw new InvalidDataException("更新只能下载官方 GitHub Release 资产");

                for (int redirects = 0; redirects < 5; redirects++)
                {
                    ct.ThrowIfCancellationRequested();
                    using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                    using (var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(
                        http, request, NetworkDownloader.HeaderTimeout, ct).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status == 301 || status == 302 || status == 303 ||
                            status == 307 || status == 308)
                        {
                            Uri next = response.Headers.Location;
                            if (next != null && !next.IsAbsoluteUri)
                                next = new Uri(current, next);
                            if (!TrustedRedirect(next))
                                throw new InvalidDataException("GitHub 更新重定向到了不受信任的域名");
                            current = next;
                            continue;
                        }
                        response.EnsureSuccessStatusCode();
                        long? expected = response.Content.Headers.ContentLength;
                        if (expected.HasValue && expected.Value > maxBytes)
                            throw new InvalidDataException("更新包大小超过限制");
                        IProgress<DownloadBytes> relay = progress == null ? null :
                            new Progress<DownloadBytes>(value => progress.Report(new LauncherUpdateProgress
                            {
                                Completed = value.Completed, Total = value.Total,
                                Message = "正在从 GitHub 下载更新..."
                            }));
                        using (var output = new FileStream(destination, FileMode.Create,
                            FileAccess.Write, FileShare.None, 128 * 1024, true))
                            await NetworkDownloader.CopyBodyAsync(response, output, maxBytes, 0, expected,
                                relay, NetworkDownloader.TransferIdleTimeout, ct).ConfigureAwait(false);
                        return;
                    }
                }
                throw new IOException("GitHub 重定向次数过多，已取消更新");
            }
        }

        static string SafeEntry(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/", StringComparison.Ordinal) ||
                path.Contains("\\") || path.Contains(":") || path.Any(char.IsControl))
                throw new InvalidDataException("更新 ZIP 中存在非法文件路径");
            var parts = path.TrimEnd('/').Split('/');
            if (parts.Length > 15 || parts.Any(part => part.Length == 0 ||
                part == "." || part == ".." || part.EndsWith(" ") || part.EndsWith(".")))
                throw new InvalidDataException("更新 ZIP 包含路径逃逸");
            foreach (var part in parts)
            {
                if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("更新 ZIP 含有非法 Windows 文件名");
                string baseName = part.Split('.')[0].ToUpperInvariant();
                if (new[] { "CON", "NUL", "AUX", "PRN" }.Contains(baseName) ||
                    (baseName.Length == 4 &&
                     (baseName.StartsWith("COM") || baseName.StartsWith("LPT")) &&
                     baseName[3] >= '1' && baseName[3] <= '9'))
                    throw new InvalidDataException("更新 ZIP 包含保留文件名");
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        internal static string ExtractCheckedZip(string archivePath, string output)
        {
            Directory.CreateDirectory(output);
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                if (zip.Entries.Count < 8 || zip.Entries.Count > 500)
                    throw new InvalidDataException("更新 ZIP 中的文件数量异常");
                string rootName = null;
                long total = 0;
                foreach (var entry in zip.Entries)
                {
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidDataException("更新包不得包含符号链接");
                    string relative = SafeEntry(entry.FullName);
                    string prefix = entry.FullName.Split('/')[0];
                    if (rootName == null) rootName = prefix;
                    if (prefix != rootName)
                        throw new InvalidDataException("更新 ZIP 顶层目录不一致");
                    if (entry.Length > 256L * 1024 * 1024 ||
                        (total += entry.Length) > 640L * 1024 * 1024)
                        throw new InvalidDataException("更新 ZIP 解压后体积过大");
                    string target = Path.GetFullPath(Path.Combine(output, relative));
                    if (!target.StartsWith(Path.GetFullPath(output).TrimEnd('\\') + "\\",
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包路径不在预期目录");
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var outputFile = new FileStream(target, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                        input.CopyTo(outputFile);
                }
                if (string.IsNullOrEmpty(rootName))
                    throw new InvalidDataException("空更新包");
                return Path.Combine(output, rootName);
            }
        }

        internal static void VerifyZipManifest(string stage)
        {
            string manifest = Path.Combine(stage, "SHA256SUMS.txt");
            string main = Path.Combine(stage, MainFile);
            string updater = Path.Combine(stage, UpdaterFile);
            if (!File.Exists(manifest) || !File.Exists(main) || !File.Exists(updater))
                throw new InvalidDataException("ZIP 缺少启动器、更新助手或安全清单");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(manifest))
            {
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
                    throw new InvalidDataException("ZIP 内部 SHA-256 格式无效");
                string relative = line.Substring(66);
                if (relative != Path.GetFileName(relative) || !seen.Add(relative))
                    throw new InvalidDataException("ZIP 内部 SHA-256 清单路径不安全");
                VerifyFile(Path.Combine(stage, relative), line.Substring(0, 64));
            }
            if (!seen.Contains(MainFile) || !seen.Contains(UpdaterFile))
                throw new InvalidDataException("ZIP 内部缺少 EXE 校验项目");
            // The embedded onefile bootstrap expects a flat SHA256SUMS.txt
            // for the top-level executable/DLL set. Subdirectories remain
            // covered by the cryptographically verified whole-ZIP hash.
            foreach (string file in Directory.GetFiles(stage))
                if ((file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) &&
                    !seen.Contains(Path.GetFileName(file)))
                    throw new InvalidDataException("ZIP 顶层存在未校验可执行文件");
        }

        static async Task SmokeTestAsync(string path, CancellationToken ct)
        {
            using (var probe = Process.Start(new ProcessStartInfo(path, "--smoke-test")
            {
                UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path),
                CreateNoWindow = true
            }))
            {
                if (probe == null) throw new IOException("无法启动候选更新包");
                try
                {
                    for (int i = 0; i < 450; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (probe.HasExited)
                        {
                            if (probe.ExitCode != 0)
                                throw new IOException("候选更新包自检失败：" + probe.ExitCode);
                            return;
                        }
                        // Do not freeze the WPF UI while preflighting an EXE.
                        await Task.Delay(200, ct);
                    }
                    throw new IOException("候选更新包自检超时");
                }
                finally
                {
                    try { if (!probe.HasExited) probe.Kill(); }
                    catch { }
                }
            }
        }

        public async Task<PreparedLauncherUpdate> PrepareAsync(
            LauncherUpdateOffer offer, CancellationToken ct,
            IProgress<LauncherUpdateProgress> progress = null)
        {
            if (offer == null || offer.Mode != InstallMode)
                throw new ArgumentException("更新包格式与当前安装方式不一致");
            if (!DownloadPathService.TryValidate(settings.DownloadDirectory,
                settings.Root, out var selectedRoot, out var directoryError))
                throw new IOException("自定义下载路径无效：" + directoryError);
            string updatesRoot = DownloadPathService.UpdatesRoot(selectedRoot);
            string folder = Path.Combine(updatesRoot, "staged-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(Path.Combine(folder, ".blockcraft-stage"),
                    "BlockCraftLauncher update staging v1", Encoding.ASCII);
                progress?.Report(new LauncherUpdateProgress { Message = "正在获取校验信息..." });
                string manifestPath = Path.Combine(folder, "RELEASE-SHA256SUMS.txt");
                await DownloadOfficialAsync(offer.ManifestUrl, manifestPath, 32768, ct, null);
                string hash = ChecksumFromManifest(
                    File.ReadAllText(manifestPath, Encoding.ASCII), offer.AssetName);
                string package = Path.Combine(folder, offer.AssetName);
                await DownloadOfficialAsync(offer.AssetUrl, package, 256L * 1024 * 1024, ct, progress);
                VerifyFile(package, hash);
                string staged = package;
                if (offer.Mode == "zip")
                {
                    progress?.Report(new LauncherUpdateProgress { Message = "正在校验并安全解压..." });
                    staged = ExtractCheckedZip(package, Path.Combine(folder, "unpacked"));
                    VerifyZipManifest(staged);
                }
                progress?.Report(new LauncherUpdateProgress { Message = "检查新版本能否启动..." });
                await SmokeTestAsync(offer.Mode == "zip" ?
                    Path.Combine(staged, MainFile) : staged, ct);
                string target = installedTarget ?? (offer.Mode == "onefile"
                    ? Path.GetFullPath(Environment.GetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP") ?? "")
                    : Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory));
                return new PreparedLauncherUpdate
                {
                    Offer = offer, StagedPath = staged, Sha256 = hash,
                    TargetPath = target, UpdatesRoot = updatesRoot
                };
            }
            catch
            {
                try { Directory.Delete(folder, true); } catch { }
                throw;
            }
        }

        internal static void VerifyUnpackedArchiveMatchesZip(string archive, string unpackedRoot)
        {
            // The ZIP hash proves the downloaded archive, not an extracted
            // file that may have been modified afterward. Recompare EVERY
            // entry, including nested runtime DLLs, before direct execution.
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string parent = Directory.GetParent(unpackedRoot)?.FullName ??
                throw new InvalidDataException("无法验证更新包解压根目录");
            string root = Path.GetFullPath(unpackedRoot).TrimEnd('\\') + "\\";
            using (var zip = ZipFile.OpenRead(archive))
            using (var sha = SHA256.Create())
            {
                foreach (var entry in zip.Entries)
                {
                    string relative = SafeEntry(entry.FullName);
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                    string full = Path.GetFullPath(Path.Combine(parent, relative));
                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("ZIP 解压内容超出目标目录");
                    if (!expected.Add(full))
                        throw new InvalidDataException("ZIP 出现重复条目");
                    if (!File.Exists(full))
                        throw new InvalidDataException("新版解压目录缺少文件：" + entry.Name);
                    using (var input = entry.Open())
                    using (var actual = File.OpenRead(full))
                    {
                        if (!sha.ComputeHash(input).SequenceEqual(sha.ComputeHash(actual)))
                            throw new InvalidDataException("新版解压文件已被修改：" + entry.Name);
                    }
                }
            }
            foreach (string actual in Directory.GetFiles(unpackedRoot, "*", SearchOption.AllDirectories))
                if (!expected.Contains(Path.GetFullPath(actual)))
                    throw new InvalidDataException("新版解压目录含有非原始 ZIP 文件");
        }

        static bool IsPathInsideUpdateStaging(string path, string updatesRoot)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string legacy = DownloadPathService.UpdatesRoot(null);
            string root = Path.GetFullPath(string.IsNullOrWhiteSpace(updatesRoot)
                ? legacy : updatesRoot).TrimEnd('\\');
            bool custom = !string.Equals(root, legacy.TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
            // An arbitrary caller-provided root must never turn a Downloads
            // folder, Minecraft instance or symlink into an executable stage.
            if (custom)
            {
                if (!string.Equals(Path.GetFileName(root), "updates",
                    StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!DownloadPathService.TryValidate(Path.GetDirectoryName(root),
                    null, out var allowed, out var reason) ||
                    !string.Equals(Path.Combine(allowed, "updates"), root,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            string full = Path.GetFullPath(path);
            string prefix = root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            string remainder = full.Substring(prefix.Length);
            string first = remainder.Split(Path.DirectorySeparatorChar)[0];
            if (!first.StartsWith("staged-", StringComparison.OrdinalIgnoreCase) ||
                first.Length != 39 || !first.Substring(7).All(Uri.IsHexDigit))
                return false;
            return !custom || File.Exists(Path.Combine(root, first,
                ".blockcraft-stage"));
        }

        internal static string DirectPreviewExecutable(PreparedLauncherUpdate prepared)
        {
            if (prepared == null || prepared.Offer == null ||
                (prepared.Offer.Mode != "onefile" && prepared.Offer.Mode != "zip"))
                throw new InvalidOperationException("更新包类型无效");
            if (!IsPathInsideUpdateStaging(prepared.StagedPath,
                prepared.UpdatesRoot))
                throw new InvalidDataException("直接打开只能使用安全暂存目录中的已验证版本");
            if (prepared.Offer.Mode == "onefile")
            {
                VerifyFile(prepared.StagedPath, prepared.Sha256);
                if (!string.Equals(Path.GetFileName(prepared.StagedPath),
                    prepared.Offer.AssetName, StringComparison.Ordinal))
                    throw new InvalidDataException("新版单 EXE 文件名与发布资产不匹配");
                return prepared.StagedPath;
            }
            string stage = Path.GetFullPath(prepared.StagedPath);
            string extracted = Directory.GetParent(stage)?.FullName;
            if (extracted == null ||
                !string.Equals(Path.GetFileName(extracted), "unpacked",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("新版 ZIP 解压目录不符合预期");
            string downloaded = Path.Combine(Directory.GetParent(extracted).FullName,
                prepared.Offer.AssetName);
            VerifyFile(downloaded, prepared.Sha256);
            VerifyZipManifest(stage);
            VerifyUnpackedArchiveMatchesZip(downloaded, stage);
            string exe = Path.Combine(stage, MainFile);
            if (!File.Exists(exe))
                throw new FileNotFoundException("已解压的新版 WPF 主程序不存在", exe);
            return exe;
        }

        static bool IsLiveRenderedWindow(string marker) =>
            LauncherRelaunchSignal.TryReadReadyProcess(marker, handoff: false, out var pid) &&
            IsLiveWpfProcess(pid);

        static bool IsLiveWpfProcess(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return !process.HasExited &&
                        string.Equals(process.ProcessName, "BlockCraftLauncher.Wpf",
                            StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>
        /// Open an already SHA-256-verified version directly from the download
        /// staging area WITHOUT replacing the currently installed launcher.
        /// Only close the current UI after the new WPF window has rendered.
        /// Future desktop launches still use the previously installed EXE.
        /// </summary>
        public async Task<bool> OpenPreparedDirectlyAsync(
            PreparedLauncherUpdate prepared, CancellationToken ct)
        {
            if (prepared == null || prepared.Offer == null ||
                prepared.Offer.Mode != InstallMode)
                throw new InvalidOperationException("当前下载包不适用于本安装形式");
            string executable = DirectPreviewExecutable(prepared);
            string token = Guid.NewGuid().ToString("N");
            string marker = LauncherRelaunchSignal.FilePath(token);
            try
            {
                var start = new ProcessStartInfo(executable,
                    "--after-update " + token + " --direct-open")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    WindowStyle = ProcessWindowStyle.Normal
                };
                using (var launched = Process.Start(start))
                {
                    if (launched == null)
                        throw new IOException("无法直接运行已校验的新版本");
                    for (int i = 0; i < 300; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (IsLiveRenderedWindow(marker)) return true;
                        if (launched.HasExited && i > 30 &&
                            prepared.Offer.Mode == "zip")
                            break; // ZIP EXE is the actual WPF process.
                        await Task.Delay(150, ct);
                    }
                }
                throw new IOException("新版已尝试启动，但没有确认主窗口完成显示。" +
                    "旧版仍保持打开。可选择正常安装或查看 Windows 安全软件记录。");
            }
            finally
            {
                try { if (File.Exists(marker)) File.Delete(marker); }
                catch (IOException) { }
            }
        }

        internal static bool SameOffer(LauncherUpdateOffer left, LauncherUpdateOffer right)
        {
            return ReferenceEquals(left, right) ||
                (left != null && right != null &&
                    string.Equals(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(left.Mode, right.Mode, StringComparison.Ordinal) &&
                    string.Equals(left.AssetName, right.AssetName, StringComparison.Ordinal) &&
                    string.Equals(left.AssetUrl, right.AssetUrl, StringComparison.Ordinal) &&
                    string.Equals(left.ManifestUrl, right.ManifestUrl, StringComparison.Ordinal) &&
                    left.AssetSize == right.AssetSize);
        }

        internal static bool ShouldAutoInstallOnExit(LauncherConfig preferences,
            PreparedLauncherUpdate prepared, LauncherUpdateOffer currentOffer,
            bool busy, bool suppressed) =>
            preferences?.AutoInstallUpdatesOnExit == true &&
            CanInstallAutomatically(preferences, prepared, currentOffer, busy, suppressed);

        internal static bool ShouldAutoRestartWhenIdle(LauncherConfig preferences,
            PreparedLauncherUpdate prepared, LauncherUpdateOffer currentOffer,
            bool busy, bool suppressed) =>
            preferences?.AutoRestartAfterUpdateDownload == true &&
            CanInstallAutomatically(preferences, prepared, currentOffer, busy, suppressed);

        static bool CanInstallAutomatically(LauncherConfig preferences,
            PreparedLauncherUpdate prepared, LauncherUpdateOffer currentOffer, bool busy, bool suppressed)
        {
            return preferences.AutoCheckUpdates && preferences.AutoDownloadUpdates &&
                !busy && !suppressed &&
                prepared?.Offer != null && SameOffer(prepared.Offer, currentOffer) &&
                !string.IsNullOrWhiteSpace(prepared.TargetPath) &&
                !string.IsNullOrWhiteSpace(prepared.StagedPath) &&
                !string.IsNullOrWhiteSpace(prepared.Sha256);
        }

        internal async Task StartPreparedHandoffAsync(PreparedLauncherUpdate prepared, CancellationToken ct)
        {
            string token = Guid.NewGuid().ToString("N");
            using (var updater = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                return StartExternalInstaller(prepared, token);
            }, ct))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (LauncherRelaunchSignal.TryReadHandoffResult(token, out var status, out var detail))
                    {
                        if (status == LauncherRelaunchSignal.Rejected)
                            throw new IOException(detail);
                        if (status == LauncherRelaunchSignal.Ready &&
                            int.TryParse(detail, out int pid) && pid > 0 &&
                            IsLiveWpfProcess(pid) && !updater.HasExited)
                            return;
                    }
                    if (updater.HasExited)
                        throw new IOException("更新助手已退出，未确认新版主窗口显示。");
                    await Task.Delay(100, ct);
                }
            }
        }

        internal void UseInstalledTarget(string status, string target)
        {
            string fullPath = Path.GetFullPath(target);
            if (status == LauncherRelaunchSignal.InstalledOneFile)
            {
                if (!IsOneFileEnvironment(AppDomain.CurrentDomain.BaseDirectory, fullPath))
                    throw new IOException("找不到已更新的单文件启动器。");
                Environment.SetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP", fullPath);
            }
            else if (status != LauncherRelaunchSignal.InstalledZip ||
                !File.Exists(Path.Combine(fullPath, MainFile)))
                throw new IOException("找不到已更新的 ZIP 启动器。");
            installedTarget = fullPath;
        }

        public void StartExternalInstaller(PreparedLauncherUpdate prepared)
        {
            using (StartExternalInstaller(prepared, null)) { }
        }

        Process StartExternalInstaller(PreparedLauncherUpdate prepared, string token)
        {
            if (prepared?.Offer == null || prepared.Offer.Mode != InstallMode)
                throw new InvalidOperationException("尚未准备好可校验的更新包");
            if (!IsPathInsideUpdateStaging(prepared.StagedPath, prepared.UpdatesRoot) ||
                (prepared.Offer.Mode == "onefile" && !File.Exists(prepared.StagedPath)) ||
                (prepared.Offer.Mode == "zip" && !Directory.Exists(prepared.StagedPath)))
                throw new IOException("更新暂存包已不可用，请连接下载目录所在磁盘并重新下载。");
            DirectPreviewExecutable(prepared);
            string updater = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UpdaterFile);
            if (!File.Exists(updater))
                throw new FileNotFoundException("当前安装包缺少独立更新助手。请手动安装新版一次。", updater);
            string target = prepared.TargetPath;
            if (prepared.Offer.Mode == "onefile" && !File.Exists(target))
                throw new FileNotFoundException("找不到最初启动的单文件 EXE", target);
            if (prepared.Offer.Mode == "zip" &&
                !File.Exists(Path.Combine(target, MainFile)))
                throw new FileNotFoundException("找不到当前 WPF 启动器", target);

            string runner = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates", "runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runner);
            string helper = Path.Combine(runner, UpdaterFile);
            string args = "--apply --mode " + Q(prepared.Offer.Mode) +
                " --pid " + Q(Process.GetCurrentProcess().Id.ToString()) +
                " --target " + Q(target) +
                " --staged " + Q(prepared.StagedPath) +
                " --sha256 " + Q(prepared.Sha256) +
                " --updates-root " + Q(prepared.UpdatesRoot ??
                    DownloadPathService.UpdatesRoot(null));
            if (token != null) args += " --handoff " + Q(token);
            try
            {
                File.Copy(updater, helper, false);
                return Process.Start(new ProcessStartInfo(helper, args)
                {
                    UseShellExecute = false, WorkingDirectory = runner
                }) ?? throw new IOException("无法启动独立更新程序");
            }
            catch
            {
                try { File.Delete(helper); Directory.Delete(runner); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        // Windows CreateProcess command-line escaping. Backslashes preceding a
        // closing quote must be doubled; ordinary path slashes remain intact.
        static string Q(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            var builder = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    builder.Append('\\', slashes * 2 + 1);
                    builder.Append('"');
                    slashes = 0;
                    continue;
                }
                builder.Append('\\', slashes);
                slashes = 0;
                builder.Append(c);
            }
            builder.Append('\\', slashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }
}
