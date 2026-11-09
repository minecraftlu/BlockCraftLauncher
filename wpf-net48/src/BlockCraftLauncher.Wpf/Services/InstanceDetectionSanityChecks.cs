using System;
using System.IO;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Verifies metadata lookup for vanilla, shared-root loader and
    /// isolated modpack instances using disposable local folders.
    /// Does not download games, start Java or read account information.
    /// </summary>
    internal static class InstanceDetectionSanityChecks
    {
        static void Require(bool actual, string purpose)
        {
            if (!actual)
                throw new InvalidOperationException("Instance detection regression: " + purpose);
        }

        static void PutMetadata(string root, string id)
        {
            var parent = Path.Combine(root, "versions", id);
            Directory.CreateDirectory(parent);
            File.WriteAllText(Path.Combine(parent, id + ".json"), "{\"id\":\"" + id + "\"}");
        }

        static void Reject(Action action, string purpose)
        {
            bool invalid = false;
            try { action(); }
            catch (InvalidDataException) { invalid = true; }
            Require(invalid, purpose);
        }

        internal static void Run()
        {
            string scratch = Path.Combine(Path.GetTempPath(),
                "BlockCraft-InstanceDetect-" + Guid.NewGuid().ToString("N"));
            try
            {
                string shared = Path.Combine(scratch, "shared");
                string isolated = Path.Combine(scratch, "isolated");
                Directory.CreateDirectory(shared);
                Directory.CreateDirectory(isolated);
                var config = new LauncherConfig { Root = shared };
                var profile = new GameProfile
                {
                    Name = "WPF offline test", BaseVersion = "1.21.1",
                    LaunchVersion = "fabric-loader-test-1.21.1",
                    Directory = isolated
                };

                // The regression: metadata is in the shared game directory,
                // but profile.Directory points to an unrelated instance folder.
                PutMetadata(shared, profile.LaunchVersion);
                var found = MinecraftInstanceLocator.Resolve(profile, config);
                Require(found.MetadataFound, "shared-root version should be detected");
                Require(!found.UsesInstanceDirectory, "shared-root fallback selected");
                Require(found.GameRoot == Path.GetFullPath(shared),
                    "detector must resolve same root as CmlLib launcher");
                Require(File.Exists(found.MetadataPath),
                    "reported metadata path must exist");

                // Isolated modpack should take precedence if it contains its
                // own manifest, libraries and versions (even if shared exists).
                PutMetadata(isolated, profile.LaunchVersion);
                found = MinecraftInstanceLocator.Resolve(profile, config);
                Require(found.MetadataFound && found.UsesInstanceDirectory,
                    "isolated metadata should take priority");
                Require(found.GameRoot == Path.GetFullPath(isolated),
                    "isolated CmlLib root selection");

                // Without either metadata file, report the exact two paths
                // rather than treating missing metadata as game launch failure.
                File.Delete(found.InstanceMetadataPath);
                File.Delete(found.SharedMetadataPath);
                found = MinecraftInstanceLocator.Resolve(profile, config);
                Require(!found.MetadataFound, "missing metadata must be reported");
                Require(found.InstanceMetadataPath != found.SharedMetadataPath,
                    "both candidate locations shown to user");
                Require(found.GameRoot == Path.GetFullPath(shared),
                    "legacy launcher fallback remains shared-root");

                // Plain vanilla instances may use BaseVersion directly.
                profile.Directory = shared;
                profile.LaunchVersion = "";
                PutMetadata(shared, profile.BaseVersion);
                found = MinecraftInstanceLocator.Resolve(profile, config);
                Require(found.MetadataFound && found.VersionId == "1.21.1",
                    "empty launch id uses base game version");

                // Importing a version from another .minecraft folder must
                // preserve that folder even when a shared default root exists.
                string external = Path.Combine(scratch, "other-minecraft");
                PutMetadata(external, "1.20.6");
                string selectedJson = Path.Combine(external, "versions",
                    "1.20.6", "1.20.6.json");
                var imported = MinecraftInstanceLocator.ImportVersionMetadata(selectedJson);
                Require(imported.LaunchVersion == "1.20.6" &&
                    imported.Directory == Path.GetFullPath(external),
                    "external import preserves version and its real root");
                var importedLocation = MinecraftInstanceLocator.Resolve(imported, config);
                Require(importedLocation.MetadataFound &&
                    importedLocation.UsesInstanceDirectory &&
                    importedLocation.GameRoot == Path.GetFullPath(external),
                    "external imported version is detectable by the same launcher root");

                // A loose JSON file must not be silently registered as a
                // launchable Minecraft version with an unrelated game root.
                string badName = Path.Combine(external, "versions",
                    "1.20.6", "not-the-version.json");
                File.WriteAllText(badName, "{}");
                Reject(() => MinecraftInstanceLocator.ImportVersionMetadata(badName),
                    "mismatching version metadata file name");
                string wrongFolder = Path.Combine(external, "other",
                    "1.19.4");
                Directory.CreateDirectory(wrongFolder);
                string wrongFolderJson = Path.Combine(wrongFolder, "1.19.4.json");
                File.WriteAllText(wrongFolderJson, "{}");
                Reject(() => MinecraftInstanceLocator.ImportVersionMetadata(wrongFolderJson),
                    "metadata must live under a versions directory");

                bool missingRejected = false;
                try
                {
                    MinecraftInstanceLocator.ImportVersionMetadata(
                        Path.Combine(external, "versions", "1.20.6", "missing.json"));
                }
                catch (FileNotFoundException) { missingRejected = true; }
                Require(missingRejected, "missing import file must be rejected");

                // Untrusted instance records must not traverse out of the
                // game versions directory while probing local metadata.
                profile.LaunchVersion = "../outside";
                Reject(() => MinecraftInstanceLocator.Resolve(profile, config),
                    "forward slash path traversal");
                profile.LaunchVersion = "..\\outside";
                Reject(() => MinecraftInstanceLocator.Resolve(profile, config),
                    "backslash path traversal");
                profile.LaunchVersion = "..";
                Reject(() => MinecraftInstanceLocator.Resolve(profile, config),
                    "dotdot version");
            }
            finally
            {
                try { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
