using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public static class LocalInstanceTools
    {
        public static int CountFiles(GameProfile profile, string folder, string extension)
        {
            if (profile == null) return 0;
            var path = Path.Combine(profile.Directory, folder);
            return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                .Count(f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) : 0;
        }
        public static void OpenFolder(GameProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            Directory.CreateDirectory(profile.Directory);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + profile.Directory.Replace("\"", "") + "\"");
        }
        public static void ToggleMod(string fullPath, bool enable)
        {
            if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到模组文件", fullPath);
            if (enable && fullPath.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) File.Move(fullPath, fullPath.Substring(0, fullPath.Length - ".disabled".Length));
            else if (!enable && fullPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) File.Move(fullPath, fullPath + ".disabled");
        }
        public static string BackupSaves(GameProfile profile, string outputZip)
        {
            var saves = Path.Combine(profile.Directory, "saves");
            if (!Directory.Exists(saves)) throw new IOException("没有存档目录");
            if (File.Exists(outputZip)) throw new IOException("目标备份文件已经存在");
            ZipFile.CreateFromDirectory(saves, outputZip, CompressionLevel.Optimal, false);
            return outputZip;
        }
    }
}
