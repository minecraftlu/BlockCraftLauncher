using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class VersionedDesktopFileName
    {
        static readonly Regex ReleaseName = new Regex(
            @"\ABlockCraftLauncher-v[0-9]+\.[0-9]+\.[0-9]+-WPF-OneFile-Windows-x64\.exe\z",
            RegexOptions.CultureInvariant);

        internal static string FromVersion(Version version) =>
            "BlockCraftLauncher-v" + version.ToString(3) + "-WPF-OneFile-Windows-x64.exe";

        internal static string GetCandidate(string releaseAssetName, int number)
        {
            if (releaseAssetName == null || !ReleaseName.IsMatch(releaseAssetName))
                throw new ArgumentException("无效的启动器发布文件名。", nameof(releaseAssetName));
            if (number < 1) throw new ArgumentOutOfRangeException(nameof(number));
            return number == 1 ? releaseAssetName :
                Path.GetFileNameWithoutExtension(releaseAssetName) + " (" +
                number.ToString(CultureInfo.InvariantCulture) + ").exe";
        }
    }
}
