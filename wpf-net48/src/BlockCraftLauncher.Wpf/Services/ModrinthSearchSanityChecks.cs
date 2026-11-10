using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Offline project-row parsing fixtures. No Modrinth API request or icon
    /// download is performed. Shows only authoritative API-provided counts.
    /// </summary>
    internal static class ModrinthSearchSanityChecks
    {
        static void Require(bool ok, string message)
        {
            if (!ok)
                throw new InvalidOperationException("PCL Modrinth search layout regression: " + message);
        }

        static string BuildPage()
        {
            var rows = new object[]
            {
                new Dictionary<string, object>
                {
                    ["project_id"] = "AABBCCDD",
                    ["title"] = "Sodium",
                    ["description"] = "Optimizes Minecraft rendering without replacing the game",
                    ["author"] = "test-author",
                    ["icon_url"] = "https://cdn.modrinth.com/data/AABBCCDD/icon.png",
                    ["downloads"] = 250000000L,
                    ["date_modified"] = "2026-10-31T06:00:00Z",
                    ["categories"] = new object[] { "optimization", "fabric", "neoforge" },
                    ["versions"] = new object[] { "1.20.1", "1.21.1", "1.21.8", "1.21.11" }
                },
                new Dictionary<string, object>
                {
                    ["project_id"] = "ZZYYXXWW",
                    ["title"] = "Test mod",
                    ["description"] = new string('长', 320),
                    ["icon_url"] = "https://unsafe.example.com/not-a-modrinth-icon.png",
                    ["downloads"] = 2000L,
                    ["categories"] = new object[] { "library", "quilt" },
                    ["versions"] = new object[] { "1.21.1" }
                }
            };
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                ["hits"] = rows,
                ["total_hits"] = 55,
                ["offset"] = 0,
                ["limit"] = 20
            });
        }

        internal static void Run()
        {
            var page = ModrinthClient.ParseSearchResponse(BuildPage(), "mod");
            Require(page.Hits.Count == 2, "valid result cards parsed");
            Require(page.TotalHits == 55, "total hits used for pagination");
            var first = page.Hits[0];
            Require(first.Name == "Sodium" && first.Author == "test-author",
                "title/author remain accurate");
            Require(first.IconUrl == "https://cdn.modrinth.com/data/AABBCCDD/icon.png",
                "approved project icon location");
            Require(first.DownloadsLabel == "2.5 亿次下载",
                "compact Chinese download count");
            Require(first.LoaderLabel.Contains("fabric") &&
                first.LoaderLabel.Contains("neoforge"),
                "loader metadata retained");
            Require(first.CategoryLabel.Contains("性能优化"),
                "display category localized");
            Require(first.VersionsLabel.Contains("1.21.11"),
                "latest supported Minecraft version shown");
            Require(first.UpdatedLabel.StartsWith("更新 ", StringComparison.Ordinal),
                "date from API is displayed");

            var second = page.Hits[1];
            Require(second.IconUrl == null,
                "third party icon hosts are never trusted");
            Require(second.Description.Length == 320,
                "long descriptions are preserved for UI tooltip");
            Require(second.DownloadsLabel == "2,000 次下载",
                "small download figures show units");

            Require(ModrinthSearchMetadata.TrustedIconUrl(
                "http://cdn.modrinth.com/data/AABBCCDD/icon.png") == null,
                "insecure images rejected");
            Require(ModrinthSearchMetadata.TrustedIconUrl(
                "https://cdn.modrinth.com.evil.invalid/data/icon.png") == null,
                "lookalike hosts rejected");
            Require(ModrinthSearchMetadata.TrustedIconUrl(
                "https://bad@cdn.modrinth.com/data/icon.png") == null,
                "credential-bearing icon URL rejected");
            Require(ModrinthSearchMetadata.DownloadCountLabel(-1) == "下载量未知",
                "unknown figures must not appear as zero");
            bool invalid = false;
            try { ModrinthClient.ParseSearchResponse("{}", "mod"); }
            catch (InvalidDataException) { invalid = true; }
            Require(invalid, "malformed response never shown as an empty successful page");
        }
    }
}
