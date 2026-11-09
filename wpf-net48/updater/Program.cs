using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            string restart = null;
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
                if (digest.Length != 64 || digest.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("更新包 SHA-256 无效");
                restart = mode == "zip" ? Path.Combine(target, WpfExe) : target;
                if (!WaitForPreviousProcess(pid, WaitSeconds))
                    throw new IOException("原启动器仍在运行；没有覆盖任何文件。请关闭后重试。");
                if (mode == "onefile")
                    ApplySingleExe(target, staged, digest);
                else
                    ApplyDirectory(target, staged, digest);
                return 0;
            }
            catch (Exception error)
            {
                string log = WriteLog(error);
                try
                {
                    MessageBox.Show("BlockCraft Launcher 更新失败，原有程序已尽可能恢复：\n" +
                        error.GetBaseException().Message + "\n\n诊断记录：\n" + log +
                        "\n\n不影响 Minecraft 游戏和存档。",
                        "BlockCraft Launcher 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { /* no UI in headless sessions */ }
                return 1;
            }
        }

        static Dictionary<string, string> ParseArguments(string[] args)
        {
            if (args.Length != 11 || args[0] != "--apply")
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
            while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
            {
                if (Reparse(current)) throw new IOException("拒绝在符号链接或联接目录执行更新");
                var parent = Path.GetDirectoryName(current.TrimEnd('\\'));
                if (parent == current) break;
                current = parent;
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

        static void Restart(string executable)
        {
            var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            });
            if (process == null) throw new IOException("无法重新打开更新后的启动器");
            process.Dispose();
        }

        static void ApplySingleExe(string target, string staged, string digest)
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

            string nonce = Guid.NewGuid().ToString("N");
            string next = target + ".blockcraft-update-" + nonce + ".tmp";
            string backup = target + ".blockcraft-previous-" + nonce + ".exe";
            bool replaced = false;
            try
            {
                File.Copy(staged, next);
                ExpectHash(next, digest);
                File.Replace(next, target, backup);
                replaced = true;
                ExpectHash(target, digest);
                SmokeTest(target);
                Restart(target);
                // Keep previous EXE for manual rollback if later needed.
            }
            catch
            {
                if (replaced && File.Exists(backup))
                    File.Copy(backup, target, true);
                throw;
            }
            finally
            {
                try { if (File.Exists(next)) File.Delete(next); } catch { }
            }
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
                if ((file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) &&
                    !known.Contains(relative))
                    throw new InvalidDataException("发现未校验的可执行文件：" + relative);
            }
        }

        static List<Changed> CopyIntoTarget(string stage, string target,
            List<string> files, string backupDir)
        {
            var changed = new List<Changed>();
            try
            {
                foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string relative = SafeRelative(file.Substring(stage.TrimEnd('\\').Length + 1));
                    string dest = Path.Combine(target, relative);
                    if (File.Exists(dest) && Reparse(dest))
                        throw new IOException("目标包含重解析文件，拒绝覆盖");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    EnsureNoReparseParents(dest);
                    var record = new Changed
                    {
                        Destination = dest,
                        Existed = File.Exists(dest),
                        Backup = Path.Combine(backupDir, relative)
                    };
                    if (record.Existed)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(record.Backup));
                        File.Copy(dest, record.Backup, false);
                    }
                    changed.Add(record);
                    string temp = dest + ".blockcraft-pending";
                    if (File.Exists(temp)) throw new IOException("目标存在未完成的旧更新，拒绝继续");
                    try
                    {
                        File.Copy(file, temp, false);
                        if (record.Existed) File.Replace(temp, dest, null);
                        else File.Move(temp, dest);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                return changed;
            }
            catch
            {
                Rollback(changed);
                throw;
            }
        }

        static void Rollback(IEnumerable<Changed> changes)
        {
            foreach (var change in changes.Reverse())
            {
                try
                {
                    if (change.Existed && File.Exists(change.Backup))
                        File.Copy(change.Backup, change.Destination, true);
                    else if (!change.Existed && File.Exists(change.Destination))
                        File.Delete(change.Destination);
                }
                catch (Exception error) { WriteLog(error); }
            }
        }

        static void ApplyDirectory(string target, string stage, string digest)
        {
            string main = Path.Combine(target, WpfExe);
            CheckLauncherExe(main);
            if (string.Equals(target, stage, StringComparison.OrdinalIgnoreCase) ||
                stage.StartsWith(target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("更新包不得放在当前启动器目录内部");
            List<string> files = StageFiles(stage);
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
            List<Changed> changed = null;
            try
            {
                changed = CopyIntoTarget(stage, target, files, backup);
                SmokeTest(main);
                Restart(main);
                // Retain backup rather than touching user-managed game files.
            }
            catch
            {
                if (changed != null) Rollback(changed);
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
                var changed = CopyIntoTarget(source, target, StageFilesForSelfTest(source), backup);
                if (File.ReadAllText(Path.Combine(target, WpfExe)) != "new" ||
                    File.ReadAllText(Path.Combine(target, "lib.dll")) != "new-lib")
                    throw new Exception("更新替换失败");
                Rollback(changed);
                if (File.ReadAllText(Path.Combine(target, WpfExe)) != "old" ||
                    File.Exists(Path.Combine(target, "lib.dll")) ||
                    File.ReadAllText(Path.Combine(target, "world.txt")) != "my world")
                    throw new Exception("文件级回滚未恢复原样");
                bool escaped = false;
                try { SafeRelative(@"..\..\evil.dll"); }
                catch (InvalidDataException) { escaped = true; }
                if (!escaped || SafeRelative("subfolder\\test.dll") == null)
                    throw new Exception("路径安全验证失效");
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
                    error.GetType().Name + ": " + error.Message + "\n", Encoding.UTF8);
            }
            catch { }
            return file;
        }
    }
}
