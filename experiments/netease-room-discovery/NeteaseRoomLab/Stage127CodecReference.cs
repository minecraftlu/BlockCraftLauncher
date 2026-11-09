using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Checks whether a third-party NetEase codec has been documented for
    /// an explicitly entered number. No room metadata, Cookie, login token,
    /// socket or Minecraft packet is ever accessed in this offline workflow.
    /// </summary>
    public static class Stage127CodecReference
    {
        private const int TimeoutMilliseconds = 10000;
        private const string Found = "NETEASE_CODEC_REFERENCE_FOUND_GAME_NOT_JOINED";
        private const string Unverified = "NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED";

        public static bool IsAvailable
        {
            get
            {
                return typeof(Stage127CodecReference).Assembly.GetManifestResourceInfo(
                    Stage12OfflineBridge.WorkerResource) != null;
            }
        }

        public static bool TryNormalizeVersion(string input, out string version)
        {
            version = null;
            string text = (input ?? "").Trim();
            if (text.Length == 0 || text.Length > 5 || text[0] == '0')
                return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
            }
            uint value;
            if (!UInt32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture,
                out value) || value == 0 || value > 99999)
                return false;
            version = text;
            return true;
        }

        public static string Classify(string version, int exitCode, string output)
        {
            string normalized;
            if (!TryNormalizeVersion(version, out normalized))
                return "STAGE12_7_PROTOCOL_INPUT_INVALID：请输入单独确认的 Bedrock 游戏协议号，不能填写房间版本标记。";
            string token = (output ?? "").TrimEnd('\r', '\n');
            if (exitCode == 0 && String.Equals(token, Found, StringComparison.Ordinal))
                return "STAGE12_7_NETEASE_CODEC_REFERENCE_ONLY_GAME_NOT_JOINED："
                    + "公开参考资料包含此协议号的网易专用编解码实现；"
                    + "这不证明当前手机房间使用该版本，也不代表身份认证通过或已进入世界。";
            if (exitCode == 0 && String.Equals(token, Unverified, StringComparison.Ordinal))
                return "STAGE12_7_NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED："
                    + "当前固定参考表没有该协议号的网易专用编解码实现；"
                    + "普通国际版协议或大厅元数据不能代替网易游戏协议证据。";
            return "STAGE12_7_PROTOCOL_CHECK_FAILED_GAME_NOT_JOINED："
                + "本地协议资料检查未通过，未执行游戏登录。";
        }

        public static async Task<string> RunAsync(string version, CancellationToken cancellationToken)
        {
            string normalized;
            if (!TryNormalizeVersion(version, out normalized))
                return Classify(version, -1, "");
            if (!IsAvailable)
                return "STAGE12_7_PROTOCOL_CHECK_UNAVAILABLE：此启动器没有内嵌协议检查工具。";
            if (cancellationToken.IsCancellationRequested)
                return "STAGE12_7_PROTOCOL_CHECK_CANCELLED：本地检查已取消。";

            string folder = Path.Combine(Path.GetTempPath(),
                "BlockCraft-Stage127-" + Guid.NewGuid().ToString("N"));
            Process process = null;
            try
            {
                Directory.CreateDirectory(folder);
                string executable = Path.Combine(folder, "Stage12Worker.exe");
                using (Stream resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(Stage12OfflineBridge.WorkerResource))
                {
                    if (resource == null) return Classify(normalized, -1, "");
                    using (var file = new FileStream(executable, FileMode.CreateNew,
                        FileAccess.Write, FileShare.None))
                        resource.CopyTo(file);
                }

                process = new Process { StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-offline-codec-reference " + normalized,
                    WorkingDirectory = folder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                }};
                if (!process.Start()) return Classify(normalized, -1, "");
                Process current = process;
                using (cancellationToken.Register(() => Stop(current)))
                {
                    Task<string> text = current.StandardOutput.ReadToEndAsync();
                    bool exited = await Task.Run(() => current.WaitForExit(TimeoutMilliseconds));
                    if (!exited)
                    {
                        Stop(current);
                        return "STAGE12_7_PROTOCOL_CHECK_TIMEOUT_GAME_NOT_JOINED：本机检查超时。";
                    }
                    if (cancellationToken.IsCancellationRequested)
                        return "STAGE12_7_PROTOCOL_CHECK_CANCELLED：本地检查已取消。";
                    string output = await text;
                    if (output.Length > 256) return Classify(normalized, -1, "");
                    return Classify(normalized, current.ExitCode, output);
                }
            }
            catch (Exception)
            {
                return Classify(normalized, -1, "");
            }
            finally
            {
                if (process != null) { Stop(process); process.Dispose(); }
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (Exception) { }
            }
        }

        private static void Stop(Process process)
        {
            try { if (process != null && !process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
