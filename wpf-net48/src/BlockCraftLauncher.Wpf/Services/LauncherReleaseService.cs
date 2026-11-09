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
        const string Api = "https://api.github.com/repos/minecraftlu/BlockCraftLauncher/releases/latest";
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
                (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<ReleaseNotice> CheckAsync(LauncherConfig config, CancellationToken ct)
        {
            using (var network = new NetworkDownloader(config))
            {
                string data = await network.GetStringWithFallbackAsync(Api, "official", ct);
                var root = Json.DeserializeObject(data) as Dictionary<string, object>
                    ?? throw new InvalidOperationException("GitHub Releases 返回数据无效");
                string tag = root.TryGetValue("tag_name", out var t) ? Convert.ToString(t) : "";
                string description = root.TryGetValue("body", out var b) ? Convert.ToString(b) : "";
                string url = root.TryGetValue("html_url", out var h) ? Convert.ToString(h) : "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                    !uri.AbsolutePath.StartsWith("/minecraftlu/BlockCraftLauncher/releases/", StringComparison.Ordinal))
                    url = OfficialReleases;
                // Do not advertise a legacy Python release (or a room-lab
                // build) as a directly compatible WPF upgrade merely because
                // its tag contains a larger semantic version.
                bool wpfAsset = false;
                if (root.TryGetValue("assets", out var rawAssets) && rawAssets is object[] assets)
                {
                    foreach (var asset in assets)
                    {
                        if (!(asset is Dictionary<string, object> file)) continue;
                        string name = file.TryGetValue("name", out var n) ? Convert.ToString(n) : "";
                        if (IsCompatibleWpfAssetName(name))
                        {
                            wpfAsset = true;
                            break;
                        }
                    }
                }
                return new ReleaseNotice
                {
                    Version = tag,
                    Description = (description ?? "").Length > 1200 ? description.Substring(0, 1200) : description,
                    Url = url,
                    CompatibleWpf = wpfAsset,
                    Newer = wpfAsset && IsNewer(tag, "v0.9.27")
                };
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