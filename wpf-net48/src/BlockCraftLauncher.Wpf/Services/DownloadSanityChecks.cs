using System;
using System.Linq;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Offline, deterministic downloader regression checks. Called by the
    /// --download-selftest command on GitHub Actions Windows runner.
    /// Never opens an HTTP connection or modifies player game files.
    /// </summary>
    internal static class DownloadSanityChecks
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("下载组件自检失败：" + message);
        }

        public static void Run()
        {
            string official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            string mirror = NetworkDownloader.MirrorMojangUri(official);
            Require(!string.IsNullOrWhiteSpace(mirror) && mirror.StartsWith("https://bmclapi2.bangbang93.com/"),
                "受支持的 Mojang 元数据镜像映射");
            Require(NetworkDownloader.MirrorMojangUri("https://evil.example/mc/game/version_manifest_v2.json") == null,
                "未知域名不应该镜像");
            Require(NetworkDownloader.MirrorMojangUri("http://piston-meta.mojang.com/mc/game/version_manifest_v2.json") == null,
                "明文 HTTP 不应该被路由");
            Require(NetworkDownloader.Routes(official, "official").Count == 1,
                "仅官方线路模式");
            Require(NetworkDownloader.Routes(official, "mirror").First().Equals(mirror,
                StringComparison.OrdinalIgnoreCase), "镜像优先线路模式");
            Require(NetworkDownloader.Routes("https://api.modrinth.com/v2/search", "mirror").Count == 1,
                "第三方 API 不应该被重写");

            var manifest = "{\"versions\":[" +
                "{\"id\":\"1.20.6\",\"type\":\"release\",\"releaseTime\":\"2024-04-29T10:00:00+00:00\"}," +
                "{\"id\":\"24w14a\",\"type\":\"snapshot\",\"releaseTime\":\"2024-04-03T10:00:00+00:00\"}," +
                "{\"id\":\"b1.7.3\",\"type\":\"old_beta\",\"releaseTime\":\"2011-07-08T00:00:00+00:00\"}" +
                "]}";
            var versions = MinecraftVersionCatalog.Parse(manifest);
            Require(versions.Count == 3, "版本索引解析数量");
            Require(versions.Any(x => x.Id == "1.20.6" && x.Category == "正式版"),
                "正式版分类");
            Require(versions.Any(x => x.Id == "24w14a" && x.Category == "快照版"),
                "快照版分类");
            Require(versions.Any(x => x.Id == "b1.7.3" && x.Category == "远古 Beta"),
                "远古版本分类");
            Require(DownloadJob.Format(1536).Contains("KiB"),
                "下载速率字节单位");
            Require(DownloadJob.Format(5L * 1024 * 1024).Contains("MiB"),
                "下载大小单位");
            Require(LauncherReleaseService.IsCompatibleWpfAssetName(
                "BlockCraftLauncher-v0.9.28-WPF-OneFile-Windows-x64.exe"),
                "WPF 单文件正式发布资产识别");
            Require(LauncherReleaseService.IsCompatibleWpfAssetName(
                "BlockCraftLauncher-v0.9.28-WPF-net48-Windows-x64.zip"),
                "WPF 稳定版完整 ZIP 发布资产识别");
            Require(!LauncherReleaseService.IsCompatibleWpfAssetName(
                "BlockCraftLauncher-v0.9.28-WPF-net48-Alpha-Windows-x64.zip"),
                "Alpha 测试包不能冒充稳定版资产");
            Require(!LauncherReleaseService.IsCompatibleWpfAssetName(
                "BlockCraftLauncher_v0.9.26_x64.exe"),
                "旧 Python 安装包不能作为 WPF 更新");
            Require(!LauncherReleaseService.IsCompatibleWpfAssetName(
                "BlockCraftRoomStage9.exe"),
                "网易房间实验包不能作为 WPF 更新");
            Require(!LauncherReleaseService.IsCompatibleWpfAssetName(
                "other-BlockCraftLauncher-v0.9.28-WPF-net48-Windows.zip"),
                "非 BlockCraft Launcher 规范命名资产不能作为自动更新");
            Require(!LauncherReleaseService.IsNewer("v999999999999999999999.0.0", "v0.9.28"),
                "无效过大版本号不应导致更新检测崩溃");
            // The real GitHub repository may have only prerelease room-lab
            // builds. Its stable-update endpoint must show a useful empty
            // state instead of failing with /releases/latest HTTP 404.
            var empty = LauncherReleaseService.SelectStableRelease("[]");
            Require(!empty.CompatibleWpf && !empty.Newer &&
                empty.Version == "尚未发布",
                "没有正式 WPF 发布时正常提示而不是误报");
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            string sample = serializer.Serialize(new object[] {
                new {
                    tag_name = "v9.99.0", draft = false, prerelease = true,
                    html_url = "https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v9.99.0",
                    assets = new[] { new { name = "BlockCraftLauncher-v9.99.0-WPF-OneFile-Windows-x64.exe" } },
                    body = "roomlab testing only"
                },
                new {
                    tag_name = "v0.9.30", draft = false, prerelease = false,
                    html_url = "https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.30",
                    assets = new[] { new { name = "BlockCraftRoomStage9.exe" } },
                    body = "Not a Java launcher release"
                },
                new {
                    tag_name = "v0.9.29", draft = false, prerelease = false,
                    html_url = "https://evil.example/releases/tag/v0.9.29",
                    assets = new[] { new { name = "BlockCraftLauncher-v0.9.29-WPF-OneFile-Windows-x64.exe" } },
                    body = "Untrusted link"
                },
                new {
                    tag_name = "v0.9.31", draft = false, prerelease = false,
                    html_url = "https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.31",
                    assets = new[] { new { name = "BlockCraftLauncher-v0.9.31-WPF-OneFile-Windows-x64.exe" } },
                    body = "WPF stable"
                },
                new {
                    tag_name = "v0.9.28", draft = false, prerelease = false,
                    html_url = "https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.28",
                    assets = new[] { new { name = "BlockCraftLauncher-v0.9.28-WPF-net48-Windows-x64.zip" } },
                    body = "Older WPF stable"
                }
            });
            var newest = LauncherReleaseService.SelectStableRelease(sample);
            Require(newest.Version == "v0.9.31" && newest.CompatibleWpf &&
                newest.Newer && newest.Url.EndsWith("/v0.9.31"),
                "选择兼容的最新正式 WPF Release");
            Require(!LauncherReleaseService.SelectStableRelease(
                serializer.Serialize(new object[] {
                    new { tag_name = "room-lab", prerelease = true }
                })).CompatibleWpf,
                "房间实验发布不能成为正式 WPF 更新");
            // This runs a simulated HTTP transport (no DNS or credentials)
            // through the SAME CmlLib game routing handler as production.
            ChinaDownloadAndCurseForgeSanityChecks.Run();
            LauncherAutoUpdateSanityChecks.Run();
            MinecraftRouteSanityChecks.RunAsync().GetAwaiter().GetResult();
        }
    }
}