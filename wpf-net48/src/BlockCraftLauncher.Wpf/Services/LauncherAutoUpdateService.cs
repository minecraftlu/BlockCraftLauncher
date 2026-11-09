using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
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
    }

    /// <summary>
    /// Fetch, stage and verify GitHub WPF Release assets. The main app NEVER
    /// replaces its own running files; a tiny external helper waits for exit,
    /// smoke-tests the replacement and rolls back on failure.
    /// </summary>
    public sealed class LauncherAutoUpdateService
    {
        internal const string CurrentTag = "v0.9.30-wpf-rc.1";
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

        public LauncherAutoUpdateService(LauncherConfig settings)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
            string version = tag.Split('-')[0]; // v0.9.30-wpf-rc.1 -> v0.9.30
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

        public async Task<LauncherUpdateOffer> CheckAsync(CancellationToken ct)
        {
            using (var network = new NetworkDownloader(settings))
            {
                string json = await network.GetStringWithFallbackAsync(ReleasesApi, "official", ct);
                return SelectOffer(json, settings.UpdateChannel, InstallMode);
            }
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
            using (var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false,
                Proxy = string.IsNullOrWhiteSpace(settings.Proxy) ? null : new WebProxy(settings.Proxy),
                UseProxy = !string.IsNullOrWhiteSpace(settings.Proxy)
            })
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(12) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF-Update/0.9.30");
                Uri current = new Uri(url);
                if (!TrustedRedirect(current) ||
                    !current.AbsolutePath.StartsWith(
                        "/minecraftlu/BlockCraftLauncher/releases/download/", StringComparison.Ordinal))
                    throw new InvalidDataException("更新只能下载官方 GitHub Release 资产");

                for (int redirects = 0; redirects < 5; redirects++)
                {
                    ct.ThrowIfCancellationRequested();
                    using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                    using (var response = await http.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, ct))
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
                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = new FileStream(destination, FileMode.Create,
                            FileAccess.Write, FileShare.None, 128 * 1024, true))
                        {
                            byte[] buffer = new byte[128 * 1024];
                            long done = 0;
                            int count;
                            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                            {
                                done += count;
                                if (done > maxBytes)
                                    throw new InvalidDataException("更新包大小超过限制");
                                await output.WriteAsync(buffer, 0, count, ct);
                                progress?.Report(new LauncherUpdateProgress
                                {
                                    Completed = done, Total = expected ?? 0,
                                    Message = "正在从 GitHub 下载更新..."
                                });
                            }
                            if (expected.HasValue && expected.Value != done)
                                throw new IOException("更新文件下载不完整");
                        }
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
            string folder = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher",
                "updates", "staged-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
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
                string target;
                if (offer.Mode == "onefile")
                    target = Path.GetFullPath(Environment.GetEnvironmentVariable(
                        "BLOCKCRAFT_SINGLEFILE_BOOTSTRAP") ?? "");
                else target = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
                return new PreparedLauncherUpdate
                {
                    Offer = offer, StagedPath = staged, Sha256 = hash,
                    TargetPath = target
                };
            }
            catch
            {
                try { Directory.Delete(folder, true); } catch { }
                throw;
            }
        }

        static bool IsPathInsideUpdateStaging(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string root = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates")).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string remainder = full.Substring(root.Length);
            string first = remainder.Split(Path.DirectorySeparatorChar)[0];
            return first.StartsWith("staged-", StringComparison.OrdinalIgnoreCase) &&
                first.Length == 39 &&
                first.Substring(7).All(Uri.IsHexDigit);
        }

        internal static string DirectPreviewExecutable(PreparedLauncherUpdate prepared)
        {
            if (prepared == null || prepared.Offer == null ||
                !string.Equals(prepared.Offer.Mode, InstallMode, StringComparison.Ordinal))
                throw new InvalidOperationException("当前下载包不适用于本安装形式");
            if (!IsPathInsideUpdateStaging(prepared.StagedPath))
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
            string exe = Path.Combine(stage, MainFile);
            if (!File.Exists(exe))
                throw new FileNotFoundException("已解压的新版 WPF 主程序不存在", exe);
            return exe;
        }

        static bool IsLiveRenderedWindow(string marker)
        {
            try
            {
                if (!File.Exists(marker)) return false;
                var lines = File.ReadAllLines(marker);
                if (lines.Length < 2 || !int.TryParse(lines[0], out var pid) ||
                    pid <= 0 || !DateTime.TryParse(lines[1], out _))
                    return false;
                using (var p = Process.GetProcessById(pid))
                    return !p.HasExited &&
                        string.Equals(p.ProcessName, "BlockCraftLauncher.Wpf",
                            StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException ||
                ex is InvalidOperationException)
            {
                return false;
            }
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

        public void StartExternalInstaller(PreparedLauncherUpdate prepared)
        {
            if (prepared == null || prepared.Offer.Mode != InstallMode)
                throw new InvalidOperationException("尚未准备好可校验的更新包");
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
            File.Copy(updater, helper, false);
            string args = "--apply --mode " + Q(prepared.Offer.Mode) +
                " --pid " + Q(Process.GetCurrentProcess().Id.ToString()) +
                " --target " + Q(target) +
                " --staged " + Q(prepared.StagedPath) +
                " --sha256 " + Q(prepared.Sha256);
            var process = Process.Start(new ProcessStartInfo(helper, args)
            {
                UseShellExecute = false, WorkingDirectory = runner
            });
            if (process == null) throw new IOException("无法启动独立更新程序");
            process.Dispose();
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
