using System;
using System.IO;
using System.Threading;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class DesktopUpdateDownload
    {
        internal static string Publish(string desktop, string staged, string assetName,
            string sha256, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
                throw new DirectoryNotFoundException("无法访问当前用户的 Windows 桌面。");
            var directory = new DirectoryInfo(desktop);
            for (var parent = directory; parent != null; parent = parent.Parent)
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("桌面路径包含无法验证的文件系统链接。");

            string temporary = null;
            try
            {
                using (var input = File.OpenRead(staged))
                {
                    for (int number = 1; number <= 1000; number++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string destination = Path.Combine(directory.FullName,
                            VersionedDesktopFileName.GetCandidate(assetName, number));
                        if (!Available(destination, input.Length, sha256, out bool matches))
                        {
                            if (matches) return destination;
                            continue;
                        }
                        if (temporary == null)
                        {
                            string path = Path.Combine(directory.FullName,
                                ".blockcraft-download-" + Guid.NewGuid().ToString("N") + ".tmp");
                            using (var output = new FileStream(path, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None))
                            {
                                temporary = path;
                                input.CopyTo(output);
                                output.Flush(true);
                            }
                            LauncherAutoUpdateService.VerifyFile(temporary, sha256);
                        }
                        cancellation.ThrowIfCancellationRequested();
                        try { File.Move(temporary, destination); }
                        catch (IOException e) when ((e.HResult & 0xffff) == 80 ||
                            (e.HResult & 0xffff) == 183)
                        {
                            // A concurrent download may have published these same bytes.
                            if (!Available(destination, input.Length, sha256, out matches) && matches)
                                return destination;
                            continue;
                        }
                        temporary = null;
                        return destination;
                    }
                }
                throw new IOException("桌面上没有可用的新版启动器文件名。");
            }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        // Occupied names remain untouched. Reuse only verified, regular files.
        static bool Available(string path, long length, string sha256, out bool matches)
        {
            matches = false;
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 &&
                    new FileInfo(path).Length == length)
                {
                    LauncherAutoUpdateService.VerifyFile(path, sha256);
                    matches = true;
                }
            }
            catch (FileNotFoundException) { return true; }
            catch (InvalidDataException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }
    }
}
