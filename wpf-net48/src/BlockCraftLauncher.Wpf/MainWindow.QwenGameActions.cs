using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        Func<string, string, IProgress<string>, IProgress<DownloadBytes>, CancellationToken, Task<GameProfile>> qwenInstallGameFixture;
        string qwenGameManifestCachePath;

        static MinecraftVersionEntry MatchQwenGameVersion(IEnumerable<MinecraftVersionEntry> versions, string query)
        {
            if (query == "latest")
                return versions.FirstOrDefault(item => item.IsLatestRelease && item.Type == "release");
            return versions.FirstOrDefault(item => string.Equals(item.Id, query, StringComparison.OrdinalIgnoreCase));
        }

        async Task<string> RunQwenGameActionAsync(string version, string loader, bool install, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string query = (version ?? "").Trim();
            string selectedLoader = string.IsNullOrWhiteSpace(loader) ? "原版" :
                new[] { "原版", "Fabric", "Forge", "NeoForge", "Quilt" }.FirstOrDefault(
                    value => string.Equals(value, loader.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selectedLoader == null)
                return "请选择原版、Fabric、Forge、NeoForge 或 Quilt 加载器。";

            bool latest = query == "latest";
            if (latest || gameVersions.Count == 0)
            {
                var catalog = new MinecraftVersionCatalog(settings, qwenGameManifestCachePath);
                var entries = await catalog.LoadAsync(latest, token);
                token.ThrowIfCancellationRequested();
                gameVersions.Clear();
                foreach (var entry in entries) gameVersions.Add(entry);
                versionLoadError = null;
                usingCachedVersions = catalog.UsingCachedManifest;
                FilterVersions();
            }

            var selected = MatchQwenGameVersion(gameVersions, query);
            if (!install || selected == null || (latest && usingCachedVersions))
            {
                NavigateToQwenActionPage("游戏版本");
                versionCategory.SelectedIndex = latest ? 1 : 0;
                versionQuery.Text = latest ? selected?.Id ?? "" : query;
                versionLoader.SelectedItem = selectedLoader;
                versionList.SelectedItem = selected;
                if (selected != null) versionList.ScrollIntoView(selected);
                string result;
                if (latest && (usingCachedVersions || selected == null))
                    result = "暂时无法在线确认最新正式版，请从版本列表选择。";
                else if (versionList.Items.Count == 0)
                    result = "版本列表中没有匹配项，请调整版本号后重试。";
                else if (install)
                    result = "请选择要安装的 Minecraft 版本，然后点“开始安装”。";
                else
                    result = "已打开游戏版本列表，共有 " + versionList.Items.Count + " 个匹配项。";
                Status(result);
                return result;
            }

            string title = selected.Id + " · " + selectedLoader;
            var pending = downloads.Jobs.FirstOrDefault(job => job.Kind == "游戏" && job.IsRunning);
            if (pending != null)
                return pending.Title == title ? title + " 已在下载任务中。" :
                    "正在安装 " + pending.Title + "，完成后可以安装 " + title + "。";

            string profileLoader = selectedLoader == "原版" ? "vanilla" : selectedLoader;
            bool InstalledAtTarget(GameProfile profile)
            {
                if (!string.IsNullOrEmpty(settings.StorageError)) return false;
                var location = MinecraftInstanceLocator.Resolve(profile, settings);
                string root = Path.GetFullPath(settings.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return location.MetadataFound && string.Equals(root,
                    location.GameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            }
            var installed = settings.Profiles.FirstOrDefault(profile =>
                profile != null && string.Equals(string.IsNullOrWhiteSpace(profile.BaseVersion) ? profile.LaunchVersion : profile.BaseVersion,
                    selected.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(string.IsNullOrWhiteSpace(profile.Loader) ? "vanilla" : profile.Loader,
                    profileLoader, StringComparison.OrdinalIgnoreCase) && InstalledAtTarget(profile));
            if (installed != null) return "已有本地实例：" + (installed.Name ?? title) + "。可从启动页选择。";
            token.ThrowIfCancellationRequested();
            QueueGameInstall(selected.Id, selectedLoader, qwenInstallGameFixture);
            return "已加入下载任务：" + title + "。可在“下载任务”查看进度或取消。";
        }
    }
}
