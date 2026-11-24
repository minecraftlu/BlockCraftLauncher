using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace BlockCraftLauncher.Updater
{
    internal static partial class Program
    {
        // Every desktop operation is limited to the exact paths chosen for this
        // update. Keep a read lease on a new desktop entry until the old copy
        // is retired; it cannot disappear or be changed between those steps.
        sealed class DesktopOneFile : IDisposable
        {
            readonly string source, oldDigest, newDigest;
            readonly FileIdentity oldIdentity;
            FileStream desktopLease;
            FileIdentity createdIdentity;
            long packageSize;
            bool created, keepDesktop;
            string destination;
            public string PreparedDesktopPath => SamePath(source, destination) ? null : destination;

            DesktopOneFile(string source, string oldDigest, string newDigest)
            {
                this.source = source;
                this.oldDigest = oldDigest;
                this.newDigest = newDigest;
                using (var old = File.OpenRead(source))
                {
                    oldIdentity = FileIdentity.Read(old.SafeFileHandle);
                    CheckDigest(old, oldDigest);
                }
            }

            public static DesktopOneFile Prepare(string source, string oldDigest, string staged,
                string newDigest, string updatesRoot)
            {
                string bootstrap = Environment.GetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP");
                if (string.IsNullOrWhiteSpace(bootstrap) || !SamePath(source, Path.GetFullPath(bootstrap)))
                    throw new InvalidDataException("无法确认此次启动的单文件 EXE 路径。");
                string ownedStage = OwnedStageDirectory(staged, updatesRoot);
                if (ownedStage == null || !SamePath(ownedStage, Path.GetDirectoryName(staged)) ||
                    AtOrBelow(source, ownedStage))
                    throw new InvalidDataException("更新包的位置与原启动器冲突或不属于此次更新。");
                CheckOneFileIdentity(source);
                CheckOneFileIdentity(staged);
                ExpectHash(staged, newDigest);
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop) ||
                    AtOrBelow(Path.GetFullPath(desktop), ownedStage))
                    throw new DirectoryNotFoundException("无法访问当前 Windows 用户的桌面。");
                EnsureNoReparseParents(desktop);
                var install = new DesktopOneFile(source, oldDigest, newDigest);
                try
                {
                    install.PrepareDestination(desktop, staged);
                    if (install.PreparedDesktopPath != null) SmokeTest(install.destination);
                    return install;
                }
                catch { install.Dispose(); throw; }
            }

            static void CheckOneFileIdentity(string path)
            {
                CheckLauncherExe(path);
                if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(AssemblyName.GetAssemblyName(path).Name, "BlockCraftLauncher",
                        StringComparison.Ordinal) ||
                    !string.Equals(FileVersionInfo.GetVersionInfo(path).ProductName, "BlockCraft Launcher",
                        StringComparison.Ordinal))
                    throw new InvalidDataException("此文件不是 BlockCraft 单文件启动器：" + path);
            }

            static bool SamePath(string a, string b) =>
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

            void PrepareDestination(string desktop, string staged)
            {
                packageSize = new FileInfo(staged).Length;
                string pending = PendingBeside(Path.Combine(desktop, "BlockCraftLauncher.exe"));
                try
                {
                    for (int number = 1; number <= 1000; number++)
                    {
                        destination = Path.Combine(desktop, number == 1 ? "BlockCraftLauncher.exe" :
                            "BlockCraftLauncher (" + number + ").exe");
                        if (SamePath(source, destination)) return;
                        if (EntryExists(destination))
                        {
                            // A previous interrupted attempt may have left this
                            // exact update ready. Never overwrite a different file.
                            if (TryReuseDestination()) return;
                            continue;
                        }
                        if (!File.Exists(pending))
                        {
                            File.Copy(staged, pending, false);
                            ExpectHash(pending, newDigest);
                        }
                        try { File.Move(pending, destination); }
                        catch (IOException error) when (WindowsError(error) == 80 || WindowsError(error) == 183)
                        {
                            // A concurrent installer or user claimed this name.
                            if (TryReuseDestination()) return;
                            continue;
                        }
                        created = true;
                        CheckLauncherExe(destination);
                        desktopLease = File.OpenRead(destination);
                        createdIdentity = FileIdentity.Read(desktopLease.SafeFileHandle);
                        CheckDigest(desktopLease, newDigest);
                        return;
                    }
                    throw new IOException("桌面上没有可用的 BlockCraft 启动器文件名。");
                }
                finally { TryDeleteOwnedFile(pending); }
            }

            static bool EntryExists(string path)
            {
                try { File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { return false; }
            }

            bool TryReuseDestination()
            {
                try
                {
                    var attributes = File.GetAttributes(destination);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        return false;
                    // Check size first: the occupant may be an unrelated large file.
                    if (new FileInfo(destination).Length != packageSize) return false;
                    desktopLease = File.OpenRead(destination);
                    CheckDigest(desktopLease, newDigest);
                    return true;
                }
                catch (Exception e) when (e is IOException || e is InvalidDataException ||
                    e is UnauthorizedAccessException)
                {
                    desktopLease?.Dispose();
                    desktopLease = null;
                    return false;
                }
            }

            public string Install(string staged, string updatesRoot, Process visible, out Exception cleanupError)
            {
                cleanupError = null;
                if (PreparedDesktopPath == null)
                {
                    using (var old = File.OpenRead(source))
                    {
                        oldIdentity.RequireMatch(old.SafeFileHandle);
                        CheckDigest(old, oldDigest);
                    }
                    ApplySingleExe(source, staged, newDigest, updatesRoot: updatesRoot, visible: visible,
                        requireConfirmation: true);
                    return source;
                }

                string runningDirectory = visible == null ? null : WindowDirectory(visible);
                if (visible == null)
                {
                    // A delayed acknowledgement may still belong to a running
                    // window. Leave both launchers available on a timeout.
                    keepDesktop = true;
                    if (!Restart(destination, out runningDirectory))
                        throw new IOException("未能确认桌面新版窗口已打开，原单文件启动器已保留。");
                }
                else if (visible.HasExited)
                    throw new IOException("新版窗口已退出，原单文件启动器已保留。");
                keepDesktop = true;
                try { RetireSource(); }
                catch (Exception e) when (e is IOException || e is InvalidDataException ||
                    e is Win32Exception || e is UnauthorizedAccessException)
                {
                    cleanupError = e;
                }
                BestEffortCleanup(() => CleanupOldOneFileCaches(staged, runningDirectory));
                BestEffortCleanup(() => CleanupOwnedStage(staged, updatesRoot));
                return destination;
            }

            void RetireSource()
            {
                CheckLauncherExe(source);
                RetryWhileLocked(() => DeleteVerifiedFile(source, oldDigest, oldIdentity));
            }

            // If the original window is already gone and recovery could not
            // be confirmed, keep the verified desktop copy available to open.
            public void PreserveVerifiedCandidate()
            {
                if (desktopLease != null) keepDesktop = true;
            }

            public void Dispose()
            {
                if (desktopLease != null) desktopLease.Dispose();
                if (created && !keepDesktop && createdIdentity != null)
                    BestEffortCleanup(() => DeleteVerifiedFile(destination, newDigest, createdIdentity));
            }

            static void CheckDigest(Stream file, string digest)
            {
                file.Position = 0;
                using (var sha = SHA256.Create())
                {
                    string hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                    if (!string.Equals(hash, digest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("文件内容已改变，取消更新或删除。");
                }
            }

            // Verify identity, contents and request deletion on ONE open file
            // handle. A replacement at the same name can never be unlinked by
            // a later path-based File.Delete call.
            static void DeleteVerifiedFile(string path, string digest, FileIdentity expected)
            {
                EnsureNoReparseParents(path);
                const uint readAndDelete = 0x80000000u | 0x00010000u;
                const uint openExisting = 3, openReparsePoint = 0x00200000;
                using (var handle = OpenForDeletion(path, readAndDelete, (uint)FileShare.Read,
                    IntPtr.Zero, openExisting, openReparsePoint, IntPtr.Zero))
                {
                    if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var current = FileIdentity.Read(handle);
                    if (expected != null && !expected.Matches(current))
                        throw new IOException("原文件已被替换，保留此位置的新文件。");
                    using (var file = new FileStream(handle, FileAccess.Read))
                    {
                        CheckDigest(file, digest);
                        byte remove = 1;
                        if (!SetFileInformationByHandle(handle, 4, ref remove, 1))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }
            }

            [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
                ExactSpelling = true, SetLastError = true)]
            static extern SafeFileHandle OpenForDeletion(string path, uint access, uint share,
                IntPtr security, uint creation, uint flags, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind,
                ref byte data, uint size);

            [StructLayout(LayoutKind.Sequential)]
            struct HandleInformation
            {
                public uint Attributes;
                public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
                public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInformation info);

            sealed class FileIdentity
            {
                readonly HandleInformation info;
                FileIdentity(HandleInformation info) { this.info = info; }
                internal static FileIdentity Read(SafeFileHandle file)
                {
                    if (!GetFileInformationByHandle(file, out var info))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (((FileAttributes)info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                        throw new IOException("拒绝修改目录或重解析点。");
                    return new FileIdentity(info);
                }
                internal bool Matches(FileIdentity other) => info.Volume == other.info.Volume &&
                    info.IndexHigh == other.info.IndexHigh && info.IndexLow == other.info.IndexLow;
                internal void RequireMatch(SafeFileHandle file)
                {
                    if (!Matches(Read(file))) throw new IOException("原文件已被替换。");
                }
            }

            internal static void RunSelfTest(string root)
            {
                string desktop = Directory.CreateDirectory(Path.Combine(root, "test-desktop")).FullName;
                string source = Path.Combine(root, "renamed-old.exe");
                string staged = Path.Combine(root, "test-incoming.exe");
                string occupied = Path.Combine(desktop, "BlockCraftLauncher.exe");
                File.WriteAllText(source, "old");
                File.WriteAllText(staged, "new version");
                File.WriteAllText(occupied, "personal executable");
                string digest = Hash(staged);
                using (var install = new DesktopOneFile(source, Hash(source), digest))
                {
                    install.PrepareDestination(desktop, staged);
                    if (SamePath(install.destination, occupied) || !File.Exists(source))
                        throw new Exception("Desktop collision or premature source cleanup");
                }
                if (!File.Exists(source) || Directory.GetFiles(desktop).Length != 1 ||
                    File.ReadAllText(occupied) != "personal executable")
                    throw new Exception("Unconfirmed update damaged a pre-existing file");
                using (var install = new DesktopOneFile(source, Hash(source), digest))
                {
                    install.PrepareDestination(desktop, staged);
                    string original = Path.Combine(root, "original-before-swap.exe");
                    File.Move(source, original);
                    File.WriteAllText(source, "old");
                    bool refused = false;
                    try { install.RetireSource(); } catch (IOException) { refused = true; }
                    if (!refused || !File.Exists(source))
                        throw new Exception("Cleanup accepted a different file with identical bytes");
                    File.Delete(source);
                    File.Move(original, source);
                    install.RetireSource();
                    install.keepDesktop = true;
                    if (File.Exists(source) || Hash(install.destination) != digest)
                        throw new Exception("Verified old launcher cleanup failed");
                }
            }
        }

        static void TestDesktopOneFile(string root) => DesktopOneFile.RunSelfTest(root);
    }
}
