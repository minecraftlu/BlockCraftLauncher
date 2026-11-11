using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// An update launch is not considered visible merely because Process.Start
    /// returned. The newly started WPF application writes this acknowledgement
    /// only AFTER the main window's ContentRendered event.
    /// No secrets, account details or Minecraft paths are written.
    /// </summary>
    internal static class LauncherRelaunchSignal
    {
        internal const string HandoffFlag = "--update-handoff";
        internal const string HandoffReady = "handoff-v1";
        internal const string Ready = "ready";
        internal const string Rejected = "rejected";
        internal const string Failed = "failed";
        internal const string InstalledOneFile = "installed-onefile";
        internal const string InstalledZip = "installed-zip";

        internal static string FilePath(string token)
        {
            if (string.IsNullOrWhiteSpace(token) ||
                !Regex.IsMatch(token, @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant))
                throw new ArgumentException("更新后的启动确认令牌无效", nameof(token));
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates", "relaunch-" + token + ".ok");
        }

        internal static string ReadToken(string[] args)
        {
            if (args == null) return null;
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "--after-update", StringComparison.OrdinalIgnoreCase))
                {
                    var token = args[i + 1];
                    return token != null &&
                        Regex.IsMatch(token, @"\A[a-f0-9]{32}\z", RegexOptions.CultureInvariant)
                        ? token : null;
                }
            return null;
        }

        internal static void AcknowledgeVisibleWindow(string token, bool handoff = false)
        {
            // The first line identifies the actual WPF child, not its bootstrap.
            string message = Process.GetCurrentProcess().Id.ToString() + "\n" +
                DateTime.UtcNow.ToString("o");
            if (handoff) message += "\n" + HandoffReady;
            WriteSignal(FilePath(token), message);
        }

        internal static bool TryReadReadyProcess(string path, bool handoff, out int pid)
        {
            pid = 0;
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 256) return false;
                var lines = File.ReadAllLines(path);
                return lines.Length >= 2 && int.TryParse(lines[0], out pid) && pid > 0 &&
                    DateTime.TryParse(lines[1], out _) &&
                    (!handoff || (lines.Length >= 3 && lines[2] == HandoffReady));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void WriteHandoffResult(string token, string status, string detail)
        {
            string message = detail ?? "";
            if (message.Length > 4096) message = message.Substring(0, 4096);
            WriteSignal(ResultPath(token), status + "\n" + message);
        }

        internal static bool TryReadHandoffResult(string token, out string status, out string detail)
        {
            status = detail = null;
            string path = ResultPath(token);
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 16384) return false;
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                int split = text.IndexOf('\n');
                if (split <= 0) return false;
                status = text.Substring(0, split);
                detail = text.Substring(split + 1);
                return status == Ready || status == Rejected || status == Failed ||
                    status == InstalledOneFile || status == InstalledZip;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void RemoveReadySignal(string token) => TryDeleteSignal(FilePath(token));

        internal static void RemoveHandoffResult(string token) => TryDeleteSignal(ResultPath(token));

        static string ResultPath(string token) => Path.ChangeExtension(FilePath(token), ".result");

        static void WriteSignal(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, text, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            finally { TryDeleteSignal(staging); }
        }

        static void TryDeleteSignal(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void RunOfflineSelfTest()
        {
            var token = Guid.NewGuid().ToString("N");
            if (ReadToken(new[] { "--after-update", token }) != token)
                throw new InvalidOperationException("更新启动令牌没有正确传递到 WPF");
            if (ReadToken(new[] { "--after-update", "../escape" }) != null)
                throw new InvalidOperationException("更新启动令牌没有拒绝非法路径");
            var path = FilePath(token);
            try
            {
                AcknowledgeVisibleWindow(token);
                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2 ||
                    lines[0] != Process.GetCurrentProcess().Id.ToString() ||
                    !DateTime.TryParse(lines[1], out _) || TryReadReadyProcess(path, true, out _))
                    throw new InvalidDataException("旧版启动确认不得触发窗口交接");
                AcknowledgeVisibleWindow(token, handoff: true);
                if (!TryReadReadyProcess(path, true, out var pid) || pid != Process.GetCurrentProcess().Id)
                    throw new InvalidDataException("窗口交接未识别新版主窗口");
                WriteHandoffResult(token, InstalledOneFile, "C:\\Launcher\\BlockCraft.exe");
                if (!TryReadHandoffResult(token, out var status, out var target) ||
                    status != InstalledOneFile || target != "C:\\Launcher\\BlockCraft.exe")
                    throw new InvalidDataException("新版无法读取安装结果");
            }
            finally
            {
                TryDeleteSignal(path);
                RemoveHandoffResult(token);
            }
        }
    }
}
