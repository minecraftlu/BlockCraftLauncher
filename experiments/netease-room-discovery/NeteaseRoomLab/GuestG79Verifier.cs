using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Opt-in, local-only orchestrator for Opentransfer connect.exe -dump-auth.
    /// The external helper performs a live G79 login using a local cookie file.
    /// stdout can contain a LIVE LOGIN TOKEN: we read it transiently only to
    /// classify exit/status, never persist, echo, copy or upload it.
    /// No room entry or game connection is performed.
    /// </summary>
    public static class GuestG79Verifier
    {
        private const int MaxCapturedCharacters = 8192;
        private const int TimeoutMilliseconds = 90000;
        private const string ReportSuccess =
            "G79_HELPER_LOGIN_ACCEPTED: 第三方认证组件报告移动端 g79 游戏登录成功。"
            + " 本机不会保存或显示它返回的游戏 token。"
            + " 这不代表房间已授权、加入房间或进入 Minecraft 世界。";

        public static string GetConfigPath()
        {
            return Path.Combine(GuestEmailAuth.GetWorkspace(), "g79-check-config.json");
        }

        public static string BuildConfig(string localCookiePath)
        {
            if (String.IsNullOrWhiteSpace(localCookiePath) ||
                !Path.IsPathRooted(localCookiePath))
                throw new ArgumentException("An absolute local cookie file is required.");

            // DO NOT embed Cookie JSON, token, or email in the config.
            var values = new Dictionary<string, object>
            {
                { "auth_mode", "g79" },
                { "cookie_file", localCookiePath },
                { "transfer", true },
                { "transfer_server", "127.0.0.1" },
                { "log_level", "error" }
            };
            return new JavaScriptSerializer().Serialize(values);
        }

        public static string ClassifyResult(int exitCode, string stdout,
            bool capturedOutputOverflow)
        {
            if (capturedOutputOverflow)
                return "G79_CHECK_INCONCLUSIVE: 认证组件输出过长，已丢弃，不展示任何凭据。";
            if (exitCode != 0)
                return "G79_HELPER_LOGIN_FAILED (exit code " + exitCode
                       + "): 第三方认证组件没有完成 g79 登录。"
                       + " 可能需要登录验证、实名审核或网络排障，不代表账号密码错误。";

            if (String.IsNullOrWhiteSpace(stdout))
                return "G79_CHECK_INCONCLUSIVE: 认证组件正常退出，但未返回可验证的 g79 状态。";

            bool uid = false, mode = false, platform = false, hasTokenLine = false;
            foreach (string sourceLine in stdout.Split('\n'))
            {
                string line = sourceLine.Trim();
                if (line.StartsWith("uid=", StringComparison.Ordinal))
                {
                    uint parsed;
                    uid = uint.TryParse(line.Substring(4), out parsed) && parsed > 0;
                }
                else if (line == "auth_mode=g79") mode = true;
                else if (line == "platform=2") platform = true;
                else if (line.StartsWith("login_token=", StringComparison.Ordinal))
                {
                    // Never return the token or a substring of it.
                    hasTokenLine = line.Length > "login_token=".Length;
                }
            }
            return uid && mode && platform && hasTokenLine
                ? ReportSuccess
                : "G79_CHECK_INCONCLUSIVE: 程序退出正常，但没有完整的移动端 g79 登录字段。"
                  + " 本次不判断游戏授权成功。";
        }

        public static async Task<string> VerifyAsync(string helperPath)
        {
            if (!GuestEmailAuth.IsAllowedHelperPath(helperPath) ||
                !File.Exists(helperPath))
                return "G79_CHECK_BLOCKED: 未选择可信的 connect.exe。";

            if (!GuestEmailAuth.CredentialFileExists())
                return "G79_CHECK_BLOCKED: 未找到邮箱登录阶段保存的本地 Cookie。"
                       + " 请先完成第二账号邮箱登录。";

            GuestEmailAuth.PreparePrivateWorkspace();
            string config = GetConfigPath();
            Process process = null;

            try
            {
                // Only the pathname to the cookie is stored, never its bytes.
                // Existing directory has per-user/SYSTEM ACLs.
                File.WriteAllText(config, BuildConfig(GuestEmailAuth.GetCredentialFile()),
                    new UTF8Encoding(false));

                var start = new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(helperPath),
                    Arguments = "-dump-auth -c \"" + config + "\"",
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath)),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ErrorDialog = false
                };
                process = new Process { StartInfo = start };
                if (!process.Start())
                    return "G79_CHECK_FAILED: 认证助手进程未启动。";

                // Drain both streams concurrently to prevent child deadlocks.
                // stderr may contain sensitive credentials; discard all of it.
                Task<string> outputTask = Task.Run(() =>
                    ReadLimited(process.StandardOutput, MaxCapturedCharacters));
                Task<string> errorsTask = Task.Run(() =>
                    ReadLimited(process.StandardError, 0));
                Task exitTask = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(exitTask, Task.Delay(TimeoutMilliseconds)) != exitTask)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    try { await exitTask; } catch (Exception) { }
                    await Task.WhenAll(outputTask, errorsTask);
                    return "G79_CHECK_TIMEOUT: 认证组件未在 90 秒内完成；已终止验证进程。";
                }
                await exitTask;
                string output = await outputTask;
                await errorsTask;
                bool overflow = output.Length > MaxCapturedCharacters;
                return ClassifyResult(process.ExitCode, output, overflow);
            }
            catch (Exception)
            {
                // Do not concatenate exception messages: they may quote helper
                // arguments, credential paths, server responses or tokens.
                return "G79_CHECK_FAILED: 无法完成本地 g79 验证。请确认组件与网络。";
            }
            finally
            {
                if (process != null) process.Dispose();
                try { if (File.Exists(config)) File.Delete(config); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string ReadLimited(StreamReader input, int limit)
        {
            var text = new StringBuilder(Math.Min(4096, limit + 1));
            char[] buffer = new char[1024];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length <= limit)
                {
                    int take = Math.Min(count, limit + 1 - text.Length);
                    if (take > 0) text.Append(buffer, 0, take);
                }
                Array.Clear(buffer, 0, buffer.Length);
            }
            return text.ToString();
        }
    }
}
