using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Fully offline regression coverage for mainland-China download route
    /// selection, private partial-file origin markers, and the CurseForge
    /// visual metadata parser. No real network or game data is accessed.
    /// </summary>
    internal static class ChinaDownloadAndCurseForgeSanityChecks
    {
        static void Require(bool expected, string message)
        {
            if (!expected) throw new InvalidOperationException("国内下载/CurseForge 自检失败：" + message);
        }

        internal static void Run()
        {
            const string official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
            var primary = NetworkDownloader.Routes(official, "china");
            Require(primary.Count == 2 && primary[0].StartsWith("https://bmclapi2.bangbang93.com/") &&
                primary[1] == official, "国内优先游戏清单，镜像失败时回退官方");
            Require(NetworkDownloader.Routes(official, "official").Count == 1,
                "仅官方设置不使用 BMCLAPI");

            string modrinth = "https://cdn.modrinth.com/data/abc/versions/x/example.jar";
            string mirrored = NetworkDownloader.MirrorCommunityUri(modrinth);
            Require(mirrored == "https://mod.mcimirror.top/data/abc/versions/x/example.jar",
                "官方 Modrinth CDN 的 MCIM 映射");
            Require(NetworkDownloader.MirrorCommunityUri(
                "https://api.modrinth.com/v2/project/test") ==
                "https://mod.mcimirror.top/modrinth/v2/project/test",
                "Modrinth API 映射");
            Require(NetworkDownloader.MirrorCommunityUri(
                "https://api.minecraftservices.com/authentication/login_with_xbox") == null,
                "微软身份认证接口绝不能使用社区镜像");
            Require(NetworkDownloader.MirrorCommunityUri(
                "http://cdn.modrinth.com/data/fake.jar") == null,
                "社区镜像拒绝不安全的 HTTP");
            Require(NetworkDownloader.MirrorCommunityUri(
                "https://user:password@cdn.modrinth.com/data/fake.jar") == null,
                "社区镜像拒绝隐私凭据");
            Require(NetworkDownloader.MirrorCommunityUri(
                "https://evil.example/data/fake.jar") == null,
                "社区镜像拒绝陌生域名");
            Require(NetworkDownloader.RoutesForDownload(modrinth, "china", false).Count == 1,
                "未主动启用 MCIM 不劫持 Modrinth 下载");
            var optedIn = NetworkDownloader.RoutesForDownload(modrinth, "china", true);
            Require(optedIn.Count == 2 && optedIn[0] == mirrored && optedIn[1] == modrinth,
                "中国大陆模式且启用 MCIM 时镜像先行、官方回退");
            Require(NetworkDownloader.RoutesForDownload(modrinth, "international", true)
                .SequenceEqual(new[] { modrinth, mirrored }), "海外模式优先使用 Modrinth 官方源");
            Require(NetworkDownloader.RoutesForDownload(modrinth, "official", true).Count == 1,
                "官方模式禁用社区重写");

            string scratch = Path.Combine(Path.GetTempPath(), "BlockCraft-China-Download-" +
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                string part = Path.Combine(scratch, "example.blockcraft.part");
                const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                var first = NetworkDownloader.PreparePartialForRoute(part, modrinth, hash);
                Require(first == 0, "首次下载从零开始");
                File.WriteAllBytes(part, new byte[] { 1, 2, 3, 4 });
                Require(NetworkDownloader.PreparePartialForRoute(part, modrinth, hash) == 4,
                    "同来源同 SHA-1 可以继续断点");
                Require(NetworkDownloader.PreparePartialForRoute(part, mirrored, hash) == 0 &&
                    !File.Exists(part), "切换官方/镜像时不能拼接不同来源的半文件");
                string text = File.ReadAllText(part + ".origin");
                Require(!text.Contains("https://") && !text.Contains("password"),
                    "断点来源记录不保存敏感 URL");
                File.WriteAllBytes(part, new byte[] { 7, 8 });
                Require(NetworkDownloader.PreparePartialForRoute(part, mirrored,
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb") == 0,
                    "版本哈希变化时必须重下");
            }
            finally { Directory.Delete(scratch, true); }

            // Private developer credentials must never reach MCIM, a mirror,
            // an arbitrary redirect, or a lookalike domain. These assertions
            // run OFFLINE and do not require the owner's real key.
            Require(CurseForgeMirrorClient.MaySendOwnerKey(
                "https://api.curseforge.com/v1/mods/search?gameId=432") &&
                CurseForgeMirrorClient.MaySendOwnerKey(
                "https://edge.forgecdn.net/files/123/4/example.jar"),
                "仅官方 CurseForge API 和官方边缘 CDN 可以接收用户私有 Key");
            foreach (string untrusted in new[] {
                "https://mod.mcimirror.top/curseforge/v1/mods/search",
                "https://api.curseforge.com.evil.example/v1/mods/search",
                "https://edge.forgecdn.net.evil.example/files/123/4/x.jar",
                "https://mediafilez.forgecdn.net/files/123/4/x.jar",
                "http://api.curseforge.com/v1/mods/search",
                "https://user:password@api.curseforge.com/v1/mods/search",
                "https://api.curseforge.com:8443/v1/mods/search",
                "https://api.curseforge.com/v1/mods/search#fragment",
                "https://evil.example/download"
            })
                Require(!CurseForgeMirrorClient.MaySendOwnerKey(untrusted),
                    "不应向任何非授权主机转发用户私有 API Key");

            // HMCL-style online query parameters must be sent to the source,
            // not faked by filtering only a few labels on the current page.
            string online = CurseForgeMirrorClient.BuildSearchPath(
                "sodium", "1.20.1", "Fabric", 1, 2);
            Require(online.Contains("gameId=432") &&
                online.Contains("classId=6") &&
                online.Contains("gameVersion=1.20.1") &&
                online.Contains("modLoaderType=4") &&
                online.Contains("sortField=6") &&
                online.Contains("index=40") &&
                online.Contains("pageSize=20"),
                "在线版本/加载器/排序/翻页要调用 CurseForge API");
            Require(CurseForgeMirrorClient.BuildSearchPath(
                "灯光 & Sodium", "", "", 0, 0).IndexOf(
                    "searchFilter=%e7%81%af%e5%85%89%20%26%20Sodium",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                "中文、空格和特殊符号必须 URL 编码");
            bool invalidSearch = false;
            try { CurseForgeMirrorClient.BuildSearchPath("", "", "Fabric", 0, 0); }
            catch (ArgumentException) { invalidSearch = true; }
            Require(invalidSearch, "官方要求加载器筛选必须搭配游戏版本");
            invalidSearch = false;
            try { CurseForgeMirrorClient.BuildSearchPath("", "", "", 0, 500); }
            catch (ArgumentOutOfRangeException) { invalidSearch = true; }
            Require(invalidSearch, "CurseForge 最大只能访问前 10000 项，超出需拒绝");
            var profile = new GameProfile { BaseVersion = "1.20.1", Loader = "fabric" };
            var compatibleFiles = CurseForgeMirrorClient.ParseCompatibleFiles(new object[]
            {
                new System.Collections.Generic.Dictionary<string, object> {
                    { "id", 124L }, { "displayName", "Safe Fabric release" },
                    { "fileName", "safe-1.20.1.jar" },
                    { "fileDate", "2026-11-12T12:30:00Z" },
                    { "modLoaderType", 4 }, { "releaseType", 1 },
                    { "gameVersions", new object[] { "1.20.1" } }
                },
                new System.Collections.Generic.Dictionary<string, object> {
                    { "id", 125L }, { "displayName", "Incompatible Forge" },
                    { "fileName", "other-1.20.1.jar" },
                    { "modLoaderType", 1 },
                    { "gameVersions", new object[] { "1.20.1" } }
                }
            }, profile);
            Require(compatibleFiles.Count == 1 &&
                compatibleFiles[0].FileId == 124 &&
                compatibleFiles[0].DisplayName.Contains("Fabric") &&
                compatibleFiles[0].DateLabel.Contains("正式版"),
                "选择文件时只显示与当前 Minecraft/Fabric 兼容的文件");

            const string json = @"[
              {
                ""id"": 123, ""name"": ""Example Mod"", ""summary"": ""A sample mod with images"",
                ""downloadCount"": 36000,
                ""logo"": {
                  ""thumbnailUrl"": ""https://media.forgecdn.net/avatars/123/456/icon.png"",
                  ""url"": ""https://media.forgecdn.net/avatars/123/456/icon.png""
                },
                ""authors"": [{ ""name"": ""Creator"" }],
                ""categories"": [{ ""name"": ""Technology"" }],
                ""latestFilesIndexes"": [
                  { ""gameVersion"": ""1.21.1"", ""modLoader"": 4 },
                  { ""gameVersion"": ""1.20.1"", ""modLoader"": 1 }
                ],
                ""links"": { ""websiteUrl"": ""https://www.curseforge.com/minecraft/mc-mods/example"" },
                ""screenshots"": [
                  {
                    ""title"": ""In game"",
                    ""thumbnailUrl"": ""https://media.forgecdn.net/attachments/123/456/thumb.png"",
                    ""url"": ""https://media.forgecdn.net/attachments/123/456/full.png""
                  },
                  { ""title"": ""Unsafe"", ""url"": ""http://evil.example/img.png"" }
                ]
              },
              {
                ""id"": 124, ""name"": ""Fallback"", ""summary"": ""No artwork available"",
                ""logo"": { ""thumbnailUrl"": ""https://untrusted.example/icon.png"" },
                ""links"": { ""websiteUrl"": ""https://evil.example/minecraft/mc-mods/test"" }
              }]";
            var parsed = new JavaScriptSerializer().DeserializeObject(json);
            var hits = CurseForgeMirrorClient.ParseSearch(parsed);
            Require(hits.Count == 2, "搜索列表读取所有有效项目");
            Require(hits[0].Id == 123 && hits[0].Name == "Example Mod",
                "保持模组 ID 和名称以供正确安装");
            Require(hits[0].IconUrl ==
                "https://media.forgecdn.net/avatars/123/456/icon.png",
                "显示真实官方 CurseForge 项目封面");
            Require(hits[0].Screenshots.Count == 1 &&
                hits[0].Screenshots[0].ImageUrl.EndsWith("/full.png"),
                "仅显示来自安全 CDN 的原作者截图");
            Require(hits[0].AuthorLabel == "Creator" &&
                hits[0].VersionsLabel.Contains("1.21.1") &&
                hits[0].DownloadsLabel.Contains("万"),
                "卡片展示作者、近期版本和下载量");
            Require(hits[0].OfficialUrl ==
                "https://www.curseforge.com/minecraft/mc-mods/example",
                "只打开真实 CurseForge 项目官方链接");
            Require(hits[1].IconUrl == null && hits[1].Screenshots.Count == 0 &&
                hits[1].OfficialUrl == null,
                "缺少或不安全的图片/外链使用占位与空状态");
            Require(CurseForgeMirrorClient.TrustedArtworkUrl(
                "https://evil.example/avatars/icon.png") == null &&
                CurseForgeMirrorClient.TrustedArtworkUrl(
                    "https://media.forgecdn.net:444/avatars/icon.png") == null,
                "不接受陌生主机和自定义端口");
            bool invalid = false;
            try
            {
                CurseForgeMirrorClient.ParseSearch(new object[] {
                    new System.Collections.Generic.Dictionary<string, object> {
                        { "id", -1 }, { "name", "Invalid" }
                    }
                });
            }
            catch (InvalidDataException) { invalid = true; }
            Require(invalid, "拒绝异常的 CurseForge 模组 ID");
        }
    }
}
