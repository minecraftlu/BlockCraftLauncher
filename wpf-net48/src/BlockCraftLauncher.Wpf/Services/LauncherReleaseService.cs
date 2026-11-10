using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class ReleaseNotice
    {
        public string Version { get; set; }
        public string Url { get; set; }
        public string Description { get; set; }
        public bool Newer { get; set; }
        public bool CompatibleWpf { get; set; }
    }

    /// <summary>Read-only GitHub Releases update check. Never executes downloaded files.</summary>
    public sealed class LauncherReleaseService
    {
        // /releases/latest returns 404 when only prereleases exist. Use the
        // list endpoint and filter on channel, asset type and version instead.
        const string Api = "https://api.github.com/repos/minecraftlu/BlockCraftLauncher/releases?per_page=100";
        const string OfficialReleases = "https://github.com/minecraftlu/BlockCraftLauncher/releases";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        static int[] VersionParts(string value)
        {
            var match = Regex.Match(value ?? "", @"^v?(\d+)\.(\d+)\.(\d+)(?:[.-].*)?$", RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            if (!int.TryParse(match.Groups[1].Value, out var major) ||
                !int.TryParse(match.Groups[2].Value, out var minor) ||
                !int.TryParse(match.Groups[3].Value, out var patch)) return null;
            return new[] { major, minor, patch };
        }
        public static bool IsNewer(string remote, string current)
        {
            var a = VersionParts(remote); var b = VersionParts(current);
            if (a == null || b == null) return false;
            for (int i = 0; i < 3; i++)
            {
                if (a[i] != b[i]) return a[i] > b[i];
            }
            return false;
        }
        public static bool IsCompatibleWpfAssetName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                name.StartsWith("BlockCraftLauncher-v", StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf("-WPF-", StringComparison.OrdinalIgnoreCase) >= 0 &&
                name.IndexOf("-Alpha-", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("-Preview-", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("-Beta-", StringComparison.OrdinalIgnoreCase) < 0 &&
                (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        static bool GetBoolean(Dictionary<string, object> entry, string key)
        {
            return entry.TryGetValue(key, out var value) && value is bool result && result;
        }

        static string GetString(Dictionary<string, object> entry, string key)
        {
            return entry.TryGetValue(key, out var value) ? Convert.ToString(value) ?? "" : "";
        }

        static bool ValidOfficialReleaseLink(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith("/minecraftlu/BlockCraftLauncher/releases/tag/",
                    StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parse official GitHub release metadata without ever selecting draft,
        /// prerelease, NetEase room-lab or legacy Python assets as WPF updates.
        /// Does not access the network; intended for offline regression tests.
        /// </summary>
        internal static ReleaseNotice SelectStableRelease(string json,
            string currentVersion = LauncherAutoUpdateService.CurrentTag)
        {
            var releases = Json.DeserializeObject(json) as object[]
                ?? throw new InvalidOperationException("GitHub Releases 返回列表格式无效");
            ReleaseNotice newest = null;
            foreach (var item in releases)
            {
                if (!(item is Dictionary<string, object> entry)) continue;
                if (GetBoolean(entry, "draft") || GetBoolean(entry, "prerelease")) continue;

                string tag = GetString(entry, "tag_name");
                // Stable versions must have plain three-component numeric
                // tags. A version suffix is not sufficient proof of stability.
                if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.IgnoreCase) ||
                    VersionParts(tag) == null) continue;
                string link = GetString(entry, "html_url");
                if (!ValidOfficialReleaseLink(link)) continue;

                bool wpfAsset = false;
                if (entry.TryGetValue("assets", out var raw) && raw is object[] assets)
                {
                    foreach (var asset in assets)
                    {
                        if (asset is Dictionary<string, object> file &&
                            IsCompatibleWpfAssetName(GetString(file, "name")))
                        {
                            wpfAsset = true;
                            break;
                        }
                    }
                }
                if (!wpfAsset) continue;
                if (newest != null && !IsNewer(tag, newest.Version)) continue;

                string description = GetString(entry, "body");
                newest = new ReleaseNotice
                {
                    Version = tag,
                    Url = link,
                    Description = description.Length > 1200 ?
                        description.Substring(0, 1200) : description,
                    CompatibleWpf = true,
                    Newer = wpfAsset && IsNewer(tag, currentVersion)
                };
            }

            return newest ?? new ReleaseNotice
            {
                Version = "尚未发布",
                Url = OfficialReleases,
                Description = "目前官方 Releases 尚无可验证为 WPF Windows 的正式稳定版；" +
                    "请继续使用已经测试过的本地版本，或手动查看预览构建。",
                CompatibleWpf = false,
                Newer = false
            };
        }

        public async Task<ReleaseNotice> CheckAsync(LauncherConfig config, CancellationToken ct)
        {
            using (var network = new NetworkDownloader(config))
            {
                string json = await network.GetStringWithFallbackAsync(Api, "official", ct);
                return SelectStableRelease(json);
            }
        }
        public static void OpenOfficialRelease(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps ||
                u.Host != "github.com" ||
                !u.AbsolutePath.StartsWith("/minecraftlu/BlockCraftLauncher/releases", StringComparison.Ordinal))
                throw new ArgumentException("非官方 GitHub Releases 链接");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}