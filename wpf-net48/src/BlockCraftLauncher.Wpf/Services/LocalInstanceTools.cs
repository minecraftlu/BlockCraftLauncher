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
        /// <summary>
        /// Restore a backup created from a Minecraft saves directory into
        /// NEW world folders only. The archive is fully validated/extracted
        /// in a staging folder before any world is published. Never replace
        /// an existing save, follow zip symlinks or trust archive paths.
        /// </summary>
        public static int RestoreSaves(GameProfile profile, string archivePath)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Directory))
                throw new ArgumentException("请选择有效的游戏实例");
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                throw new FileNotFoundException("找不到要恢复的存档 ZIP", archivePath);
            var archiveInfo = new FileInfo(archivePath);
            if (archiveInfo.Length == 0 || archiveInfo.Length > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("存档备份文件为空或超过 4 GiB 的恢复限制");
            string root = Path.GetFullPath(profile.Directory);
            string saves = Path.Combine(root, "saves");
            string stage = Path.Combine(root, ".blockcraft-saves-restore-" +
                Guid.NewGuid().ToString("N"));
            var committed = new System.Collections.Generic.List<string>();
            try
            {
                Directory.CreateDirectory(stage);
                var names = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                var worlds = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                long total = 0;
                int count = 0;
                using (var input = File.OpenRead(archivePath))
                using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count == 0 || zip.Entries.Count > 20000)
                        throw new InvalidDataException("存档 ZIP 文件数不在安全范围内");
                    foreach (var entry in zip.Entries)
                    {
                        count++;
                        string name = entry.FullName;
                        if (string.IsNullOrEmpty(name) || name.Length > 512 ||
                            name.StartsWith("/", StringComparison.Ordinal) ||
                            name.Contains("\\") || name.Contains(":") ||
                            name.IndexOf('\0') >= 0)
                            throw new InvalidDataException("存档包含不安全的文件名");
                        bool isDirectory = name.EndsWith("/", StringComparison.Ordinal);
                        string trimmed = isDirectory ? name.TrimEnd('/') : name;
                        var segments = trimmed.Split('/');
                        if (segments.Length < 2 && !isDirectory)
                            throw new InvalidDataException("存档文件必须位于世界文件夹内");
                        if (segments.Length > 32 || segments.Any(segment =>
                            string.IsNullOrWhiteSpace(segment) || segment == "." ||
                            segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) ||
                            segment.EndsWith(" ", StringComparison.Ordinal) ||
                            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                            segment.IndexOfAny(new[] { '<', '>', '|', '?', '*', '"' }) >= 0))
                            throw new InvalidDataException("存档压缩包包含不安全的路径段");
                        // Unix symlink entries use the mode's high 16 bits.
                        int mode = (entry.ExternalAttributes >> 16) & 0xF000;
                        if (mode == 0xA000)
                            throw new InvalidDataException("不允许 ZIP 中的符号链接");
                        if (!names.Add(trimmed))
                            throw new InvalidDataException("存档 ZIP 包含重复文件或目录");
                        worlds.Add(segments[0]);
                        if (worlds.Count > 50)
                            throw new InvalidDataException("一次恢复的世界数量超过 50 个");
                        if (entry.Length > 2L * 1024 * 1024 * 1024)
                            throw new InvalidDataException("单个存档文件超过 2 GiB");
                        total += entry.Length;
                        if (total > 8L * 1024 * 1024 * 1024)
                            throw new InvalidDataException("存档恢复展开数据超过 8 GiB");
                        string destination = Path.GetFullPath(
                            Path.Combine(stage, trimmed.Replace('/', Path.DirectorySeparatorChar)));
                        if (!destination.StartsWith(stage + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("存档 ZIP 路径逃逸");
                        if (isDirectory)
                        {
                            Directory.CreateDirectory(destination);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(destination));
                            using (var source = entry.Open())
                            using (var output = new FileStream(destination, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None))
                                source.CopyTo(output);
                        }
                    }
                }
                if (worlds.Count == 0)
                    throw new InvalidDataException("备份中没有世界文件夹");
                foreach (string world in worlds)
                {
                    if (!Directory.Exists(Path.Combine(stage, world)))
                        throw new InvalidDataException("存档世界目录不存在：" + world);
                }
                Directory.CreateDirectory(saves);
                var existing = new System.Collections.Generic.HashSet<string>(
                    Directory.EnumerateFileSystemEntries(saves)
                        .Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
                foreach (string world in worlds)
                    if (existing.Contains(world))
                        throw new IOException("检测到已有同名世界，拒绝覆盖：" + world);

                foreach (string world in worlds.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                {
                    string to = Path.Combine(saves, world);
                    if (Directory.Exists(to) || File.Exists(to))
                        throw new IOException("恢复时目标世界已存在：" + world);
                    Directory.Move(Path.Combine(stage, world), to);
                    committed.Add(to);
                }
                return worlds.Count;
            }
            catch
            {
                // Never touch any existing world; rollback only worlds moved
                // into saves by this invocation.
                foreach (string folder in committed)
                {
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                throw;
            }
            finally
            {
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
