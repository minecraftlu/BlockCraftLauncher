using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        const string LiveGameVersion = "1.16.5";
        string qwenLiveFolder;

        internal static MainWindow CreateQwenLiveDownloadWindow(Func<ModrinthClient> modrinthFactory = null)
        {
            string folder = Path.Combine(Path.GetTempPath(), "blockcraft-qwen-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var isolated = new SettingsStore(Path.Combine(folder, "settings.json"));
                var config = new LauncherConfig {
                    Root = Path.Combine(folder, "minecraft"), DownloadDirectory = Path.Combine(folder, "downloads"),
                    AutoCheckUpdates = false, AutoDownloadUpdates = false, AutoRestartAfterUpdateDownload = false,
                    NetworkMode = "official", DownloadRetries = 2
                };
                var profile = new GameProfile {
                    Id = "qwen-live-fabric", Name = "临时 Fabric 实例", Directory = Path.Combine(folder, "instance"),
                    Loader = "fabric", BaseVersion = LiveGameVersion, LaunchVersion = LiveGameVersion
                };
                Directory.CreateDirectory(config.Root);
                Directory.CreateDirectory(config.DownloadDirectory);
                Directory.CreateDirectory(profile.Directory);
                config.Profiles.Add(profile);
                isolated.Save(config);
                return new MainWindow(null, null, isolated, modrinthFactory) {
                    qwenLiveFolder = folder,
                    qwenWebPageFactory = OfflineQwenWebPage,
                    qwenSettingsPath = Path.Combine(folder, "qwen.json"),
                    qwenGameManifestCachePath = Path.Combine(folder, "mojang-versions.json"),
                    ShowActivated = false, ShowInTaskbar = false, Opacity = 0, SuppressAutomaticUpdateCheck = true
                };
            }
            catch { Directory.Delete(folder, true); throw; }
        }

        internal async Task<string> VerifyLiveQwenDownloadAsync()
        {
            if (qwenLiveFolder == null || activeProfile == null)
                throw new InvalidOperationException("Live download check requires a fresh isolated window.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = timeout.Token;
            async Task Submit(string words)
            {
                Navigate(QwenSearchPage);
                SelectQwenFixtureChat();
                qwenQuery.Text = words;
                var command = OpenQwenPageAsync();
                if (await Task.WhenAny(command, Task.Delay(Timeout.Infinite, token)) != command)
                    throw new TimeoutException("Live launcher command timed out: " + words);
                await command;
            }
            try
            {
                Show();
                // A direct command must not use the companion or any paid API.
                int modelCalls = 0;
                qwenRunSearch = (question, key, endpoint, model, ct) => {
                    modelCalls++;
                    throw new InvalidOperationException("Unexpected Qwen model request.");
                };
                await Submit("搜索 Minecraft " + LiveGameVersion);
                if (currentPage != "游戏版本" || usingCachedVersions ||
                    (versionList?.SelectedItem as MinecraftVersionEntry)?.Id != LiveGameVersion ||
                    !File.Exists(qwenGameManifestCachePath) || downloads.Jobs.Any())
                    throw new InvalidDataException("Live Minecraft catalog search did not select " + LiveGameVersion + ". " + StatusText.Text);

                await Submit("从 Modrinth 下载模组 Sodium");
                var job = downloads.Jobs.SingleOrDefault();
                if (job == null || job.Kind != "Modrinth" || currentPage != "资源中心")
                    throw new InvalidOperationException("Sodium was not queued. " + (modSourceStatus?.Text ?? qwenSearchStatus.Text));
                while (job.IsRunning) await Task.Delay(100, token);
                if (job.Status != "已完成") throw new IOException("Sodium download failed: " + job.Error);
                string mods = Path.Combine(activeProfile.Directory, "mods");
                var jars = Directory.GetFiles(mods, "*.jar").Where(p =>
                    Path.GetFileName(p).StartsWith("sodium-", StringComparison.OrdinalIgnoreCase)).ToList();
                if (jars.Count != 1 || modelCalls != 0) throw new InvalidDataException("Unexpected installed JARs or model call.");

                string path = jars[0], digest;
                using (var file = File.OpenRead(path))
                using (var sha = SHA512.Create())
                    digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                string filename = Path.GetFileName(path);
                using (var network = new NetworkDownloader(settings))
                {
                    string json = await network.GetStringWithFallbackAsync(
                        "https://api.modrinth.com/v2/version_file/" + digest + "?algorithm=sha512", "official", token);
                    var metadata = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    bool Has(string key, string value) => metadata[key] is System.Collections.IEnumerable items &&
                        items.Cast<object>().Any(item => Equals(item, value));
                    var files = ((System.Collections.IEnumerable)metadata["files"]).Cast<Dictionary<string, object>>();
                    bool matches = files.Any(f => Equals(f["filename"], filename) &&
                        f["hashes"] is Dictionary<string, object> hashes && Equals(hashes["sha512"], digest));
                    if (!Equals(metadata["project_id"], "AANobbMI") || !Equals(metadata["version_type"], "release") ||
                        !Has("game_versions", LiveGameVersion) || !Has("loaders", "fabric") || !matches)
                        throw new InvalidDataException("Installed bytes do not match Sodium's official Fabric release metadata.");
                }
                string packs = await VerifyLivePackCommandsAsync(Submit, token);
                if (modelCalls != 0) throw new InvalidDataException("A direct resource installation invoked the AI provider.");
                return "LIVE PASS: Minecraft " + LiveGameVersion + " selected from Mojang's public manifest; " +
                    "Sodium installed by typed command into the temporary Fabric instance: " + filename +
                    " (" + new FileInfo(path).Length + " bytes). Modrinth SHA-512: " + digest +
                    ". No game process started or Qwen API call made." + Environment.NewLine + packs;
            }
            finally
            {
                Close();
                foreach (var job in downloads.Jobs.Where(j => j.IsRunning)) job.Cancel();
                for (int i = 0; i < 100 && downloads.Jobs.Any(j => j.IsRunning); i++) await Task.Delay(100);
                Directory.Delete(qwenLiveFolder, true);
            }
        }
    }
}
