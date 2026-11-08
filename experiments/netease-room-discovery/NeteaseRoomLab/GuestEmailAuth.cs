using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Launches a user-selected, external console login helper with arguments only.
    /// This app NEVER asks for the email/password or reads the resulting cookie.
    /// A credential file by itself is not proof of NetEase room authorization.
    ///
    /// WARNING: any third-party helper handling credentials has its own security
    /// and terms-of-service risks. Do not download or start it silently.
    /// </summary>
    public static class GuestEmailAuth
    {
        public const string HelperFilename = "connect.exe";
        public const string HelperMode = "email";

        public static bool IsAllowedHelperPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return String.Equals(Path.GetFileName(path), HelperFilename,
                    StringComparison.OrdinalIgnoreCase)
                    && String.Equals(Path.GetExtension(path), ".exe",
                    StringComparison.OrdinalIgnoreCase)
                    && Path.IsPathRooted(path);
            }
            catch (ArgumentException) { return false; }
        }

        public static string PreviewArgs(string credentialFile, string dataDir)
        {
            if (String.IsNullOrWhiteSpace(credentialFile) ||
                String.IsNullOrWhiteSpace(dataDir) ||
                credentialFile.IndexOf('"') >= 0 || dataDir.IndexOf('"') >= 0)
                throw new ArgumentException("Invalid local credential paths.");

            return "-login -login-method email -cookie-out \"" + credentialFile
                + "\" -auth-data-dir \"" + dataDir + "\"";
        }

        public static string GetWorkspace()
        {
            return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "NeteaseAuthExperiment");
        }

        public static string GetCredentialFile()
        {
            return Path.Combine(GetWorkspace(), "email-guest-credential.json");
        }

        public static void PreparePrivateWorkspace()
        {
            string root = GetWorkspace();
            var current = WindowsIdentity.GetCurrent();
            var sid = current == null ? null : current.User;
            if (sid == null) throw new InvalidOperationException("No Windows user identity.");

            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            const InheritanceFlags inheritance =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));

            if (!Directory.Exists(root)) Directory.CreateDirectory(root, acl);
            else Directory.SetAccessControl(root, acl);

            string deviceDir = Path.Combine(root, "device");
            if (!Directory.Exists(deviceDir)) Directory.CreateDirectory(deviceDir, acl);
            else Directory.SetAccessControl(deviceDir, acl);
        }

        public static string GetHelperSha256(string helperPath)
        {
            if (!IsAllowedHelperPath(helperPath) || !File.Exists(helperPath))
                throw new FileNotFoundException("Please select a trusted connect.exe.");

            using (var stream = File.OpenRead(helperPath))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", "").ToLowerInvariant();
        }

        public static Process StartEmailLogin(string helperPath)
        {
            if (!IsAllowedHelperPath(helperPath) || !File.Exists(helperPath))
                throw new FileNotFoundException("No selected connect.exe.");

            PreparePrivateWorkspace();
            // UseShellExecute opens a separate interactive console. Neither email
            // nor password is placed into command arguments or captured by WPF.
            var info = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(helperPath),
                Arguments = PreviewArgs(GetCredentialFile(),
                    Path.Combine(GetWorkspace(), "device")),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath)),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                ErrorDialog = false
            };
            var process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("Unable to start helper.");
            return process;
        }

        public static bool CredentialFileExists()
        {
            try
            {
                var file = new FileInfo(GetCredentialFile());
                return file.Exists && file.Length > 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static bool DeleteCredentialFile()
        {
            // Only this experiment's expected cookie output; the helper's device
            // binding files (if any) are NOT removed by this function.
            var path = GetCredentialFile();
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }

        public static string ExplainExit(int exitCode, bool credentialFileFound)
        {
            if (exitCode != 0)
                return "AUTH_HELPER_EXITED_WITH_ERROR: 认证助手报告失败；请只在本机查看详细原因。";
            if (!credentialFileFound)
                return "AUTH_HELPER_EXITED_NO_CREDENTIAL: 认证助手已退出，但未发现本地凭据文件。";
            return "LOCAL_CREDENTIAL_FILE_PRESENT_UNVERIFIED: 认证助手已保存本地文件。"
                   + " 这不是官方账号有效性验证，更不是进入房间成功。";
        }
    }
}
