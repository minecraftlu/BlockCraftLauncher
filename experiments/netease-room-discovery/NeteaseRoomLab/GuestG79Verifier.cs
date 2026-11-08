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

            // Work only with fixed, expected field names. The values from the
            // third-party helper may be private; never echo them into the UI,
            // logs, exception messages or a diagnostics report.
            bool uid = false, mode = false, platform = false, hasTokenLine = false;
            // Keep a bounded, non-secret classification of the mode field.
            // Never echo arbitrary values supplied by the helper.
            string modeCategory = "missing";
            foreach (string sourceLine in stdout.Split('\n'))
            {
                string line = sourceLine.Trim().TrimStart('\uFEFF');
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string name = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (String.Equals(name, "uid", StringComparison.OrdinalIgnoreCase))
                {
                    uint parsed;
                    uid = uint.TryParse(value, out parsed) && parsed > 0;
                }
                else if (String.Equals(name, "auth_mode", StringComparison.OrdinalIgnoreCase))
                {
                    mode = String.Equals(value, "g79", StringComparison.OrdinalIgnoreCase);
                    if (mode) modeCategory = "g79";
                    else if (String.Equals(value, "x19", StringComparison.OrdinalIgnoreCase))
                        modeCategory = "x19";
                    else if (String.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
                        modeCategory = "auto";
                    else modeCategory = "other";
                }
                else if (String.Equals(name, "platform", StringComparison.OrdinalIgnoreCase))
                    platform = value == "2";
                else if (String.Equals(name, "login_token", StringComparison.OrdinalIgnoreCase))
                {
                    // Only presence is tracked, NEVER the token value.
                    hasTokenLine = value.Length > 0;
                }
            }
            if (uid && mode && platform && hasTokenLine)
                return ReportSuccess;

            // The published helper source appears to choose LoginG79 when the
            // caller supplies auth_mode=g79. However, the real user's helper
            // omitted auth_mode from its output. Preserve this as a separate
            // evidence level, not as proof of G79 or lobby admission.
            if (uid && platform && hasTokenLine && modeCategory == "missing")
                return "PE_SESSION_REPORTED_MODE_UNCONFIRMED: 第三方助手报告了有效格式的"
                    + " PE 平台用户会话信息，但没有返回可确认的 auth_mode 字段。"
                    + " 不需要重复登录；不能据此声称已确认 G79 授权或进入网易房间。"
                    + " 下一步需要合法的房间准入回执和授权信令资料。";

            // A safe field-presence report makes malformed/missing helper
            // output actionable without requiring users to disclose tokens.
            // No UID, token, nickname, email or raw stdout is copied here.
            return "G79_CHECK_INCONCLUSIVE: 认证助手正常退出，但以下字段未全部满足验证条件。"
                + " 字段诊断：uid_valid=" + (uid ? "yes" : "no")
                + ", auth_mode_g79=" + (mode ? "yes" : "no")
                + ", mode_category=" + modeCategory
                + ", platform_pe=" + (platform ? "yes" : "no")
                + ", token_present=" + (hasTokenLine ? "yes" : "no")
                + "。mode_category 仅用于分类，不包含原始值。"
                + " 请只分享这些字段状态，不要分享原始 -dump-auth 输出。";
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
