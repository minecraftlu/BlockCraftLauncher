using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class OllamaPortablePackage
    {
        internal const string Version = "0.40.18";
        internal const long DownloadBytes = 1717307988L;
        const string Digest = "ca97292ac1e9acc77fcf473bb2741fa6acd12e3798f261b419680068b7cc4c16";
        const string Asset = "ollama-windows-amd64.zip";
        const string Receipt = "installed.bin";
        static readonly Uri Source = new Uri("https://github.com/ollama/ollama/releases/download/v" + Version + "/" + Asset);
        static readonly byte[] ReceiptScope = Encoding.UTF8.GetBytes("BlockCraft.Ollama.Windows.x64." + Digest);

        static string PackageDirectory(string managedRoot)
        {
            if (string.IsNullOrWhiteSpace(managedRoot) || !Path.IsPathRooted(managedRoot))
                throw new IOException("请先选择本机的模型存储目录。");
            return Path.Combine(Path.GetFullPath(managedRoot), "ollama-runtime", Version);
        }

        internal static bool IsInstalled(string managedRoot)
        {
            try
            {
                string directory = PackageDirectory(managedRoot);
                var receipt = ReadReceipt(directory);
                return receipt != null && File.Exists(Path.Combine(directory, receipt[0], "ollama.exe"));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            { return false; }
        }

        static string[] ReadReceipt(string directory)
        {
            string path = Path.Combine(directory, Receipt);
            VerifiedModelDownload.RejectReparsePoints(path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 2 * 1024 * 1024 || info.Length == 0) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), ReceiptScope, DataProtectionScope.CurrentUser);
                var lines = Encoding.UTF8.GetString(bytes).Split('\n');
                if (lines.Length < 2 || !Guid.TryParseExact(lines[0], "N", out _)) return null;
                return lines;
            }
            catch (CryptographicException) { return null; }
        }

        internal static Task<string> EnsureAsync(LauncherConfig settings, string managedRoot, IProgress<string> progress, CancellationToken ct,
            bool allowDownload = true)
        {
            return InstallAsync(managedRoot, progress, ct, (path, token) =>
                VerifiedModelDownload.FetchAsync(Source, path, DownloadBytes, Digest, settings, progress, token), allowDownload);
        }

        internal static async Task<string> InstallAsync(string managedRoot, IProgress<string> progress, CancellationToken ct,
            Func<string, CancellationToken, Task> downloadVerified, bool allowDownload = true)
        {
            ct.ThrowIfCancellationRequested();
            string directory = PackageDirectory(managedRoot);
            VerifiedModelDownload.RejectReparsePoints(directory);
            Directory.CreateDirectory(directory);
            using (await AcquireLockAsync(Path.Combine(directory, "install.lock"), ct).ConfigureAwait(false))
            {
                var receipt = ReadReceipt(directory);
                if (receipt != null)
                {
                    progress?.Report("正在校验本地运行组件…");
                    var existing = Path.Combine(directory, receipt[0]);
                    if (await Task.Run(() => VerifyInstalled(existing, receipt, ct), ct).ConfigureAwait(false))
                        return Path.Combine(existing, "ollama.exe");
                }

                if (!allowDownload) throw new InvalidOperationException("本地运行组件尚未安装或校验未通过，请点击下载并安装。");
                string archive = Path.Combine(directory, Asset);
                VerifiedModelDownload.RejectReparsePoints(archive);
                await downloadVerified(archive, ct).ConfigureAwait(false);
                progress?.Report("正在解压本地运行组件…");
                string generation = Guid.NewGuid().ToString("N");
                string target = Path.Combine(directory, generation);
                // Publish the new generation by replacing its receipt only after every file is complete.
                Directory.CreateDirectory(target);
                string temp = Path.Combine(directory, generation + ".receipt.tmp");
                bool published = false;
                try
                {
                    var files = await Task.Run(() => Extract(archive, target, ct), ct).ConfigureAwait(false);
                    string data = generation + "\n" + string.Join("\n", files);
                    var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(data), ReceiptScope, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(temp, bytes);
                    ct.ThrowIfCancellationRequested();
                    NetworkDownloader.CommitVerifiedFile(temp, Path.Combine(directory, Receipt));
                    published = true;
                    try { File.Delete(archive); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    return Path.Combine(target, "ollama.exe");
                }
                finally
                {
                    if (!published && Directory.Exists(target)) Directory.Delete(target, true);
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
        }

        static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                VerifiedModelDownload.RejectReparsePoints(path);
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33)
                { await Task.Delay(300, ct).ConfigureAwait(false); }
            }
        }

        static string EntryName(string name)
        {
            // Ollama's Windows archive may use either ZIP or Windows directory separators.
            var segments = name.Replace('\\', '/').TrimEnd('/').Split('/');
            if (segments.Length > 16 || name.Length > 512) throw new InvalidDataException("运行组件压缩包路径太长。");
            foreach (var part in segments)
            {
                if (part.Length == 0 || part.EndsWith(".") || part.EndsWith(" ") || part.Any(char.IsControl) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("运行组件压缩包包含无效路径。");
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                     "0123456789¹²³".IndexOf(stem[3]) >= 0))
                    throw new InvalidDataException("运行组件压缩包包含保留的文件名。");
            }
            return string.Join("/", segments);
        }

        static List<string> Extract(string archive, string target, CancellationToken ct)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<string>();
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 8192) throw new InvalidDataException("运行组件压缩包包含过多文件。");
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = EntryName(entry.FullName);
                    if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 ||
                        (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("运行组件压缩包包含重复文件或链接。");
                    string path = Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar));
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    { Directory.CreateDirectory(path); continue; }
                    if (entry.Length > 12L * 1024 * 1024 * 1024 - expanded) throw new InvalidDataException("运行组件解压后过大。");
                    expanded += entry.Length;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        entries.Add(CopyAndHash(input, output, entry.Length, ct) + " " + name);
                }
            }
            if (!File.Exists(Path.Combine(target, "ollama.exe"))) throw new InvalidDataException("运行组件中缺少 ollama.exe。");
            return entries;
        }

        static string CopyAndHash(Stream input, Stream output, long expected, CancellationToken ct)
        {
            byte[] buffer = new byte[128 * 1024];
            long total = 0;
            using (var hash = SHA256.Create())
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += count;
                    if (total > expected) throw new InvalidDataException("运行组件文件长度异常。");
                    output?.Write(buffer, 0, count);
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                if (total != expected) throw new InvalidDataException("运行组件文件不完整。");
                hash.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        static bool VerifyInstalled(string directory, string[] receipt, CancellationToken ct)
        {
            VerifiedModelDownload.RejectReparsePoints(directory);
            if (!Directory.Exists(directory)) return false;
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in receipt.Skip(1))
            {
                if (line.Length < 66 || line[64] != ' ') return false;
                expected.Add(line.Substring(65), line.Substring(0, 64));
            }
            var dirs = new Stack<string>();
            dirs.Push(directory);
            while (dirs.Count != 0)
            {
                foreach (var item in Directory.EnumerateFileSystemEntries(dirs.Pop()))
                {
                    ct.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) { dirs.Push(item); continue; }
                    string name = item.Substring(directory.Length + 1).Replace('\\', '/');
                    if (!expected.TryGetValue(name, out var digest)) return false;
                    using (var file = File.OpenRead(item))
                        if (CopyAndHash(file, null, file.Length, ct) != digest) return false;
                    expected.Remove(name);
                }
            }
            return expected.Count == 0;
        }
    }
}
