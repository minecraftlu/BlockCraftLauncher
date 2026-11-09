using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BlockCraftLauncher.OneFile
{
    internal static class Program
    {
        private const string ResourceName = "BlockCraftLauncher.Payload.zip";
        private const string GameExe = "BlockCraftLauncher.Wpf.exe";
        private const string Release = "v0.9.28";
        private const string Ready = ".blockcraft-ready";

        [STAThread]
        private static int Main(string[] args)
        {
            bool probe = args.Length == 1 &&
                (string.Equals(args[0], "--smoke-test", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "--download-ui-selftest", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(args[0], "--auth-selftest", StringComparison.OrdinalIgnoreCase));
            try
            {
                var package = ReadPackage();
                string digest = Digest(package);
                string home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlockCraftLauncher", "onefile");
                string folder = Path.Combine(home, Release + "-" + digest.Substring(0, 16));
                Directory.CreateDirectory(home);

                bool acquired = false;
                using (var mutex = new Mutex(false, @"Local\BlockCraftLauncher_OneFile_v0927"))
                {
                    try
                    {
                        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(60)); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired)
                            throw new IOException("另一个启动器正在准备文件，操作超时。");
                        if (!ValidCache(folder, digest))
                            Extract(package, folder, digest);
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }

                string target = Path.Combine(folder, GameExe);
                var options = new ProcessStartInfo(target)
                {
                    UseShellExecute = false,
                    WorkingDirectory = folder,
                    Arguments = string.Join(" ", args.Select(QuoteArgument))
                };
                using (var process = Process.Start(options))
                {
                    if (process == null) throw new IOException("无法启动 WPF 主程序。");
                    if (probe)
                    {
                        if (!process.WaitForExit(35000))
                        {
                            process.Kill();
                            throw new TimeoutException("单文件启动自检超时。");
                        }
                        return process.ExitCode;
                    }
                }
                return 0;
            }
            catch (Exception error)
            {
                string path = WriteError(error);
                if (!probe)
                    MessageBox.Show("BlockCraft Launcher 单文件启动失败：\n" + error.GetBaseException().Message +
                        "\n\n请确认已经安装 .NET Framework 4.8，微软正版登录还需 WebView2 Runtime。" +
                        "\n\n诊断日志：\n" + path,
                        "BlockCraft Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 25;
            }
        }

        private static byte[] ReadPackage()
        {
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (resource == null || resource.Length < 10000 || resource.Length > 160L * 1024 * 1024)
                    throw new InvalidDataException("EXE 内置启动器资源不存在或体积异常。");
                using (var memory = new MemoryStream())
                {
                    resource.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }

        private static string ToHex(byte[] digest) => BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
        private static string Digest(byte[] content)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(content));
        }
        private static string FileDigest(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return ToHex(sha.ComputeHash(stream));
        }

        private static bool ValidCache(string directory, string digest)
        {
            try
            {
                if (!Directory.Exists(directory) ||
                    (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (!File.Exists(Path.Combine(directory, Ready)) ||
                    File.ReadAllText(Path.Combine(directory, Ready)).Trim() != digest ||
                    !File.Exists(Path.Combine(directory, GameExe)) ||
                    !File.Exists(Path.Combine(directory, GameExe + ".config")))
                    return false;
                string manifest = Path.Combine(directory, "SHA256SUMS.txt");
                if (!File.Exists(manifest)) return false;
                string[] lines = File.ReadAllLines(manifest);
                if (lines.Length < 8 || lines.Length > 250) return false;
                bool main = false, commons = false;
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines)
                {
                    if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
                        return false;
                    string hash = line.Substring(0, 64);
                    if (hash.Any(c => !Uri.IsHexDigit(c))) return false;
                    string name = line.Substring(66);
                    if (name != Path.GetFileName(name) || !known.Add(name) ||
                        name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        return false;
                    string file = Path.Combine(directory, name);
                    if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                        !string.Equals(FileDigest(file), hash, StringComparison.OrdinalIgnoreCase))
                        return false;
                    main |= name.Equals(GameExe, StringComparison.OrdinalIgnoreCase);
                    commons |= name.Equals("CmlLib.Core.Commons.dll", StringComparison.OrdinalIgnoreCase);
                }
                return main && commons;
            }
            catch (Exception) { return false; }
        }

        private static string SafeEntryName(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("/", StringComparison.Ordinal) ||
                path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path.Any(char.IsControl))
                throw new InvalidDataException("资源包中包含不安全的文件路径。");
            var parts = path.TrimEnd('/').Split('/');
            if (parts.Length == 0 || parts.Length > 15)
                throw new InvalidDataException("资源包目录层级异常。");
            foreach (var part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." ||
                    part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("资源包包含路径逃逸或非法文件名。");
                string baseName = part.Split('.')[0].ToUpperInvariant();
                if (baseName == "CON" || baseName == "AUX" || baseName == "NUL" ||
                    baseName == "PRN" || baseName == "CLOCK$" ||
                    (baseName.Length == 4 &&
                     (baseName.StartsWith("COM", StringComparison.Ordinal) ||
                      baseName.StartsWith("LPT", StringComparison.Ordinal)) &&
                     baseName[3] >= '1' && baseName[3] <= '9'))
                    throw new InvalidDataException("资源包包含 Windows 保留文件名。");
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        private static void Extract(byte[] package, string folder, string digest)
        {
            string staging = Path.Combine(Path.GetDirectoryName(folder),
                ".onefile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                using (var memory = new MemoryStream(package, false))
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Read, false))
                {
                    if (zip.Entries.Count < 8 || zip.Entries.Count > 500)
                        throw new InvalidDataException("内置资源文件数异常。");
                    long total = 0;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    foreach (var entry in zip.Entries)
                    {
                        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                            throw new InvalidDataException("内置资源不允许符号链接。");
                        string relative = SafeEntryName(entry.FullName);
                        if (!names.Add(relative))
                            throw new InvalidDataException("内置资源存在重复目标。");
                        string output = Path.GetFullPath(Path.Combine(staging, relative));
                        if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("资源路径逃逸。");
                        if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(output);
                            continue;
                        }
                        if (entry.Length < 0 || entry.Length > 512L * 1024 * 1024 ||
                            (total += entry.Length) > 1024L * 1024 * 1024)
                            throw new InvalidDataException("内置资源大小超出限制。");
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        using (var input = entry.Open())
                        using (var dest = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            input.CopyTo(dest);
                    }
                }
                File.WriteAllText(Path.Combine(staging, Ready), digest, Encoding.ASCII);
                if (!ValidCache(staging, digest))
                    throw new InvalidDataException("启动器内置文件完整性校验失败。");
                if (Directory.Exists(folder))
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("缓存目录为重解析链接，拒绝覆盖。");
                    Directory.Delete(folder, true);
                }
                Directory.Move(staging, folder);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        }

        private static string QuoteArgument(string value)
        {
            if (value == null || value.Length == 0) return "\"\"";
            if (!value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
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

        private static string WriteError(Exception error)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "onefile", "bootstrap-error.log");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("u") +
                    Environment.NewLine + error + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
            return path;
        }
    }
}