using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace BlockCraftLauncher.Updater
{
    /// <summary>
    /// Separate process: the launcher never overwrites its own running EXE.
    /// No admin elevation, shell scripts or Minecraft/game directory edits.
    /// </summary>
    internal static class Program
    {
        const string WpfExe = "BlockCraftLauncher.Wpf.exe";
        const string ManifestName = "SHA256SUMS.txt";
        const int WaitSeconds = 150;
        const uint MoveReplaceExisting = 1;
        const uint MoveWriteThrough = 8;

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool MoveFileEx(string source, string destination, uint flags);

        sealed class Changed
        {
            internal string Destination, Backup;
            internal bool Existed;
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--selftest")
                return SelfTest();
            try
            {
                var opt = ParseArguments(args);
                string mode = Require(opt, "mode");
                if (mode != "onefile" && mode != "zip")
                    throw new InvalidDataException("不支持的更新包格式");
                int pid;
                if (!int.TryParse(Require(opt, "pid"), out pid) || pid <= 0)
                    throw new InvalidDataException("原启动器进程编号无效");
                string target = Path.GetFullPath(Require(opt, "target"));
                string staged = Path.GetFullPath(Require(opt, "staged"));
                string digest = Require(opt, "sha256");
                string updatesRoot = opt.TryGetValue("updates-root",
                    out var selectedRoot) ? Path.GetFullPath(selectedRoot) : null;
                if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("更新包 SHA-256 无效");
                string entryPoint = mode == "onefile" ? target : Path.Combine(target, WpfExe);
                using (var sha = SHA256.Create())
                {
                    string key = BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes(entryPoint.ToUpperInvariant()))).Replace("-", "");
                    using (var updateLock = new Mutex(false, @"Local\BlockCraftLauncher.Update." + key))
                    {
                        bool acquired;
                        try { acquired = updateLock.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new IOException("同一个启动器正在更新，请等待更新结束。");
                        try
                        {
                            if (!WaitForPreviousProcess(pid, WaitSeconds))
                                throw new IOException("原启动器仍在运行，请关闭后重试。");
                            if (mode == "onefile")
                                ApplySingleExe(target, staged, digest, updatesRoot: updatesRoot);
                            else
                                ApplyDirectory(target, staged, digest, updatesRoot: updatesRoot);
                        }
                        finally { updateLock.ReleaseMutex(); }
                    }
                }
                return 0;
            }
            catch (Exception error)
            {
                string log = WriteLog(error);
                try
                {
                    MessageBox.Show("BlockCraft Launcher 更新失败。\n\n" +
                        error.Message + "\n\n诊断记录：\n" + log,
                        "BlockCraft Launcher 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { /* no UI in headless sessions */ }
                return 1;
            }
        }

        static Dictionary<string, string> ParseArguments(string[] args)
        {
            if ((args.Length != 11 && args.Length != 13) || args[0] != "--apply")
                throw new ArgumentException("缺少安全更新参数");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) ||
                    result.ContainsKey(args[i].Substring(2)))
                    throw new ArgumentException("重复或无效的更新参数");
                result.Add(args[i].Substring(2), args[i + 1]);
            }
            return result;
        }

        static string Require(Dictionary<string, string> data, string key)
        {
            string value;
            if (!data.TryGetValue(key, out value) || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("更新参数缺失：" + key);
            return value;
        }

        static bool WaitForPreviousProcess(int pid, int seconds)
        {
            try
            {
                using (var old = Process.GetProcessById(pid))
                    return old.WaitForExit(seconds * 1000);
            }
            catch (ArgumentException) { return true; } // Already exited.
        }

        static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static void ExpectHash(string path, string digest)
        {
            if (!File.Exists(path) ||
                !string.Equals(Hash(path), digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA-256 校验未通过：" + Path.GetFileName(path));
        }

        static bool Reparse(string path)
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }

        static void EnsureNoReparseParents(string fullPath)
        {
            var current = File.Exists(fullPath)
                ? Path.GetDirectoryName(fullPath) : fullPath;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && Reparse(current))
                    throw new IOException("拒绝在符号链接或联接目录执行更新");
                current = Path.GetDirectoryName(current);
            }
        }

        static void CheckLauncherExe(string target)
        {
            if (!File.Exists(target) || Reparse(target))
                throw new FileNotFoundException("启动器主程序不存在或是符号链接", target);
            EnsureNoReparseParents(target);
        }

        static void SmokeTest(string executable)
        {
            using (var probe = Process.Start(new ProcessStartInfo(executable, "--smoke-test")
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable),
                CreateNoWindow = true
            }))
            {
                if (probe == null || !probe.WaitForExit(90000))
                {
                    try { probe?.Kill(); } catch { }
                    throw new IOException("新版启动器启动检查超时，恢复旧版本");
                }
                if (probe.ExitCode != 0)
                    throw new IOException("新版启动自检退出码 " + probe.ExitCode + "，恢复旧版本");
            }
        }

        static string RelaunchFile(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length != 32 ||
                token.Any(c => !Uri.IsHexDigit(c)))
                throw new ArgumentException("启动确认令牌格式无效");
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates", "relaunch-" +
                token.ToLowerInvariant() + ".ok");
        }

        static bool TryReadWindowReady(string path, Func<int, bool> liveWpfProcess)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2 ||
                    !int.TryParse(lines[0], out var pid) || pid <= 0 ||
                    !DateTime.TryParse(lines[1], out _))
                    return false;
                return liveWpfProcess(pid);
            }
            catch (Exception error) when (error is IOException ||
                error is UnauthorizedAccessException)
            {
                return false; // The WPF process may still be writing the marker.
            }
        }

        static bool IsVisibleWpfProcess(int pid)
        {
            try
            {
                using (var candidate = Process.GetProcessById(pid))
                    return !candidate.HasExited &&
                        string.Equals(candidate.ProcessName, "BlockCraftLauncher.Wpf",
                            StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        static bool AwaitWindow(string marker, int timeoutMs,
            Func<int, bool> liveWpfProcess)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (TryReadWindowReady(marker, liveWpfProcess)) return true;
                Thread.Sleep(150);
            }
            return TryReadWindowReady(marker, liveWpfProcess);
        }

        /// <summary>
        /// Starts the ORIGINAL installed EXE, not the old onefile payload cache.
        /// On success a new WPF main window must have rendered and acknowledged
        /// itself, including when an outer OneFile bootstrap has already exited.
        /// </summary>
        static bool Restart(string executable)
        {
            string token = Guid.NewGuid().ToString("N");
            string marker = RelaunchFile(token);
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            if (File.Exists(marker)) File.Delete(marker);

            Process launched = Process.Start(new ProcessStartInfo(executable,
                "--after-update " + token)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable),
                WindowStyle = ProcessWindowStyle.Normal
            });
            if (launched == null) throw new IOException("无法重新打开更新后的启动器");
            launched.Dispose();
            try
            {
                if (!AwaitWindow(marker, 70000, IsVisibleWpfProcess))
                {
                    // Do NOT roll back or overwrite a possibly still-running
                    // new WPF process. The ZIP and original EXE were verified,
                    // but the GUI may be delayed or blocked by Windows.
                    string text = "新版本文件已完成安装并通过启动自检，" +
                        "但未能确认新版窗口已显示。\n\n" +
                        "可手动打开：\n" + executable +
                        "\n\n如果被杀毒软件拦截，请查看其安全提示。";
                    WriteLog(new IOException(text));
                    MessageBox.Show(text, "BlockCraft Launcher · 启动确认",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                return true;
            }
            catch (Exception error)
            {
                // After starting the new executable, leave it and its rollback
                // copy alone if the window acknowledgement cannot be read.
                WriteLog(error);
                return false;
            }
            finally { TryDeleteOwnedFile(marker); }
        }

        // Best-effort housekeeping happens ONLY after the new window has
        // rendered. An unconfirmed upgrade retains the rollback copy.
        static void BestEffortCleanup(Action cleanup)
        {
            // Never roll back a successfully relaunched EXE because disk
            // cleanup failed; log the reason and retry on a later launch.
            try { cleanup(); }
            catch (Exception error) { WriteLog(error); }
        }

        static void TryDeleteOwnedFile(string file)
        {
            try
            {
                if (File.Exists(file) && !Reparse(file)) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void TryDeleteOwnedDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory) && !Reparse(directory))
                    Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static bool IsOwnedBackupExe(string target, string candidate)
        {
            if (string.IsNullOrWhiteSpace(target) ||
                string.IsNullOrWhiteSpace(candidate)) return false;
            string full = Path.GetFullPath(target);
            string item = Path.GetFullPath(candidate);
            return string.Equals(Path.GetDirectoryName(full),
                    Path.GetDirectoryName(item), StringComparison.OrdinalIgnoreCase) &&
                Regex.IsMatch(Path.GetFileName(item),
                    "^" + Regex.Escape(Path.GetFileName(full)) +
                    @"\.blockcraft-previous-[a-f0-9]{32}\.exe$",
                    RegexOptions.IgnoreCase);
        }

        static void CleanupOwnedExeBackups(string target)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(target));
            foreach (var candidate in Directory.GetFiles(directory,
                Path.GetFileName(target) + ".blockcraft-previous-*.exe"))
                if (IsOwnedBackupExe(target, candidate))
                    TryDeleteOwnedFile(candidate);
        }

        internal static string OwnedStageDirectory(string staged, string updatesRoot = null)
        {
            if (string.IsNullOrWhiteSpace(staged)) return null;
            string legacy = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates"));
            string updates = string.IsNullOrWhiteSpace(updatesRoot) ?
                legacy : Path.GetFullPath(updatesRoot);
            bool custom = !string.Equals(updates, legacy, StringComparison.OrdinalIgnoreCase);
            // Custom stage roots can never be the drive root, an arbitrary
            // game folder, or a symlink. Only a named child "updates" is valid.
            if (custom &&
                (!string.Equals(Path.GetFileName(updates.TrimEnd('\\')), "updates",
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetDirectoryName(updates.TrimEnd('\\')),
                    Path.GetPathRoot(updates), StringComparison.OrdinalIgnoreCase)))
                return null;
            string current = Path.GetFullPath(staged);
            while (!string.IsNullOrWhiteSpace(current) &&
                current.StartsWith(updates + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(Path.GetDirectoryName(current), updates,
                    StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(Path.GetFileName(current),
                        @"^staged-[a-f0-9]{32}$", RegexOptions.IgnoreCase))
                {
                    string marker = Path.Combine(current, ".blockcraft-stage");
                    if (custom && (!File.Exists(marker) || Reparse(marker) ||
                        new FileInfo(marker).Length > 64 ||
                        File.ReadAllText(marker, Encoding.ASCII) != "BlockCraftLauncher update staging v1"))
                        return null;
                    return current;
                }
                current = Path.GetDirectoryName(current);
            }
            return null;
        }

        static void CleanupOwnedStage(string staged, string updatesRoot)
        {
            string directory = OwnedStageDirectory(staged, updatesRoot);
            if (directory != null) TryDeleteOwnedDirectory(directory);
        }

        static bool CacheIsInUse(string directory)
        {
            foreach (var process in Process.GetProcessesByName("BlockCraftLauncher.Wpf"))
            {
                using (process)
                {
                    try
                    {
                        string running = process.MainModule.FileName;
                        if (running.StartsWith(directory.TrimEnd('\\') + "\\",
                            StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (System.ComponentModel.Win32Exception) { return true; }
                    catch (InvalidOperationException) { return true; }
                }
            }
            return false;
        }

        static void CleanupOldOneFileCaches(string staged)
        {
            // Never walk Downloads, Desktop, game libraries or user-managed
            // directories: only launcher-created, manifest-bearing caches.
            var match = Regex.Match(Path.GetFileName(staged),
                @"^BlockCraftLauncher-(v\d+\.\d+\.\d+)-WPF-OneFile-Windows-x64\.exe$",
                RegexOptions.IgnoreCase);
            if (!match.Success) return;
            string keepVersion = match.Groups[1].Value;
            string cacheRoot = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher", "onefile");
            if (!Directory.Exists(cacheRoot) || Reparse(cacheRoot)) return;
            foreach (string candidate in Directory.GetDirectories(cacheRoot))
            {
                string name = Path.GetFileName(candidate);
                var version = Regex.Match(name,
                    @"^(v\d+\.\d+\.\d+)-[a-f0-9]{16}$", RegexOptions.IgnoreCase);
                if (!version.Success ||
                    string.Equals(version.Groups[1].Value, keepVersion,
                        StringComparison.OrdinalIgnoreCase) ||
                    Reparse(candidate) ||
                    !File.Exists(Path.Combine(candidate, ".blockcraft-ready")) ||
                    !File.Exists(Path.Combine(candidate, ManifestName)))
                    continue;
                if (!CacheIsInUse(candidate)) TryDeleteOwnedDirectory(candidate);
            }
        }

        static int WindowsError(Exception error) =>
            error is Win32Exception native ? native.NativeErrorCode : error.HResult & 0xffff;

        static bool IsTemporaryLock(Exception error)
        {
            if (!(error is IOException || error is UnauthorizedAccessException ||
                error is Win32Exception)) return false;
            int code = WindowsError(error);
            // A loaded image or a scanner may briefly deny access or deletion.
            return code == 5 || code == 32 || code == 33 || code == 1175 || code == 1224;
        }

        static void RetryWhileLocked(Action operation)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { operation(); return; }
                catch (Exception error) when (attempt < 20 && IsTemporaryLock(error))
                {
                    Thread.Sleep(200);
                }
            }
        }

        static bool CanUseRename(IOException error)
        {
            int code = WindowsError(error);
            return code == 1 || code == 50 || code == 87 ||
                code == 1175 || code == 1176 || code == 1177;
        }

        static void RenamePreparedFile(string pending, string destination, bool overwrite)
        {
            RetryWhileLocked(() =>
            {
                uint flags = MoveWriteThrough | (overwrite ? MoveReplaceExisting : 0u);
                if (!MoveFileEx(pending, destination, flags))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            });
        }

        static void InstallPreparedFile(string pending, string destination, bool overwrite)
        {
            if (overwrite && File.Exists(destination))
            {
                try
                {
                    RetryWhileLocked(() => File.Replace(pending, destination, null, true));
                    return;
                }
                catch (IOException error) when (CanUseRename(error) && File.Exists(pending))
                {
                    // The independently saved backup remains available even if
                    // ReplaceFile left the destination name temporarily absent.
                }
            }
            RenamePreparedFile(pending, destination, overwrite);
        }

        static string PendingBeside(string target) => Path.Combine(
            Path.GetDirectoryName(target), ".blockcraft-" + Guid.NewGuid().ToString("N") + ".tmp");

        static void ApplySingleExe(string target, string staged, string digest,
            bool simulateLaunchFailure = false, string updatesRoot = null)
        {
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !staged.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("单文件更新必须使用 EXE");
            CheckLauncherExe(target);
            if (Reparse(staged)) throw new InvalidDataException("更新来源不能是重解析链接");
            EnsureNoReparseParents(staged);
            ExpectHash(staged, digest);
            if (string.Equals(target, staged, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能使用同一个文件同时作为新旧版本");

            string next = PendingBeside(target);
            string backup = target + ".blockcraft-previous-" + Guid.NewGuid().ToString("N") + ".exe";
            bool backupReady = false;
            try
            {
                File.Copy(staged, next);
                ExpectHash(next, digest);
                RetryWhileLocked(() => File.Copy(target, backup));
                backupReady = true;
                InstallPreparedFile(next, target, overwrite: true);
                ExpectHash(target, digest);
                if (simulateLaunchFailure)
                    throw new IOException("CI 模拟新版启动自检失败");
                SmokeTest(target);
                if (Restart(target))
                    BestEffortCleanup(() =>
                    {
                        CleanupOwnedExeBackups(target);
                        CleanupOldOneFileCaches(staged);
                        CleanupOwnedStage(staged, updatesRoot);
                    });
            }
            catch (Exception installError)
            {
                if (backupReady)
                {
                    var change = new Changed { Destination = target, Backup = backup, Existed = true };
                    if (!Rollback(new[] { change }))
                        throw new IOException("未能恢复原 EXE，旧版备份保留在 " + backup +
                            "。安装失败：" + installError.Message, installError);
                }
                TryDeleteOwnedFile(backup);
                throw;
            }
            finally { TryDeleteOwnedFile(next); }
        }

        static string SafeRelative(string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
                relative.IndexOf(':') >= 0 || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                throw new InvalidDataException("更新包路径异常");
            string[] parts = relative.Replace('/', '\\').Split('\\');
            if (parts.Any(p => string.IsNullOrEmpty(p) || p == "." || p == ".." ||
                p.EndsWith(".", StringComparison.Ordinal) || p.EndsWith(" ", StringComparison.Ordinal)))
                throw new InvalidDataException("更新包包含路径逃逸");
            foreach (var part in parts)
            {
                string upper = part.Split('.')[0].ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(upper) ||
                    (upper.Length == 4 &&
                     (upper.StartsWith("COM") || upper.StartsWith("LPT")) &&
                     upper[3] >= '1' && upper[3] <= '9'))
                    throw new InvalidDataException("更新包包含 Windows 保留文件名");
            }
            if (new[] { "mods", "saves", "versions", "libraries", "accounts" }
                .Contains(parts[0], StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包不得包含游戏内容或账号目录");
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        static List<string> StageFiles(string stage)
        {
            if (!Directory.Exists(stage)) throw new IOException("解压后的更新目录不存在");
            EnsureNoReparseParents(stage);
            var found = new List<string>();
            var pending = new Stack<string>();
            pending.Push(stage);
            while (pending.Count > 0)
            {
                string dir = pending.Pop();
                if (Reparse(dir)) throw new InvalidDataException("拒绝更新包目录符号链接");
                foreach (var child in Directory.GetDirectories(dir))
                {
                    SafeRelative(child.Substring(stage.TrimEnd('\\').Length + 1));
                    pending.Push(child);
                }
                foreach (var file in Directory.GetFiles(dir))
                {
                    if (Reparse(file)) throw new InvalidDataException("拒绝更新包文件符号链接");
                    SafeRelative(file.Substring(stage.TrimEnd('\\').Length + 1));
                    found.Add(file);
                }
            }
            if (found.Count < 8 || found.Count > 500)
                throw new InvalidDataException("更新包文件数量不符合预期");
            return found;
        }

        static void VerifyStageManifest(string stage, List<string> files)
        {
            string manifest = Path.Combine(stage, ManifestName);
            if (!File.Exists(manifest)) throw new InvalidDataException("更新包缺少 SHA-256 清单");
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(manifest))
            {
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
                    throw new InvalidDataException("SHA-256 清单格式无效");
                string digest = line.Substring(0, 64);
                if (digest.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("哈希格式无效");
                string relative = SafeRelative(line.Substring(66));
                if (!known.Add(relative)) throw new InvalidDataException("校验清单含重复文件");
                string path = Path.Combine(stage, relative);
                ExpectHash(path, digest);
            }
            if (!known.Contains(WpfExe) || !known.Contains("BlockCraftLauncher.Updater.exe"))
                throw new InvalidDataException("更新包缺少 WPF 主程序或更新组件");
            foreach (var file in files)
            {
                string relative = SafeRelative(file.Substring(stage.TrimEnd('\\').Length + 1));
                // The original ZIP SHA256 verifies all nested files; the
                // packaging manifest explicitly covers top-level EXE/DLL.
                if (relative.IndexOf(Path.DirectorySeparatorChar) < 0 &&
                    (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) &&
                    !known.Contains(relative))
                    throw new InvalidDataException("发现未校验的顶层可执行文件：" + relative);
            }
        }

        static bool AtOrBelow(string path, string folder)
        {
            string parent = Path.GetFullPath(folder).TrimEnd('\\', '/');
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            return string.Equals(full, parent, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static void CheckZipLocations(string target, string stage, string updatesRoot,
            IEnumerable<string> files)
        {
            if (AtOrBelow(target, stage))
                throw new InvalidOperationException("安装目录与解压目录重叠。");
            if (!AtOrBelow(stage, target)) return;

            // A custom downloads folder can sit beside the EXE. Only permit
            // that layout for our own staged payload and keep its cache intact.
            string owned = OwnedStageDirectory(stage, updatesRoot);
            if (owned == null || !string.Equals(Path.GetDirectoryName(stage.TrimEnd('\\', '/')),
                    Path.Combine(owned, "unpacked"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("安装目录内的更新包须位于启动器下载缓存中。");
            string cache = Path.GetDirectoryName(owned);
            foreach (string file in files)
            {
                string relative = SafeRelative(file.Substring(stage.TrimEnd('\\', '/').Length + 1));
                string dest = Path.Combine(target, relative);
                if (AtOrBelow(dest, cache) || AtOrBelow(cache, dest))
                    throw new InvalidDataException("更新包与现有下载缓存目录冲突。");
            }
        }

        static void CopyIntoTarget(string stage, string target, List<string> files,
            string backupDir, List<Changed> changed)
        {
            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string relative = SafeRelative(file.Substring(stage.TrimEnd('\\', '/').Length + 1));
                string dest = Path.Combine(target, relative);
                if (File.Exists(dest) && Reparse(dest))
                    throw new IOException("目标包含重解析文件，拒绝覆盖");
                EnsureNoReparseParents(dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                var record = new Changed
                {
                    Destination = dest,
                    Existed = File.Exists(dest),
                    Backup = Path.Combine(backupDir, relative)
                };
                if (record.Existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(record.Backup));
                    RetryWhileLocked(() => File.Copy(dest, record.Backup, false));
                }

                string pending = PendingBeside(dest);
                try
                {
                    File.Copy(file, pending, false);
                    if (record.Existed) changed.Add(record);
                    InstallPreparedFile(pending, dest, record.Existed);
                    if (!record.Existed) changed.Add(record);
                }
                finally { TryDeleteOwnedFile(pending); }
            }
        }

        static bool Rollback(IEnumerable<Changed> changes)
        {
            bool restored = true;
            foreach (var change in changes.Reverse())
            {
                try
                {
                    if (!change.Existed)
                    {
                        RetryWhileLocked(() => File.Delete(change.Destination));
                        continue;
                    }
                    if (File.Exists(change.Destination) &&
                        string.Equals(Hash(change.Destination), Hash(change.Backup), StringComparison.Ordinal))
                        continue;
                    string pending = PendingBeside(change.Destination);
                    try
                    {
                        File.Copy(change.Backup, pending);
                        InstallPreparedFile(pending, change.Destination, overwrite: true);
                    }
                    finally { TryDeleteOwnedFile(pending); }
                }
                catch (Exception error)
                {
                    restored = false;
                    WriteLog(new IOException("恢复失败：" + change.Destination +
                        "，备份：" + change.Backup, error));
                }
            }
            return restored;
        }

        static void ApplyDirectory(string target, string stage, string digest,
            bool simulateLaunchFailure = false, string updatesRoot = null)
        {
            string main = Path.Combine(target, WpfExe);
            CheckLauncherExe(main);
            List<string> files = StageFiles(stage);
            CheckZipLocations(target, stage, updatesRoot, files);
            VerifyStageManifest(stage, files);
            // The full ZIP SHA-256 is also verified before extraction in WPF.
            // Here we recheck the staged executable manifest at commit time.
            if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("原始 ZIP 的 SHA-256 参数无效");

            string home = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher", "updates");
            Directory.CreateDirectory(home);
            string backup = Path.Combine(home, "rollback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var changed = new List<Changed>();
            try
            {
                CopyIntoTarget(stage, target, files, backup, changed);
                if (simulateLaunchFailure)
                    throw new IOException("CI 模拟新版 WPF 启动自检失败");
                SmokeTest(main);
                bool confirmed = Restart(main);
                if (confirmed)
                    BestEffortCleanup(() =>
                    {
                        // Only signed ZIP files are backed up; Minecraft
                        // worlds, mods and personal files are not touched.
                        TryDeleteOwnedDirectory(backup);
                        CleanupOwnedStage(stage, updatesRoot);
                    });
            }
            catch (Exception installError)
            {
                if (!Rollback(changed))
                    throw new IOException("ZIP 回滚未完成，旧版备份保留在 " + backup +
                        "。安装失败：" + installError.Message, installError);
                TryDeleteOwnedDirectory(backup);
                throw;
            }
        }

        static int SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "BlockCraft-Updater-Test-" +
                Guid.NewGuid().ToString("N"));
            try
            {
                string source = Path.Combine(root, "stage");
                string target = Path.Combine(root, "application");
                string backup = Path.Combine(root, "backup");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(backup);
                File.WriteAllText(Path.Combine(source, WpfExe), "new", Encoding.UTF8);
                File.WriteAllText(Path.Combine(source, "lib.dll"), "new-lib", Encoding.UTF8);
                File.WriteAllText(Path.Combine(target, WpfExe), "old", Encoding.UTF8);
                File.WriteAllText(Path.Combine(target, "world.txt"), "my world", Encoding.UTF8);
                var changed = new List<Changed>();
                CopyIntoTarget(source, target, StageFilesForSelfTest(source), backup, changed);
                if (File.ReadAllText(Path.Combine(target, WpfExe)) != "new" ||
                    File.ReadAllText(Path.Combine(target, "lib.dll")) != "new-lib")
                    throw new Exception("更新替换失败");
                if (!Rollback(changed) ||
                    File.ReadAllText(Path.Combine(target, WpfExe)) != "old" ||
                    File.Exists(Path.Combine(target, "lib.dll")) ||
                    File.ReadAllText(Path.Combine(target, "world.txt")) != "my world")
                    throw new Exception("文件级回滚未恢复原样");
                bool escaped = false;
                try { SafeRelative(@"..\..\evil.dll"); }
                catch (InvalidDataException) { escaped = true; }
                if (!escaped || SafeRelative("subfolder\\test.dll") == null)
                    throw new Exception("路径安全验证失效");

                // A fake executable is intentionally unable to pass the
                // post-install launch test. Verify that an EXE replacement
                // automatically returns the previous file byte-for-byte.
                string originalExe = Path.Combine(target, "launcher.exe");
                string incomingExe = Path.Combine(source, "incoming.exe");
                File.WriteAllText(originalExe, "old-single-exe", Encoding.ASCII);
                File.WriteAllText(incomingExe, "new-single-exe", Encoding.ASCII);
                int attempts = 0;
                RetryWhileLocked(() =>
                {
                    if (++attempts == 1) throw new Win32Exception(32);
                });
                if (attempts != 2) throw new Exception("文件锁释放后未重试");

                string pending = PendingBeside(originalExe);
                File.Copy(incomingExe, pending);
                RenamePreparedFile(pending, originalExe, overwrite: true);
                if (File.Exists(pending) || File.ReadAllText(originalExe) != "new-single-exe")
                    throw new Exception("同目录重命名未能原位替换文件");
                File.WriteAllText(originalExe, "old-single-exe", Encoding.ASCII);

                bool onefileRollback = false;
                try { ApplySingleExe(originalExe, incomingExe, Hash(incomingExe), true); }
                catch (IOException error) when (error.Message == "CI 模拟新版启动自检失败")
                { onefileRollback = true; }
                if (!onefileRollback || File.ReadAllText(originalExe) != "old-single-exe" ||
                    Directory.GetFiles(target, "*.blockcraft-previous-*.exe").Length != 0)
                    throw new Exception("单文件启动失败后未还原旧版并清理备份");
                string legacyBackup = originalExe + ".blockcraft-previous-" +
                    Guid.NewGuid().ToString("N") + ".exe";
                string notOurs = Path.Combine(target, "my-other-launcher.exe");
                File.WriteAllText(legacyBackup, "updater-created old version");
                File.WriteAllText(notOurs, "never delete");
                if (!IsOwnedBackupExe(originalExe, legacyBackup) ||
                    IsOwnedBackupExe(originalExe, notOurs) ||
                    OwnedStageDirectory(notOurs) != null)
                    throw new Exception("旧 EXE 清理目标范围不安全");
                CleanupOwnedExeBackups(originalExe);
                if (File.Exists(legacyBackup) || !File.Exists(notOurs))
                    throw new Exception("清理旧版 EXE 时删除了非启动器文件");

                // Custom download roots are never deleted wholesale, and a
                // named stage requires an exact marker before helper cleanup.
                string external = Path.Combine(root, "external-cache", "updates");
                string stageName = "staged-" + Guid.NewGuid().ToString("N");
                string customStage = Path.Combine(external, stageName);
                Directory.CreateDirectory(customStage);
                File.WriteAllText(Path.Combine(customStage, "personal-data.txt"),
                    "not eligible until marked", Encoding.ASCII);
                if (OwnedStageDirectory(customStage, external) != null)
                    throw new Exception("没有标记的自定义缓存不允许自动删除");
                File.WriteAllText(Path.Combine(customStage, ".blockcraft-stage"),
                    "BlockCraftLauncher update staging v1", Encoding.ASCII);
                if (OwnedStageDirectory(Path.Combine(customStage, "unpacked", "payload"),
                    external) != customStage ||
                    OwnedStageDirectory(Path.Combine(root, "unrelated"), external) != null)
                    throw new Exception("自定义下载路径的暂存目录边界验证失败");
                string unsafeFile = Path.Combine(external, "other-user-file.txt");
                File.WriteAllText(unsafeFile, "keep", Encoding.ASCII);
                CleanupOwnedStage(Path.Combine(customStage, "unpacked", "payload"), external);
                if (Directory.Exists(customStage) || !File.Exists(unsafeFile))
                    throw new Exception("清理自定义缓存时误删非启动器文件");

                // The directory updater must also restore DLLs and the main
                // WPF executable when its post-install smoke test fails.
                string install = Path.Combine(root, "full-install");
                string nestedCache = Path.Combine(install, "下载 缓存", "updates");
                string nestedStage = Path.Combine(nestedCache, stageName);
                string fullStage = Path.Combine(nestedStage, "unpacked", "payload");
                Directory.CreateDirectory(fullStage);
                File.WriteAllText(Path.Combine(nestedStage, ".blockcraft-stage"),
                    "BlockCraftLauncher update staging v1", Encoding.ASCII);
                var parts = new[] { WpfExe, "BlockCraftLauncher.Updater.exe",
                    "one.dll", "two.dll", "three.dll", "four.dll", "five.dll" };
                foreach (string part in parts)
                    File.WriteAllText(Path.Combine(fullStage, part), "incoming-" + part, Encoding.ASCII);
                File.WriteAllText(Path.Combine(install, WpfExe), "old-wpf", Encoding.ASCII);
                File.WriteAllText(Path.Combine(install, "world.txt"), "world-data", Encoding.ASCII);
                using (var writer = new StreamWriter(Path.Combine(fullStage, ManifestName), false, Encoding.ASCII))
                    foreach (string part in parts)
                        writer.WriteLine(Hash(Path.Combine(fullStage, part)) + "  " + part);
                string overlap = Path.Combine(fullStage, "下载 缓存", "updates", "other.txt");
                bool cacheProtected = false;
                try { CheckZipLocations(install, fullStage, nestedCache, new[] { overlap }); }
                catch (InvalidDataException) { cacheProtected = true; }
                if (!cacheProtected) throw new Exception("ZIP 更新不能覆盖自己的下载缓存");

                bool zipRollback = false;
                try { ApplyDirectory(install, fullStage, new string('a', 64), true, nestedCache); }
                catch (IOException error) when (error.Message == "CI 模拟新版 WPF 启动自检失败")
                { zipRollback = true; }
                if (!zipRollback ||
                    File.ReadAllText(Path.Combine(install, WpfExe)) != "old-wpf" ||
                    File.Exists(Path.Combine(install, "one.dll")) ||
                    File.ReadAllText(Path.Combine(install, "world.txt")) != "world-data")
                    throw new Exception("ZIP 更新失败后未自动恢复文件");

                // The real app writes a one-time acknowledgement only once its
                // MainWindow has rendered. Exercise that marker protocol with
                // a simulated WPF PID and a delayed asynchronous writer.
                string token = Guid.NewGuid().ToString("N");
                string marker = RelaunchFile(token);
                if (TryReadWindowReady(marker, pid => true))
                    throw new Exception("未启动时不得错误报告窗口已打开");
                var emitter = new Thread(() =>
                {
                    Thread.Sleep(170);
                    Directory.CreateDirectory(Path.GetDirectoryName(marker));
                    File.WriteAllText(marker, Process.GetCurrentProcess().Id +
                        "\n" + DateTime.UtcNow.ToString("o"));
                });
                emitter.Start();
                bool visible = AwaitWindow(marker, 3000,
                    pid => pid == Process.GetCurrentProcess().Id);
                emitter.Join();
                if (!visible || !File.Exists(marker))
                    throw new Exception("更新后窗口显示确认协议失效");
                File.Delete(marker);
                if (AwaitWindow(marker, 250, pid => true))
                    throw new Exception("缺少确认文件时仍然报告启动成功");
                return 0;
            }
            catch (Exception error) { WriteLog(error); return 40; }
            finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
        }

        static List<string> StageFilesForSelfTest(string root) => Directory.GetFiles(root).ToList();

        static string WriteLog(Exception error)
        {
            string folder = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "BlockCraftLauncher", "updates");
            string file = Path.Combine(folder, "update-error.log");
            try
            {
                Directory.CreateDirectory(folder);
                File.AppendAllText(file, DateTime.UtcNow.ToString("u") + "\n" +
                    error + "\n", Encoding.UTF8);
            }
            catch { }
            return file;
        }
    }
}
