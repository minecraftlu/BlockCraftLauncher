using System;
using System.IO;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Validates a user-selected location for temporary launcher downloads.
    /// Only named launcher-owned subfolders are ever cleaned. The selected
    /// root itself, game instances and user-managed files are never deleted.
    /// </summary>
    public static class DownloadPathService
    {
        public static string DefaultDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlockCraftLauncher");

        public static string Resolve(string selected)
        {
            return Path.GetFullPath(string.IsNullOrWhiteSpace(selected)
                ? DefaultDirectory : selected.Trim().Trim('"'));
        }

        public static bool TryValidate(string selected, string minecraftRoot,
            out string directory, out string error)
        {
            directory = null;
            error = null;
            string full;
            try
            {
                string raw = string.IsNullOrWhiteSpace(selected)
                    ? DefaultDirectory : selected.Trim().Trim('"');
                if (!Path.IsPathRooted(raw) ||
                    raw.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new ArgumentException("请选择本机磁盘上的绝对路径，例如 D:\\BlockCraftDownloads。");
                full = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string volume = Path.GetPathRoot(full);
                string windows = Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows);
                string profile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                if (string.Equals(full, volume.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase) ||
                    ContainsOrEquals(windows, full) ||
                    string.Equals(full, profile, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("不能直接使用磁盘根目录、Windows 系统目录或用户主目录。");
                if (!string.IsNullOrWhiteSpace(minecraftRoot))
                {
                    string game = Path.GetFullPath(minecraftRoot);
                    if (ContainsOrEquals(game, full) || ContainsOrEquals(full, game))
                        throw new ArgumentException(
                            "下载缓存不能设在 Minecraft 游戏根目录或其父目录，以免误清理游戏文件。");
                }
                // Do not traverse junctions/symlinks for updater downloads.
                for (string check = full;
                    !string.IsNullOrWhiteSpace(check); check = Path.GetDirectoryName(check))
                {
                    if (Directory.Exists(check) &&
                        (File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("下载目录及其上级目录不能是符号链接或联接点。");
                }
                directory = full;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException ||
                ex is NotSupportedException || ex is PathTooLongException ||
                ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool ContainsOrEquals(string parent, string descendant)
        {
            if (string.IsNullOrWhiteSpace(parent) ||
                string.IsNullOrWhiteSpace(descendant)) return false;
            string root = Path.GetFullPath(parent).TrimEnd('\\', '/');
            string path = Path.GetFullPath(descendant).TrimEnd('\\', '/');
            return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static string UpdatesRoot(string selected) =>
            Path.Combine(Resolve(selected), "updates");

        public static string ModpackCache(string selected) =>
            Path.Combine(Resolve(selected), "wpf-pack-cache");
    }
}
