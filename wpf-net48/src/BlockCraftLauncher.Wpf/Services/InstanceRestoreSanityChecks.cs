using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Disposable local fixtures only. Exercises ZIP validation, preservation
    /// of existing world directories and restore transaction rollback.
    /// </summary>
    internal static class InstanceRestoreSanityChecks
    {
        static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(
                "Minecraft save restore regression: " + message);
        }

        static void Zip(string path, params string[] files)
        {
            using (var output = File.Create(path))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (string filename in files)
                {
                    var entry = zip.CreateEntry(filename);
                    using (var stream = entry.Open())
                    {
                        var bytes = Encoding.UTF8.GetBytes("world-fixture-" + filename);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }
        }

        static void ExpectInvalid(Action act, string message)
        {
            bool rejected = false;
            try { act(); }
            catch (InvalidDataException) { rejected = true; }
            catch (IOException) { rejected = true; }
            Require(rejected, message);
        }

        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "BlockCraft-Saves-Selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string profileDirectory = Path.Combine(root, "game");
                Directory.CreateDirectory(profileDirectory);
                var profile = new GameProfile { Directory = profileDirectory,
                    Name = "offline test", Id = "saves-fixture", Loader = "vanilla" };
                string backup = Path.Combine(root, "worlds.zip");
                Zip(backup, "WorldOne/level.dat", "WorldOne/region/r.0.0.mca",
                    "WorldTwo/level.dat");
                int count = LocalInstanceTools.RestoreSaves(profile, backup);
                Require(count == 2, "valid archive restores two worlds");
                string saves = Path.Combine(profileDirectory, "saves");
                string original = File.ReadAllText(Path.Combine(saves, "WorldOne", "level.dat"));
                Require(original.Contains("world-fixture"), "world data was extracted");
                ExpectInvalid(() => LocalInstanceTools.RestoreSaves(profile, backup),
                    "second restore must not overwrite existing worlds");
                Require(File.ReadAllText(Path.Combine(saves, "WorldOne", "level.dat")) == original,
                    "existing world must be byte-for-byte unchanged");

                string copyBackup = Path.Combine(root, "created-by-launcher.zip");
                LocalInstanceTools.BackupSaves(profile, copyBackup);
                var second = new GameProfile { Directory = Path.Combine(root, "game-2") };
                Directory.CreateDirectory(second.Directory);
                Require(LocalInstanceTools.RestoreSaves(second, copyBackup) == 2,
                    "backup created by launcher must be restorable");

                string traversal = Path.Combine(root, "escape.zip");
                Zip(traversal, "../escape/world.dat");
                ExpectInvalid(() => LocalInstanceTools.RestoreSaves(profile, traversal),
                    "ZIP traversal must be rejected");
                Require(!File.Exists(Path.Combine(root, "escape", "world.dat")),
                    "malicious ZIP cannot write outside save staging");
                string rootFile = Path.Combine(root, "root-file.zip");
                Zip(rootFile, "level.dat");
                ExpectInvalid(() => LocalInstanceTools.RestoreSaves(profile, rootFile),
                    "files at ZIP root are not Minecraft world folders");
                string separators = Path.Combine(root, "backslash.zip");
                Zip(separators, "WorldBack\\..\\evil.dat");
                ExpectInvalid(() => LocalInstanceTools.RestoreSaves(profile, separators),
                    "backslashes must not create hidden traversal");
                string duplicated = Path.Combine(root, "duplicate.zip");
                Zip(duplicated, "WorldOne/level.dat", "WorldOne/level.dat");
                ExpectInvalid(() => LocalInstanceTools.RestoreSaves(second, duplicated),
                    "duplicate ZIP entries must be rejected");
                string symlink = Path.Combine(root, "symlink.zip");
                using (var file = File.Create(symlink))
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry("LinkedWorld/level.dat");
                    entry.ExternalAttributes = unchecked((int)0xA0000000);
                    using (var stream = entry.Open())
                    {
                        byte[] data = Encoding.UTF8.GetBytes("target");
                        stream.Write(data, 0, data.Length);
                    }
                }
                ExpectInvalid(() => LocalInstanceTools.RestoreSaves(second, symlink),
                    "symbolic links may never be restored");
                Require(!Directory.EnumerateDirectories(saves,
                    ".blockcraft-saves-restore-*", SearchOption.AllDirectories).Any(),
                    "no restore staging directory left in saves");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
