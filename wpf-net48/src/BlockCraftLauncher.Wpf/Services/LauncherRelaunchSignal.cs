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

        internal static void AcknowledgeVisibleWindow(string token)
        {
            string path = FilePath(token);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // The first line is the actual WPF process ID, not the transient
            // onefile unpacking/bootstrap process.
            string message = Process.GetCurrentProcess().Id.ToString() + "\n" +
                DateTime.UtcNow.ToString("o");
            string staging = path + ".tmp";
            try
            {
                File.WriteAllText(staging, message, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(staging, path);
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); }
                catch (IOException) { }
            }
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
                    !DateTime.TryParse(lines[1], out _))
                    throw new InvalidDataException("主窗口确认文件格式不正确");
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
            }
        }
    }
}
