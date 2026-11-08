using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Integrates the official NetEase Minecraft China PC Bedrock-interoperable
    /// edition as a separate external client. No NetEase authentication,
    /// room APIs, game files, tokens or proprietary protocols are accessed.
    /// </summary>
    public static class NeteaseBedrockSupport
    {
        public const string OfficialWebsite = "https://minecraft.com.cn/";

        /// <summary>
        /// The executable is selected explicitly by the user after installation.
        /// Never guess a process path from arbitrary uninstall registry values.
        /// </summary>
        public static string ValidateSelectedLauncher(string selection)
        {
            if (string.IsNullOrWhiteSpace(selection))
                throw new ArgumentException("尚未选择网易官方启动器。请先安装客户端，再选择其启动程序 EXE。");
            string file = Path.GetFullPath(selection.Trim().Trim('"'));
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                file.StartsWith(@"\\", StringComparison.Ordinal) ||
                !Path.IsPathRooted(file))
                throw new ArgumentException("只能选择本机已安装的 Windows 启动器 EXE 文件。");
            if (!File.Exists(file))
                throw new FileNotFoundException("找不到所选启动器，请检查它是否已更新或移动。", file);
            return file;
        }

        public static void LaunchSelectedLauncher(string selection)
        {
            var file = ValidateSelectedLauncher(selection);
            Process.Start(new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file)
            });
        }

        public static void OpenOfficialWebsite()
        {
            Process.Start(new ProcessStartInfo(OfficialWebsite) { UseShellExecute = true });
        }

        public static bool IsSupportedWindowsVersion()
        {
            // The WPF launcher is Windows-only. OSVersion can be affected by
            // application compatibility shims, so this is informational.
            return Environment.OSVersion.Platform == PlatformID.Win32NT &&
                Environment.OSVersion.Version.Major >= 10;
        }

        static bool LooksLikeNeteaseMinecraft(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName)) return false;
            return displayName.IndexOf("我的世界", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   displayName.IndexOf("网易发烧", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   displayName.IndexOf("发烧游戏平台", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   displayName.IndexOf("网易基岩互通", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Read display names only from Windows uninstall metadata to provide
        /// installation hints. Never execute the recorded UninstallString,
        /// InstallLocation or any untrusted registry-provided command.
        /// </summary>
        public static IReadOnlyList<string> DetectInstalledClients()
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var uninstall = root.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false))
                        {
                            if (uninstall == null) continue;
                            foreach (var subName in uninstall.GetSubKeyNames())
                            {
                                try
                                {
                                    using (var sub = uninstall.OpenSubKey(subName, false))
                                    {
                                        var name = sub?.GetValue("DisplayName") as string;
                                        if (LooksLikeNeteaseMinecraft(name) &&
                                            name.Length < 150 && found.Count < 12)
                                            found.Add(name);
                                    }
                                }
                                catch (Exception ex) when (
                                    ex is System.Security.SecurityException ||
                                    ex is UnauthorizedAccessException ||
                                    ex is IOException ||
                                    ex is ArgumentException)
                                {
                                    // Registry entries are advisory only.
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (
                        ex is System.Security.SecurityException ||
                        ex is UnauthorizedAccessException ||
                        ex is IOException ||
                        ex is ArgumentException)
                    {
                        // A restricted registry view does not prevent use of
                        // the official site or user-selected executable.
                    }
                }
            }
            return new List<string>(found);
        }

        public static string DiagnosticSummary(string selectedExe)
        {
            var lines = new List<string>();
            lines.Add(IsSupportedWindowsVersion()
                ? "操作系统：Windows 10/11 兼容性检测通过（请以网易官方要求为准）。"
                : "操作系统：可能不符合 Windows 10/11 要求，请核对官方客户端支持情况。");

            if (string.IsNullOrWhiteSpace(selectedExe))
                lines.Add("已保存启动器：未指定。");
            else
            {
                try
                {
                    ValidateSelectedLauncher(selectedExe);
                    lines.Add("已保存启动器：路径有效，可尝试启动（不代表已安装正确的游戏版本）。");
                }
                catch (Exception)
                {
                    lines.Add("已保存启动器：文件不存在或格式不符，请重新选择。");
                }
            }

            var hints = DetectInstalledClients();
            lines.Add(hints.Count == 0
                ? "已安装程序：没有识别到网易相关安装记录；并不代表客户端一定未安装。"
                : "已安装程序：发现可能相关的记录（不代表已验证为基岩互通版）：");
            foreach (var hint in hints)
                lines.Add("  · " + hint);
            lines.Add("注意：Java 国际版不能使用本入口直接进入网易手机房间。");
            return string.Join(Environment.NewLine, lines);
        }

        public static void RunSelfTests()
        {
            if (OfficialWebsite != "https://minecraft.com.cn/" ||
                !OfficialWebsite.StartsWith("https://", StringComparison.Ordinal))
                throw new InvalidOperationException("网易官方网站入口必须使用指定的 HTTPS 域名。");
            if (LooksLikeNeteaseMinecraft("完全无关的软件") ||
                !LooksLikeNeteaseMinecraft("网易发烧游戏平台"))
                throw new InvalidOperationException("网易安装记录检测规则异常。");
            foreach (var value in new[] { "", "https://example.com/file.exe", @"\\host\share\client.exe", "client.jar" })
            {
                bool rejected = false;
                try { ValidateSelectedLauncher(value); }
                catch (Exception ex) when (
                    ex is ArgumentException || ex is NotSupportedException || ex is FileNotFoundException)
                { rejected = true; }
                if (!rejected)
                    throw new InvalidOperationException("不安全或无效的程序路径未被拒绝。");
            }
        }
    }
}
