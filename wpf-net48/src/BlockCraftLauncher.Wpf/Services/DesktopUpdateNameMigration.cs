using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace BlockCraftLauncher.Wpf.Services
{
    // v0.9.53's updater reports a generic desktop name even when installing a
    // newer payload. Only migrate a successful handoff for that specific EXE.
    internal static class DesktopUpdateNameMigration
    {
        const uint GenericRead = 0x80000000, DeleteAccess = 0x10000;
        const uint OpenExisting = 3, OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;

        internal static string CaptureBootstrapHash()
        {
            try
            {
                var path = Environment.GetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP");
                if (!LauncherAutoUpdateService.IsOneFileEnvironment(AppDomain.CurrentDomain.BaseDirectory, path)) return null;
                using (var file = File.OpenRead(path)) return Digest(file);
            }
            catch (Exception error) when (FileError(error)) { return null; }
        }

        internal static Task<string> RenameAfterHandoffAsync(string installed, string bootstrapHash, string tag) =>
            Task.Run(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(installed) || bootstrapHash == null) return installed;
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var full = Path.GetFullPath(installed);
                    if (string.IsNullOrEmpty(desktop) || !string.Equals(Path.GetDirectoryName(full), desktop,
                        StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(full),
                        @"\ABlockCraftLauncher(?: \((?:[2-9]|[1-9][0-9]{1,2}|1000)\))?\.exe\z",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return installed;

                    var newName = LauncherAutoUpdateService.ExpectedAsset(tag, "onefile");
                    // Avoid following a redirected ancestor during the migration.
                    for (var dir = new DirectoryInfo(desktop); dir != null; dir = dir.Parent)
                        if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return installed;
                    using (var parent = Open(desktop, 0, FileShare.ReadWrite, OpenReparsePoint | BackupSemantics))
                    {
                        if (parent.IsInvalid || !HasAttributes(parent, true)) return installed;
                        // The legacy helper writes success just before it releases its read lease.
                        // Never block the WPF dispatcher and never wait indefinitely for other apps.
                        for (int attempt = 0; attempt < 30; attempt++)
                        {
                            using (var source = Open(full, GenericRead | DeleteAccess, FileShare.Read, OpenReparsePoint))
                            {
                                if (!source.IsInvalid)
                                {
                                    if (!HasAttributes(source, false)) return installed;
                                    using (var file = new FileStream(source, FileAccess.Read))
                                    {
                                        if (Digest(file) != bootstrapHash) return installed;
                                        return MoveVerified(file, full, desktop, newName, bootstrapHash);
                                    }
                                }
                                int error = Marshal.GetLastWin32Error();
                                if (error != 32 && error != 33) return installed;
                            }
                            Thread.Sleep(100);
                        }
                    }
                }
                catch (Exception error) when (FileError(error)) { }
                return installed;
            });

        static string MoveVerified(FileStream source, string original, string desktop, string name, string digest)
        {
            for (int number = 1; number <= 1000; number++)
            {
                string candidate = Path.Combine(desktop, VersionedDesktopFileName.GetCandidate(name, number));
                int error = RenameHandle(source.SafeFileHandle, candidate);
                if (error == 0) return candidate;
                if (error != 80 && error != 183 && !File.Exists(candidate) && !Directory.Exists(candidate)) return original;
                // An already-installed identical EXE is also a valid destination.
                // Pin it against deletion/replacement until the generic name is removed.
                using (var other = Open(candidate, GenericRead, FileShare.Read, OpenReparsePoint))
                {
                    if (other.IsInvalid || !HasAttributes(other, false)) continue;
                    using (var existing = new FileStream(other, FileAccess.Read))
                    {
                        if (existing.Length != source.Length || Digest(existing) != digest) continue;
                        return SetFileInformationByHandle(source.SafeFileHandle, 4, new byte[] { 1 }, 1) ? candidate : original;
                    }
                }
            }
            return original;
        }

        static int RenameHandle(SafeFileHandle handle, string path)
        {
            // FILE_RENAME_INFO: zero ReplaceIfExists and RootDirectory, followed by
            // a DWORD byte length and the UTF-16 absolute destination (no overwrite).
            var bytes = Encoding.Unicode.GetBytes(path);
            int lengthOffset = IntPtr.Size * 2;
            int nameOffset = lengthOffset + sizeof(int);
            var info = new byte[nameOffset + bytes.Length + sizeof(char)];
            Buffer.BlockCopy(BitConverter.GetBytes(bytes.Length), 0, info, lengthOffset, sizeof(int));
            Buffer.BlockCopy(bytes, 0, info, nameOffset, bytes.Length);
            return SetFileInformationByHandle(handle, 3, info, (uint)info.Length) ? 0 : Marshal.GetLastWin32Error();
        }

        static string Digest(Stream file)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(file));
        }

        static bool FileError(Exception error) => error is IOException || error is UnauthorizedAccessException ||
            error is ArgumentException || error is SecurityException || error is NotSupportedException ||
            error is CryptographicException || error is Win32Exception;

        static SafeFileHandle Open(string path, uint access, FileShare share, uint flags) =>
            CreateFileW(path, access, (uint)share, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);

        static bool HasAttributes(SafeFileHandle handle, bool directory) =>
            GetFileInformationByHandleEx(handle, 9 /* FileAttributeTagInfo */, out var info, 8) &&
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
                (directory ? FileAttributes.Directory : 0);

        [StructLayout(LayoutKind.Sequential)]
        struct AttributeTagInfo { public FileAttributes Attributes; public uint Tag; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] data, uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTagInfo data, uint size);
    }
}
