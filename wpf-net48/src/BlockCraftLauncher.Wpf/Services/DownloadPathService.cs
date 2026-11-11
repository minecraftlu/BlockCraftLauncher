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
                if (!IsLocalAbsolutePath(raw))
                    throw new ArgumentException("请选择本机磁盘上的完整绝对路径，例如 E:\\BlockCraftLauncher\\Downloads。");
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
                            "同一块移动硬盘可以使用两个独立的文件夹，例如 E:\\BlockCraftLauncher\\Minecraft 和 E:\\BlockCraftLauncher\\Downloads；" +
                            "不能设成同一个目录，也不能互相包含，以免影响游戏和存档。");
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

        /// <summary>
        /// Prepare two sibling folders on the SAME local drive. A USB HDD
        /// often reports DriveType.Fixed, so deliberately do not require the
        /// OS to identify a drive as removable. Nothing is created or moved
        /// until the user explicitly saves their settings.
        /// </summary>
        public static bool TryPlanSameDiskFolders(string selectedParent,
            out string minecraftDirectory, out string downloadDirectory,
            out string error)
        {
            minecraftDirectory = null;
            downloadDirectory = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(selectedParent))
                    throw new ArgumentException("请选择移动硬盘盘符或其下的父文件夹。");
                string raw = selectedParent.Trim().Trim('"');
                if (!IsLocalAbsolutePath(raw))
                    throw new ArgumentException("请选择本机磁盘的绝对路径，例如 E:\\。");
                string parent = Path.GetFullPath(raw);
                string driveRoot = Path.GetPathRoot(parent);
                if (string.Equals(parent.TrimEnd('\\', '/'),
                    driveRoot.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase))
                    parent = Path.Combine(driveRoot, "BlockCraftLauncher");
                string game = Path.Combine(parent, "Minecraft");
                string cache = Path.Combine(parent, "Downloads");
                if (!TryValidate(cache, game, out var validCache,
                    out var reason))
                    throw new ArgumentException(reason ??
                        "移动硬盘上的目录不符合安全要求。");
                minecraftDirectory = Path.GetFullPath(game);
                downloadDirectory = validCache;
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

        // Path.IsPathRooted("E:folder") also returns true on Windows even
        // though it is relative to the current directory on drive E.
        // USB/movable settings must never silently resolve such a path.
        static bool IsLocalAbsolutePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                path.Length >= 3 &&
                char.IsLetter(path[0]) && path[1] == ':' &&
                (path[2] == '\\' || path[2] == '/') &&
                Path.IsPathRooted(path);
        }

        public static string UpdatesRoot(string selected) =>
            Path.Combine(Resolve(selected), "updates");

        public static string ModpackCache(string selected) =>
            Path.Combine(Resolve(selected), "wpf-pack-cache");
    }
}
