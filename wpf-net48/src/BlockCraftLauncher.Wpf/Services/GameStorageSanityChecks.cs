using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class GameStorageSanityChecks
    {
        internal static void Run(string folder)
        {
            string file = Path.Combine(folder, "storage-settings.json");
            var store = new SettingsStore(file);
            var serializer = new JavaScriptSerializer();
            string cache = Path.Combine(folder, "chosen downloads");
            var legacy = new { root = LauncherConfig.DefaultGameRoot, download_directory = cache,
                profiles = new[] { new { id = "old", base_version = "1.19", launch_version = "1.19", dir = "" } } };
            File.WriteAllText(file, serializer.Serialize(legacy));
            var settings = store.Load();
            string managed = Path.Combine(cache, "Minecraft");
            void Check(bool ok, string message)
            {
                if (!ok) throw new InvalidDataException("游戏下载位置自检失败：" + message);
            }
            Check(settings.GameRootFollowsDownloads && settings.Root == managed && settings.DownloadDirectory == cache,
                "旧设置只调整下载位置时，新的游戏安装必须跟随该位置。");
            Check(settings.Profiles.Single().Directory == LauncherConfig.DefaultGameRoot &&
                settings.Profiles.Single().SharedGameRoot == LauncherConfig.DefaultGameRoot, "旧实例位置发生变化。");
            foreach (var child in new[] { DownloadPathService.VersionManifest(cache), DownloadPathService.ForgeTempRoot(cache),
                DownloadPathService.ModpackCache(cache), DownloadPathService.UpdatesRoot(cache) })
                Check(DownloadPathService.ContainsOrEquals(cache, child), "下载临时文件离开了选定目录。");
            DownloadPathService.EnsureGameStorageAvailable(settings);
            var frozen = settings.CopyForInstall();
            string separate = Path.Combine(folder, "explicit-game");
            Check(DownloadPathService.TryPlanStorage(separate, cache, false, out var game, out var downloads, out _)
                && game == separate, "未保留手动指定的游戏目录。");
            var inherited = new GameProfile { Id = "inherited", Directory = Path.Combine(folder, "instance"),
                BaseVersion = "1.18", LaunchVersion = "1.18" };
            settings.Profiles.Add(inherited);
            var json = Path.Combine(managed, "versions", "1.18", "1.18.json");
            Directory.CreateDirectory(Path.GetDirectoryName(json));
            File.WriteAllText(json, "{}");
            DownloadPathService.ApplyStorage(settings, game, downloads, false);
            store.Save(settings);
            var reopened = store.Load();
            var old = reopened.Profiles.Single(p => p.Id == "inherited");
            var location = MinecraftInstanceLocator.Resolve(old, reopened);
            Check(!reopened.GameRootFollowsDownloads && reopened.Root == separate &&
                old.Directory == inherited.Directory && location.MetadataFound && location.GameRoot == managed,
                "重开后没有沿用旧实例的共享版本。");
            Check(frozen.Root == managed && frozen.DownloadDirectory == cache, "编辑设置影响了正在运行的下载。");

            string missing = Enumerable.Range('D', 'Z' - 'D' + 1).Select(code => (char)code + @":\")
                .FirstOrDefault(volume => !Directory.Exists(volume));
            if (missing != null)
            {
                string offline = Path.Combine(missing, "BlockCraft disconnected storage");
                File.WriteAllText(file, serializer.Serialize(new { root = LauncherConfig.DefaultGameRoot, download_directory = offline }));
                var detached = store.Load();
                Check(detached.Root == Path.Combine(offline, "Minecraft") && detached.DownloadDirectory == offline,
                    "外置磁盘离线时回退到了其他磁盘。");
                try { DownloadPathService.EnsureGameStorageAvailable(detached); }
                catch (DirectoryNotFoundException ex) when (ex.Message.Contains(missing)) { return; }
                throw new InvalidDataException("磁盘离线时安装没有报告目标路径。");
            }
        }
    }
}
