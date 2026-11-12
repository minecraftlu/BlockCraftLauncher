using System;
using System.Diagnostics;
using System.Windows;
using MessageBox = BlockCraftLauncher.Wpf.LauncherDialog;
using Microsoft.Web.WebView2.Core;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// The CmlLib interactive Microsoft OAuth window uses the Evergreen WebView2
    /// Runtime. Microsoft Edge the browser and the WebView2 managed DLL are not
    /// substitutes for the installed runtime.
    /// </summary>
    internal static class WebView2RuntimeSupport
    {
        internal const string OfficialDownloadPage =
            "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";

        internal static bool IsAvailable(out string version)
        {
            try
            {
                // Uses Microsoft's loader, not an unreliable browser-exists check.
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                return !string.IsNullOrWhiteSpace(version);
            }
            catch (Exception)
            {
                // A missing or incompatible native loader should not crash WPF.
                version = null;
                return false;
            }
        }

        internal static bool IsRuntimeFailure(Exception error)
        {
            for (Exception ex = error; ex != null; ex = ex.InnerException)
            {
                if (ex is WebView2RuntimeNotFoundException ||
                    ex is DllNotFoundException ||
                    ex is BadImageFormatException)
                    return true;

                string message = ex.Message ?? string.Empty;
                if (message.IndexOf("WebView2 Runtime", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("compatible Webview2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("WebView2Loader", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        internal static void OfferOfficialSetup(Window owner)
        {
            var answer = MessageBox.Show(
                owner,
                "没有找到可用的 Microsoft Edge WebView2 Runtime。\n\n" +
                "BlockCraft Launcher 的微软正版登录需要该运行环境，" +
                "但原版游戏启动和离线模式不依赖它。\n\n" +
                "点击“是”打开微软官方 WebView2 下载页，安装 Evergreen Bootstrapper；" +
                "如果联网安装失败，可选择 x64 Standalone Installer。\n\n" +
                "安装完成后，请完全退出本启动器并重新打开，然后再点“微软正版”。",
                "微软正版登录环境缺失",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
                return;

            try
            {
                Process.Start(new ProcessStartInfo(OfficialDownloadPage)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception)
            {
                MessageBox.Show(owner, "无法自动打开浏览器，请手动访问：\n" +
                    OfficialDownloadPage, "微软官方 WebView2 下载",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }
}
