using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        internal async Task<string> VerifyLiveQwenGameAsync()
        {
            const string version = "1.7.10";
            if (qwenLiveFolder == null || !string.Equals(settings.Root, Path.Combine(qwenLiveFolder, "downloads", "Minecraft"), StringComparison.OrdinalIgnoreCase) ||
                !settings.GameRootFollowsDownloads ||
                settings.NetworkMode != "official" || gameVersions.Count != 0 || downloads.Jobs.Any())
                throw new InvalidOperationException("The legacy download directory did not redirect the game to isolated storage.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(7));
            var token = timeout.Token;
            var json = new JavaScriptSerializer { MaxJsonLength = 10 * 1024 * 1024 };
            DownloadJob job = null;
            try
            {
                Show();
                bool modelCalled = false;
                qwenRunSearch = (question, key, endpoint, model, ct) => {
                    modelCalled = true;
                    throw new InvalidOperationException("Unexpected Qwen model request.");
                };
                Navigate(QwenSearchPage);
                SelectQwenFixtureChat();
                qwenQuery.Text = "下载 Minecraft " + version;
                var submit = OpenQwenPageAsync();
                if (await Task.WhenAny(submit, Task.Delay(Timeout.Infinite, token)) != submit)
                    throw new TimeoutException("Timed out submitting the Minecraft installation command.");
                await submit;
                job = downloads.Jobs.SingleOrDefault();
                if (job?.Kind != "游戏" || job.Title != version + " · 原版")
                    throw new InvalidDataException("Game installation was not queued: " + qwenSearchStatus.Text);
                while (job.IsRunning) await Task.Delay(200, token);
                if (job.Status != "已完成")
                    throw new IOException("Minecraft installation failed: " + job.Error);

                var profile = settings.Profiles.SingleOrDefault(p => p.BaseVersion == version && p.Loader == "vanilla");
                var registered = store.Load().Profiles.SingleOrDefault(p => p.Id == profile?.Id);
                if (profile == null || registered?.BaseVersion != version || registered.LaunchVersion != version ||
                    registered.Loader != "vanilla" || registered.Directory != settings.Root ||
                    modelCalled || launchStages.Count != 0 ||
                    settings.Profiles.Any(p => gameProcesses.GetState(p).Status != GameProcessStatus.NotTracked))
                    throw new InvalidDataException("Unexpected installed profile, model call or game launch.");

                if (usingCachedVersions || !File.Exists(qwenGameManifestCachePath))
                    throw new InvalidDataException("Missing fresh Mojang version manifest.");
                var manifest = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(qwenGameManifestCachePath));
                var entries = ((System.Collections.IEnumerable)manifest["versions"]).Cast<Dictionary<string, object>>();
                var entry = entries.Single(e => Equals(e["id"], version) && Equals(e["type"], "release"));
                var metadataUri = new Uri((string)entry["url"]);
                string expectedJsonSha1 = (string)entry["sha1"];
                if (metadataUri.Scheme != Uri.UriSchemeHttps || !metadataUri.IsDefaultPort ||
                    metadataUri.UserInfo.Length != 0 || metadataUri.Host != "piston-meta.mojang.com")
                    throw new InvalidDataException("Unrecognized Mojang version metadata location.");
                string referencePath = Path.Combine(qwenLiveFolder, "mojang-1.7.10.json");
                using (var network = new NetworkDownloader(settings))
                    await network.DownloadVerifiedDetailedAsync(metadataUri.AbsoluteUri, referencePath, expectedJsonSha1, token);

                string game = Path.Combine(settings.Root, "versions", version);
                VerifyQwenLiveGameFile(Path.Combine(game, version + ".json"), new FileInfo(referencePath).Length, expectedJsonSha1);
                var metadata = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(referencePath));
                var downloadsMetadata = (Dictionary<string, object>)metadata["downloads"];
                var client = (Dictionary<string, object>)downloadsMetadata["client"];
                long clientSize = Convert.ToInt64(client["size"]);
                string clientSha1 = (string)client["sha1"];
                VerifyQwenLiveGameFile(Path.Combine(game, version + ".jar"), clientSize, clientSha1);
                var assets = (Dictionary<string, object>)metadata["assetIndex"];
                string index = Path.Combine(settings.Root, "assets", "indexes", (string)assets["id"] + ".json");
                VerifyQwenLiveGameFile(index, Convert.ToInt64(assets["size"]), (string)assets["sha1"]);
                if (!Directory.EnumerateFiles(Path.Combine(settings.Root, "libraries"), "*.jar", SearchOption.AllDirectories).Any() ||
                    !Directory.EnumerateFiles(Path.Combine(settings.Root, "assets", "objects"), "*", SearchOption.AllDirectories).Any())
                    throw new InvalidDataException("Installed Minecraft libraries or assets are missing.");
                return "LIVE PASS: typed Minecraft " + version + " installation completed and registered a vanilla profile. " +
                    "Legacy custom storage has metadata, client, assets and libraries in " + settings.Root + ". Mojang JSON SHA-1: " + expectedJsonSha1 + "; client SHA-1: " + clientSha1 + " (" + clientSize +
                    " bytes); asset index verified, libraries and assets installed. No game launch or Qwen API call.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw new TimeoutException("Minecraft install timed out: " + job?.Status + " " + job?.Detail);
            }
            finally
            {
                CancelQwenSearch();
                foreach (var pending in downloads.Jobs.Where(j => j.IsRunning)) pending.Cancel();
                for (int i = 0; i < 100 && downloads.Jobs.Any(j => j.IsRunning); i++) await Task.Delay(100);
                Close();
                if (!downloads.Jobs.Any(j => j.IsRunning)) Directory.Delete(qwenLiveFolder, true);
            }
        }

        static void VerifyQwenLiveGameFile(string path, long expectedLength, string expectedSha1)
        {
            using var file = File.OpenRead(path);
            using var sha1 = SHA1.Create();
            var actual = BitConverter.ToString(sha1.ComputeHash(file)).Replace("-", "");
            if (file.Length != expectedLength || !string.Equals(actual, expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Size or SHA-1 differs from Mojang metadata: " + Path.GetFileName(path));
        }
    }
}
