using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Offline fixtures: never reaches api.modrinth.com, fetches Minecraft
    /// or reads any login credential. Exercises graph traversal and the
    /// batch registry transaction on disposable temporary game folders.
    /// </summary>
    internal static class ModrinthDependencySanityChecks
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly string Hash = new string('a', 40);

        static void Require(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(
                "Modrinth dependency migration regression: " + label);
        }

        static Dictionary<string, object> Dependency(string project, string version = null,
            string type = "required")
        {
            return new Dictionary<string, object> {
                ["project_id"] = project, ["version_id"] = version,
                ["dependency_type"] = type
            };
        }

        static Dictionary<string, object> Version(string project, string id, string file,
            object[] dependencies = null, string minecraft = "1.21.1",
            string loader = "fabric", string url = null, string sha1 = null)
        {
            return new Dictionary<string, object> {
                ["project_id"] = project,
                ["id"] = id,
                ["name"] = project,
                ["game_versions"] = new[] { minecraft },
                ["loaders"] = new[] { loader },
                ["dependencies"] = dependencies ?? new object[0],
                ["files"] = new object[] {
                    new Dictionary<string, object> {
                        ["filename"] = file, ["primary"] = true,
                        ["url"] = url ?? ("https://cdn.modrinth.com/data/" + project +
                            "/versions/" + id + "/" + file),
                        ["hashes"] = new Dictionary<string, object> {
                            ["sha1"] = sha1 ?? Hash
                        }
                    }
                }
            };
        }

        static Func<string, CancellationToken, Task<string>> Fixture(
            params Dictionary<string, object>[] versions)
        {
            var endpoints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in versions)
            {
                string id = Convert.ToString(v["id"]);
                endpoints["https://api.modrinth.com/v2/version/" + id] = Json.Serialize(v);
            }
            foreach (var group in versions.GroupBy(v => Convert.ToString(v["project_id"])))
                endpoints["https://api.modrinth.com/v2/project/" + group.Key + "/version"] =
                    Json.Serialize(group.Cast<object>().ToArray());
            return (url, ct) => {
                ct.ThrowIfCancellationRequested();
                // The real API supports game_versions/loaders filtering. Keep
                // all supplied fixture rows so client validation stays tested.
                string path = new Uri(url).GetLeftPart(UriPartial.Path);
                if (!endpoints.TryGetValue(path, out var payload))
                    throw new InvalidOperationException("Offline fixture requested unknown endpoint: " + url);
                return Task.FromResult(payload);
            };
        }

        static async Task ThrowsAsync(Func<Task> action, string label)
        {
            bool rejected = false;
            try { await action(); }
            catch (InvalidDataException) { rejected = true; }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, label);
        }

        static async Task VerifyGraphsAsync()
        {
            // A→B→C→A is a cycle, and C is shared by A/B. Only one
            // copy of each mod is ever planned, dependency before parent.
            var a = Version("A", "vA", "A.jar",
                new object[] { Dependency("B", "vB"),
                    Dependency("OPTIONAL", null, "optional") });
            var b = Version("B", "vB", "B.jar",
                new object[] { Dependency(null, "vC") });
            var c = Version("C", "vC", "C.jar",
                new object[] { Dependency("A") });
            var plan = await new ModrinthDependencyPlanner(Fixture(a, b, c))
                .PlanAsync("A", "1.21.1", "fabric", CancellationToken.None);
            Require(plan.Count == 3, "cycle and shared dependency deduplication");
            Require(plan[0].ProjectId == "C" &&
                plan[1].ProjectId == "B" && plan[2].ProjectId == "A",
                "dependencies must precede selected mod");
            Require(plan.All(x => x.DownloadUrl.StartsWith("https://cdn.modrinth.com/",
                StringComparison.OrdinalIgnoreCase)), "trusted CDN fixture");
            Require(plan.All(x => x.Sha1.Length == 40), "every planned file needs SHA-1");

            var mixed = Version("MIXED", "vMixed", "mixed-sources.jar");
            mixed["files"] = new object[] {
                new Dictionary<string, object> {
                    ["filename"] = "mixed-sources.jar",
                    ["file_type"] = "sources-jar", ["primary"] = true,
                    ["url"] = "https://cdn.modrinth.com/data/MIXED/versions/vMixed/mixed-sources.jar",
                    ["hashes"] = new Dictionary<string, object> { ["sha1"] = Hash }
                },
                new Dictionary<string, object> {
                    ["filename"] = "mixed.jar", ["file_type"] = "unknown",
                    ["primary"] = false,
                    ["url"] = "https://cdn.modrinth.com/data/MIXED/versions/vMixed/mixed.jar",
                    ["hashes"] = new Dictionary<string, object> { ["sha1"] = Hash }
                }
            };
            var mixedPlan = await new ModrinthDependencyPlanner(Fixture(mixed))
                .PlanAsync("MIXED", "1.21.1", "fabric", CancellationToken.None);
            Require(mixedPlan.Count == 1 && mixedPlan[0].Filename == "mixed.jar",
                "primary source archive skipped in favor of runnable JAR");

            // A release may have source-only entries before a runnable version;
            // the installer must continue instead of treating it as missing.
            var old = Version("LONG", "vOld", "old-sources.jar");
            var good = Version("LONG", "vGood", "good.jar");
            var longPlan = await new ModrinthDependencyPlanner(Fixture(old, good))
                .PlanAsync("LONG", "1.21.1", "fabric", CancellationToken.None);
            Require(longPlan.Count == 1 && longPlan[0].VersionId == "vGood",
                "source-only compatible version skipped for installable release");
            var neo = Version("NEO", "vNeo", "neo.jar", loader: "neoforge");
            var neoPlan = await new ModrinthDependencyPlanner(Fixture(neo))
                .PlanAsync("NEO", "1.21.1", "NeoForge", CancellationToken.None);
            Require(neoPlan.Count == 1 && neoPlan[0].Filename == "neo.jar",
                "NeoForge loader casing normalized for Modrinth version filters");

            var mismatched = Version("B", "vB", "B.jar",
                minecraft: "1.20.1");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(a, mismatched, c)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "incompatible fixed dependency must fail");

            var invalidFilename = Version("A", "vA", "../escape.jar");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(invalidFilename)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "directory traversal must fail");
            var invalidUrl = Version("A", "vA", "A.jar",
                url: "https://evil.invalid/malware.jar");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(invalidUrl)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "untrusted CDN must fail");
            var invalidHash = Version("A", "vA", "A.jar", sha1: "BAD");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(invalidHash)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "invalid checksum must fail");
            var unsupportedLoader = Version("A", "vA", "A.jar", loader: "forge");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(unsupportedLoader)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "wrong loader must fail");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(a, b, c)).PlanAsync("A", "1.21.1", "vanilla",
                CancellationToken.None), "vanilla cannot install mods");

            var duplicate = Version("B", "vB", "A.jar");
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(a, duplicate, c)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "two projects cannot overwrite same filename");

            var missingRequired = Version("A", "vA", "A.jar",
                new object[] { Dependency(null, null) });
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(missingRequired)).PlanAsync("A", "1.21.1", "fabric",
                CancellationToken.None), "unresolved required dependency must fail");

            var conflict = Version("A", "vA", "A.jar",
                new object[] { Dependency("B", "vB"), Dependency("B", "vB2") });
            await ThrowsAsync(async () => await new ModrinthDependencyPlanner(
                Fixture(conflict, Version("B", "vB", "B.jar"), Version("B", "vB2", "B2.jar")))
                .PlanAsync("A", "1.21.1", "fabric", CancellationToken.None),
                "conflicting required fixed versions must fail");
        }

        static string FileSha1(string file)
        {
            using (var sha = SHA1.Create())
            using (var input = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "")
                    .ToLowerInvariant();
        }

        static void VerifyAtomicRegistry()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "BlockCraft-Deps-Selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                string mods = Path.Combine(root, "mods");
                Directory.CreateDirectory(mods);
                string a = Path.Combine(mods, "A.jar");
                string b = Path.Combine(mods, "B.jar");
                File.WriteAllText(a, "offline-mod-a", Encoding.UTF8);
                File.WriteAllText(b, "offline-mod-b", Encoding.UTF8);
                var config = new LauncherConfig();
                var profile = new GameProfile { Id = "offline-test", Name = "offline",
                    Directory = root, Loader = "fabric", BaseVersion = "1.21.1" };
                var manager = new ManagedModUpdateService(config);
                var records = new[] {
                    new ManagedResource { ProjectId = "A", VersionId = "vA",
                        Kind = "mod", Filename = "A.jar", Sha1 = FileSha1(a), Title = "A" },
                    new ManagedResource { ProjectId = "B", VersionId = "vB",
                        Kind = "mod", Filename = "B.jar", Sha1 = FileSha1(b), Title = "B" }
                };
                manager.RecordBatch(profile, records);
                Require(manager.Load(profile).Count == 2, "atomic dependency registration");

                File.AppendAllText(b, "tampered");
                bool rejected = false;
                try { manager.RecordBatch(profile, records); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "tampered dependency rejected");
                Require(manager.Load(profile).Count == 2,
                    "failed batch left the previous registry unchanged");
                Require(File.Exists(a) && File.Exists(b), "existing files never deleted");
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static void Run()
        {
            VerifyGraphsAsync().GetAwaiter().GetResult();
            VerifyAtomicRegistry();
        }
    }
}
