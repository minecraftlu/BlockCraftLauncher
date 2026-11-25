using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        internal async Task<string> VerifyLiveForgeInstallAsync()
        {
            const string minecraft = "1.21.1";
            const string build = "52.1.0";
            if (qwenLiveFolder == null || !settings.GameRootFollowsDownloads ||
                !string.Equals(settings.Root, Path.Combine(qwenLiveFolder, "downloads", "Minecraft"), StringComparison.OrdinalIgnoreCase) ||
                settings.NetworkMode != "official" || downloads.Jobs.Any() || settings.Profiles.Any())
                throw new InvalidOperationException("Forge verification requires a clean isolated download directory.");

            DownloadJob job = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(18));
            try
            {
                Show();
                string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
                if (!string.IsNullOrWhiteSpace(javaHome))
                    settings.JavaPath = Path.Combine(javaHome, "bin", "java.exe");
                await RunQwenGameActionAsync(minecraft, "Forge", false, timeout.Token);
                if (currentPage != "游戏版本" || (versionList.SelectedItem as MinecraftVersionEntry)?.Id != minecraft)
                    throw new InvalidDataException("Could not select Minecraft " + minecraft + " from the public catalog.");
                using (var catalogDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
                {
                    while (!forgeBuildSelector.IsEnabled || !forgeBuildSelector.Items.Contains(build))
                        await Task.Delay(150, catalogDeadline.Token);
                }
                forgeBuildSelector.SelectedItem = build;
                if (!versionInstallButton.IsEnabled || forgeBuildSelector.Items.Count < 3)
                    throw new InvalidDataException("The public catalog did not offer multiple selectable Forge builds.");
                await InstallSelectedVersionAsync();
                job = downloads.Jobs.SingleOrDefault();
                if (job == null || !job.Title.Contains(build))
                    throw new InvalidDataException("The pinned Forge build was not passed to the download queue.");
                while (job.IsRunning) await Task.Delay(300, timeout.Token);
                if (job.Status != "已完成") throw new IOException("Forge installation failed: " + job.Error);

                var profile = store.Load().Profiles.Single(p => p.BaseVersion == minecraft && p.Loader == "forge");
                var location = MinecraftInstanceLocator.Resolve(profile, settings);
                if (!location.MetadataFound || !string.Equals(location.GameRoot, settings.Root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Registered Forge metadata is outside the selected game root.");
                var metadata = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(location.MetadataPath));
                if (!Equals(metadata["id"], profile.LaunchVersion) || !Equals(metadata["inheritsFrom"], minecraft))
                    throw new InvalidDataException("The installed Forge profile does not reference the requested game version.");
                string coordinate = minecraft + "-" + build;
                string forge = Path.Combine(settings.Root, "libraries", "net", "minecraftforge", "forge", coordinate);
                var files = Directory.GetFiles(forge, "*.jar").Where(f => new FileInfo(f).Length > 0).ToList();
                if (!files.Any(f => Path.GetFileName(f).StartsWith("forge-" + coordinate + "-", StringComparison.Ordinal)) ||
                    !profile.LaunchVersion.EndsWith(build, StringComparison.Ordinal) ||
                    !File.Exists(Path.Combine(settings.Root, "versions", minecraft, minecraft + ".jar")) ||
                    !Directory.EnumerateFiles(Path.Combine(settings.Root, "assets", "objects"), "*", SearchOption.AllDirectories).Any())
                    throw new InvalidDataException("The pinned Forge library, client JAR, or game assets are missing.");
                return "LIVE PASS: manually selected Forge " + build + " for Minecraft " + minecraft +
                    " in the public catalog and installed " + profile.LaunchVersion + " under " + settings.Root +
                    ". Verified the registered profile, exact Maven coordinate, client and assets.";
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out checking Forge " + build + ": " + (job?.Detail ?? StatusText.Text));
            }
            finally
            {
                job?.Cancel();
                for (int i = 0; i < 100 && job?.IsRunning == true; i++) await Task.Delay(100);
                Close();
                if (job?.IsRunning != true) Directory.Delete(qwenLiveFolder, true);
            }
        }
    }
}
