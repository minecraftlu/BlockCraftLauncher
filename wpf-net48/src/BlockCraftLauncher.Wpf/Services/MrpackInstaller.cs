using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class MrpackInfo
    {
        public string Name { get; set; }
        public string Minecraft { get; set; }
        public string Loader { get; set; }
        public string LoaderVersion { get; set; }
        public int FileCount { get; set; }
        public long DownloadBytes { get; set; }
    }

    /// <summary>
    /// Restricted, isolated client .mrpack installer. Never extracts into a
    /// pre-existing profile and never registers a failed partial installation.
    /// </summary>
    public sealed class MrpackInstaller
    {
        readonly LauncherConfig config;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        const long MaxArchive = 256L * 1024 * 1024;
        const long MaxExtracted = 1024L * 1024 * 1024;
        const long MaxDownload = 4L * 1024 * 1024 * 1024;
        const int MaxEntries = 4000;

        public MrpackInstaller(LauncherConfig config) { this.config = config; }

        static string Get(IDictionary<string, object> obj, string key)
            => obj.TryGetValue(key, out var v) && v != null ? Convert.ToString(v) : "";

        static string SafeRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 300 ||
                path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("\\", StringComparison.Ordinal) ||
                path.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0)
                throw new InvalidDataException("整合包包含不安全的文件路径：" + path);
            var components = path.Split('/');
            if (components.Any(p => p == "." || p == ".." || p.Length == 0 || p.EndsWith(".", StringComparison.Ordinal) ||
                p.EndsWith(" ", StringComparison.Ordinal) || p.Any(char.IsControl)))
                throw new InvalidDataException("整合包文件路径不合法：" + path);
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        static string UnderRoot(string root, string relative)
        {
            var resolvedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(Path.Combine(resolvedRoot, SafeRelative(relative)));
            if (!resolved.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("检测到整合包路径逃逸");
            return resolved;
        }

        static bool PermittedHost(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IsDefaultPort || uri.UserInfo.Length != 0) return false;
            string host = uri.IdnHost.ToLowerInvariant();
            return host == "cdn.modrinth.com" ||
                host == "github.com" || host == "raw.githubusercontent.com" ||
                host == "gitlab.com";
        }

        sealed class RemoteFile
        {
            public string Path, Sha1, Sha512;
            public string[] Urls;
            public long Size;
        }
        sealed class Parsed
        {
            public MrpackInfo Info;
            public List<RemoteFile> Files = new List<RemoteFile>();
        }
        Parsed Inspect(ZipArchive archive)
        {
            if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("整合包文件数超过限制");
            long totalUnpacked = 0;
            foreach (var item in archive.Entries)
            {
                // Reject symlinks (Unix file mode 0120000) even if the ZIP path looks safe.
                if (((item.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("整合包不允许符号链接");
                if (item.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                SafeRelative(item.FullName);
                if (item.Length > MaxArchive || (totalUnpacked += item.Length) > MaxExtracted)
                    throw new InvalidDataException("整合包解压内容超出大小限制");
            }
            var index = archive.GetEntry("modrinth.index.json");
            if (index == null || index.Length > 4 * 1024 * 1024) throw new InvalidDataException("无有效的 modrinth.index.json");
            Dictionary<string, object> data;
            using (var reader = new StreamReader(index.Open(), Encoding.UTF8, true))
                data = json.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
            if (data == null || Convert.ToInt32(data["formatVersion"]) != 1 || Get(data, "game") != "minecraft")
                throw new InvalidDataException("不支持此 .mrpack 格式");
            if (!(data.TryGetValue("dependencies", out var deps) && deps is Dictionary<string, object> versions))
                throw new InvalidDataException("整合包缺少 Minecraft 版本说明");
            string mcVersion = Get(versions, "minecraft");
            if (!Regex.IsMatch(mcVersion, @"^[A-Za-z0-9._-]{1,60}$"))
                throw new InvalidDataException("Minecraft 版本号不合法");
            var loaderTypes = new[] { Tuple.Create("fabric-loader", "Fabric"), Tuple.Create("quilt-loader", "Quilt"),
                Tuple.Create("forge", "Forge"), Tuple.Create("neoforge", "NeoForge") }
                .Where(x => versions.ContainsKey(x.Item1)).ToList();
            if (loaderTypes.Count > 1) throw new InvalidDataException("整合包指定了多种冲突的模组加载器");
            string loader = loaderTypes.Count == 0 ? "原版" : loaderTypes[0].Item2;
            string loaderVersion = loaderTypes.Count == 0 ? "" : Get(versions, loaderTypes[0].Item1);
            if (loader != "原版" && !Regex.IsMatch(loaderVersion, @"^[A-Za-z0-9._+-]{1,90}$"))
                throw new InvalidDataException("模组加载器版本不合法");

            var parsed = new Parsed { Info = new MrpackInfo { Minecraft = mcVersion, Loader = loader,
                LoaderVersion = loaderVersion, Name = Get(data, "name") } };
            if (!(data.TryGetValue("files", out var rawFiles) && rawFiles is object[] array) || array.Length > MaxEntries)
                throw new InvalidDataException("整合包远程文件索引不合法");
            long totalDownload = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array)
            {
                if (!(item is Dictionary<string, object> row))
                    throw new InvalidDataException("整合包远程文件条目不合法");
                if (row.TryGetValue("env", out var env) && env is Dictionary<string, object> flags &&
                    Get(flags, "client") == "unsupported") continue;
                string relative = Get(row, "path"); SafeRelative(relative);
                if (!paths.Add(relative)) throw new InvalidDataException("整合包包含重复目标文件");
                var hashes = row.TryGetValue("hashes", out var hashObj) ? hashObj as Dictionary<string, object> : null;
                string sha1 = hashes == null ? "" : Get(hashes, "sha1");
                string sha512 = hashes == null ? "" : Get(hashes, "sha512");
                if (!Regex.IsMatch(sha1, "^[a-fA-F0-9]{40}$") ||
                    !Regex.IsMatch(sha512, "^[a-fA-F0-9]{128}$"))
                    throw new InvalidDataException("远程文件缺少有效的 SHA-1 / SHA-512 校验值");
                if (!long.TryParse(Get(row, "fileSize"), out long size) || size < 0 || size > MaxArchive ||
                    (totalDownload += size) > MaxDownload)
                    throw new InvalidDataException("整合包下载大小超出限制");
                if (!(row.TryGetValue("downloads", out var list) && list is object[] urls))
                    throw new InvalidDataException("资源没有下载地址");
                string[] sources = urls.Select(Convert.ToString).Where(PermittedHost)
                    .Distinct(StringComparer.Ordinal).Take(4).ToArray();
                if (sources.Length == 0) throw new InvalidDataException("资源没有允许的官方/可信下载地址");
                parsed.Files.Add(new RemoteFile
                    { Path = relative, Sha1 = sha1, Sha512 = sha512, Size = size, Urls = sources });
            }
            parsed.Info.FileCount = parsed.Files.Count;
            parsed.Info.DownloadBytes = totalDownload;
            return parsed;
        }

        public MrpackInfo Preview(string archivePath)
        {
            if (new FileInfo(archivePath).Length > MaxArchive) throw new InvalidDataException("整合包压缩文件过大");
            using (var archive = ZipFile.OpenRead(archivePath)) return Inspect(archive).Info;
        }

        static async Task DownloadPackFileAsync(NetworkDownloader network, RemoteFile file,
            string destination, CancellationToken ct)
        {
            IOException last = null;
            foreach (var source in file.Urls)
            {
                try
                {
                    await network.DownloadVerifiedAsync(source, destination, file.Sha1, ct);
                    return;
                }
                catch (IOException ex) when (!ct.IsCancellationRequested) { last = ex; }
            }
            throw new IOException("整合包文件下载失败：" + file.Path + "。" + last?.Message, last);
        }

        public async Task<GameProfile> InstallAsync(string archivePath, IProgress<string> progress, CancellationToken ct)
        {
            if (new FileInfo(archivePath).Length > MaxArchive) throw new InvalidDataException("整合包压缩文件过大");
            Parsed parsed;
            using (var zip = ZipFile.OpenRead(archivePath)) parsed = Inspect(zip);
            var info = parsed.Info;
            string instance = Path.Combine(Path.GetFullPath(config.Root), "instances", "wpf-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(instance);
            try
            {
                // Install the game + exact loader version in this *new* instance root.
                var isolated = new LauncherConfig
                {
                    Root = instance, JavaPath = config.JavaPath, Proxy = config.Proxy,
                    NetworkMode = config.NetworkMode, McimEnabled = config.McimEnabled,
                    DownloadThreads = config.DownloadThreads, DownloadRetries = config.DownloadRetries
                };
                var loader = new ModLoaderInstaller(isolated);
                progress?.Report("准备安装 Minecraft " + info.Minecraft + " 和 " + info.Loader + " " + info.LoaderVersion);
                var game = await loader.InstallAsync(info.Minecraft, info.Loader, info.LoaderVersion, progress, ct);
                using (var network = new NetworkDownloader(config))
                {
                    for (int i = 0; i < parsed.Files.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var row = parsed.Files[i]; var path = UnderRoot(instance, row.Path);
                        if (File.Exists(path)) throw new IOException("目标文件发生冲突：" + row.Path);
                        progress?.Report("下载整合包文件 " + (i + 1) + "/" + parsed.Files.Count + ": " + row.Path);
                        await DownloadPackFileAsync(network, row, path, ct);
                        if (new FileInfo(path).Length != row.Size) throw new InvalidDataException("文件大小与索引不一致：" + row.Path);
                        using (var stream = File.OpenRead(path))
                        using (var sha = SHA512.Create())
                        {
                            string digest = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                            if (!digest.Equals(row.Sha512, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("SHA-512 校验失败：" + row.Path);
                        }
                    }
                }
                using (var zip = ZipFile.OpenRead(archivePath))
                {
                    foreach (var item in zip.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (item.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                        string relative = null;
                        if (item.FullName.StartsWith("overrides/", StringComparison.Ordinal))
                            relative = item.FullName.Substring("overrides/".Length);
                        if (item.FullName.StartsWith("client-overrides/", StringComparison.Ordinal))
                            relative = item.FullName.Substring("client-overrides/".Length);
                        if (relative == null || relative.Length == 0) continue;
                        string target = UnderRoot(instance, relative);
                        // A .mrpack override can intentionally replace another override,
                        // but may not overwrite game metadata or remote downloads.
                        if (File.Exists(target)) throw new InvalidDataException("覆盖文件与现有资源冲突：" + relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = item.Open())
                        using (var output = new FileStream(target, FileMode.CreateNew))
                            input.CopyTo(output);
                    }
                }
                ct.ThrowIfCancellationRequested();
                game.Name = string.IsNullOrWhiteSpace(info.Name) ? "Modrinth 整合包" : info.Name.Trim();
                game.Directory = instance;
                progress?.Report("整合包安装完成：" + game.Name);
                return game;
            }
            catch
            {
                // Only delete the GUID-named instance created for this attempt.
                if (Directory.Exists(instance)) Directory.Delete(instance, true);
                throw;
            }
        }
    }
}