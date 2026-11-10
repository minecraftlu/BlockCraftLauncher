using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Fully offline GitHub API and JAR fixtures. Never downloads binaries or
    /// uses personal tokens; installed release JARs must pass all checks.
    /// </summary>
    internal static class GitHubOpenSourceModSanityChecks
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static readonly string Digest = "sha256:" + new string('a', 64);

        static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(
                "GitHub 开源模组自检失败：" + message);
        }

        static Dictionary<string, object> Repo(string name,
            string license = "MIT", bool archived = false)
        {
            return new Dictionary<string, object> {
                ["name"] = name.Split('/').Last(),
                ["full_name"] = name,
                ["description"] = "Minecraft Java open source mod",
                ["archived"] = archived, ["fork"] = false, ["private"] = false,
                ["owner"] = new Dictionary<string, object> {
                    ["login"] = name.Split('/')[0],
                    ["avatar_url"] = "https://avatars.githubusercontent.com/u/123456?v=4"
                },
                ["license"] = license == null ? null :
                    new Dictionary<string, object> { ["spdx_id"] = license }
            };
        }

        static object Asset(string name, string url = null, string digest = null)
        {
            return new Dictionary<string, object> {
                ["name"] = name,
                ["browser_download_url"] = url ??
                    ("https://github.com/example/mod/releases/download/mc1.21.1/" + name),
                ["digest"] = digest ?? Digest,
                ["size"] = 1024
            };
        }

        static string Release(params object[] assets)
        {
            return Json.Serialize(new Dictionary<string, object> {
                ["tag_name"] = "mc1.21.1",
                ["name"] = "Release for Minecraft 1.21.1",
                ["draft"] = false,
                ["prerelease"] = false,
                ["assets"] = assets
            });
        }

        static void MustReject(string label, Func<GitHubOpenSourceModClient.ReleaseJar> choose)
        {
            bool rejected = false;
            try { choose(); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, label);
        }

        static void SearchParser()
        {
            string fixture = Json.Serialize(new Dictionary<string, object> {
                ["items"] = new object[] {
                    Repo("example/mod", "MIT"),
                    Repo("example/no-license", null),
                    Repo("example/private", "MIT", true),
                    Repo("example/strict", "GPL-3.0-only"),
                    Repo("bad-owner/../traversal", "MIT")
                },
                ["total_count"] = 5
            });
            var page = GitHubOpenSourceModClient.ParseSearch(fixture);
            Require(page.Hits.Count == 2 && page.TotalHits == 5,
                "只有明确 SPDX 开源许可的有效公开仓库被展示");
            Require(page.Hits[0].Source == "GitHub" &&
                page.Hits[0].SourceLabel == "GitHub 开源" &&
                page.Hits[0].CategoryLabel.Contains("MIT") &&
                page.Hits[0].IconUrl == "https://avatars.githubusercontent.com/u/123456?v=4",
                "GitHub 的来源、许可及作者头像需清晰标注");
            Require(GitHubOpenSourceModClient.RetryableDownloadStatus(408) &&
                GitHubOpenSourceModClient.RetryableDownloadStatus(429) &&
                GitHubOpenSourceModClient.RetryableDownloadStatus(502) &&
                !GitHubOpenSourceModClient.RetryableDownloadStatus(400) &&
                !GitHubOpenSourceModClient.RetryableDownloadStatus(401) &&
                !GitHubOpenSourceModClient.RetryableDownloadStatus(403) &&
                !GitHubOpenSourceModClient.RetryableDownloadStatus(404),
                "临时网络故障可重试，永久拒绝下载不得假装能成功");
            Require(GitHubOpenSourceModClient.TrustedAuthorAvatar(
                    "https://avatars.githubusercontent.com/u/123?v=4") != null &&
                GitHubOpenSourceModClient.TrustedAuthorAvatar(
                    "https://avatars.githubusercontent.com.evil.invalid/u/123") == null &&
                GitHubOpenSourceModClient.TrustedAuthorAvatar(
                    "http://avatars.githubusercontent.com/u/123") == null &&
                GitHubOpenSourceModClient.TrustedAuthorAvatar(
                    "https://avatars.githubusercontent.com/other/malicious.png") == null,
                "只允许 Github 官方头像主机和严格的 ID 路径");
        }

        static void ReleaseAssets()
        {
            var selected = GitHubOpenSourceModClient.ChooseReleaseJar(
                "example/mod", "1.21.1", "fabric",
                Release(
                    Asset("example-1.21.1-sources.jar"),
                    Asset("example-1.21.1-forge.jar"),
                    Asset("example-1.21.1-fabric.jar")));
            Require(selected.Filename == "example-1.21.1-fabric.jar" &&
                selected.Sha256 == new string('a', 64),
                "只能选择兼容加载器且具有 GitHub SHA-256 的 JAR");
            // The project's newest release may target 1.21.10, while the
            // player still needs a verified 1.21.1 binary from older Releases.
            string newer = Release(Asset("example-1.21.10-fabric.jar"));
            string older = Release(Asset("example-1.21.1-fabric.jar"));
            var releases = Json.Serialize(new object[] {
                Json.DeserializeObject(newer),
                Json.DeserializeObject(older)
            });
            var fromHistory = GitHubOpenSourceModClient.ChooseFromReleaseList(
                "example/mod", "1.21.1", "fabric", releases);
            Require(fromHistory != null &&
                fromHistory.Filename == "example-1.21.1-fabric.jar",
                "历史公开 Release 的兼容 JAR 可代替最新不兼容文件");
            var unavailable = GitHubOpenSourceModClient.ChooseFromReleaseList(
                "example/mod", "1.20.1", "fabric", releases);
            Require(unavailable == null,
                "无兼容历史版本时不猜测不安全下载文件");

            Require(GitHubOpenSourceModClient.CompatibleVersion(
                "example-mc1.21.1-fabric.jar", "1.21.1") &&
                !GitHubOpenSourceModClient.CompatibleVersion(
                    "example-mc1.21.10-fabric.jar", "1.21.1"),
                "不会误将 Minecraft 1.21.10 当作 1.21.1");

            MustReject("不能安装缺少 GitHub SHA256 的文件", () =>
                GitHubOpenSourceModClient.ChooseReleaseJar("example/mod",
                    "1.21.1", "fabric",
                    Release(Asset("example-1.21.1-fabric.jar",
                        digest: "sha256:invalid"))));
            MustReject("不能安装伪造的 GitHub 下载主机", () =>
                GitHubOpenSourceModClient.ChooseReleaseJar("example/mod",
                    "1.21.1", "fabric", Release(
                        Asset("example-1.21.1-fabric.jar",
                            url: "https://github.com.evil.invalid/other.jar"))));
            MustReject("不能猜测多个候选 JAR", () =>
                GitHubOpenSourceModClient.ChooseReleaseJar("example/mod",
                    "1.21.1", "fabric", Release(
                        Asset("example-1.21.1-fabric.jar"),
                        Asset("example-additional-1.21.1-fabric.jar"))));
            MustReject("不能从源码归档猜测可安装文件", () =>
                GitHubOpenSourceModClient.ChooseReleaseJar("example/mod",
                    "1.21.1", "fabric",
                    Release(Asset("example-1.21.1-sources.jar"))));
            MustReject("不能使用伪造的仓库路径", () =>
                GitHubOpenSourceModClient.ChooseReleaseJar("../mod",
                    "1.21.1", "fabric", Release(
                        Asset("example-1.21.1-fabric.jar"))));
        }

        static void JarSafety()
        {
            string temp = Path.Combine(Path.GetTempPath(),
                "blockcraft-openmods-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string fabric = Path.Combine(temp, "fabric.jar");
                using (var zip = ZipFile.Open(fabric, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("fabric.mod.json");
                    using (var writer = new StreamWriter(entry.Open()))
                        writer.Write("{\"id\":\"testmod\"}");
                }
                GitHubOpenSourceModClient.ValidateJar(fabric, "fabric");
                bool rejected = false;
                try { GitHubOpenSourceModClient.ValidateJar(fabric, "forge"); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Fabric JAR 不可被误装到 Forge 实例");

                string fake = Path.Combine(temp, "fake.jar");
                File.WriteAllText(fake, "source text is not a mod");
                rejected = false;
                try { GitHubOpenSourceModClient.ValidateJar(fake, "fabric"); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "GitHub 错误文件不得通过 JAR 验证");
            }
            finally
            {
                try { Directory.Delete(temp, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static void Run()
        {
            SearchParser();
            ReleaseAssets();
            JarSafety();
        }
    }
}
