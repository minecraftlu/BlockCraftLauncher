using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        internal async Task VerifyForgeBuildSelectionAsync()
        {
            string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "blockcraft-forge-" +
                Guid.NewGuid().ToString("N"))).FullName;
            var isolated = new SettingsStore(Path.Combine(folder, "settings.json"));
            isolated.Save(new LauncherConfig { Root = Path.Combine(folder, "game"), GameRootFollowsDownloads = false });
            var window = new MainWindow(null, null, isolated) {
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0, SuppressAutomaticUpdateCheck = true
            };
            const string chosen = "47.2.0", newer = "47.4.0";
            var oldGame = new MinecraftVersionEntry { Id = "1.19.2", Type = "release" };
            var newGame = new MinecraftVersionEntry { Id = "1.20.1", Type = "release" };
            var delayed = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var install = new TaskCompletionSource<GameProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
            int requests = 0, queued = 0;
            string actualGame = null, actualLoader = null, actualBuild = null;
            window.listForgeBuildsFixture = (game, token) =>
            {
                if (game == oldGame.Id) return delayed.Task;
                if (game != newGame.Id) throw new InvalidDataException("Unexpected Minecraft version " + game);
                requests++;
                if (requests == 1) return Task.FromException<IReadOnlyList<string>>(new IOException("offline fixture"));
                if (requests == 2) return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
                return Task.FromResult<IReadOnlyList<string>>(new[] { newer, chosen });
            };
            window.installGameBuildFixture = (game, loader, build, progress, bytes, token) =>
            {
                queued++;
                actualGame = game;
                actualLoader = loader;
                actualBuild = build;
                return install.Task;
            };
            void Require(bool ok, string message)
            {
                if (!ok) throw new InvalidDataException("Forge 版本选择测试：" + message);
            }
            async Task Ui()
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
            }
            try
            {
                window.gameVersions.Add(oldGame);
                window.gameVersions.Add(newGame);
                window.Show();
                ClickFixtureAction(window.TopDownloadButton);
                window.versionList.SelectedItem = oldGame;
                window.versionLoader.SelectedItem = "Forge";
                await Ui();
                Require(window.forgeBuildPanel.IsVisible && !window.versionInstallButton.IsEnabled,
                    "Forge 列表加载时允许开始安装。");
                window.versionList.SelectedItem = newGame;
                await WaitForQwenAsync(() => requests == 1 && window.forgeBuildRetry.IsVisible, "Forge 请求失败后的重试");
                Require(!window.versionInstallButton.IsEnabled, "失败后仍可安装未获取的版本。");
                ClickFixtureAction(window.forgeBuildRetry);
                await WaitForQwenAsync(() => requests == 2 && window.forgeBuildRetry.IsVisible, "Forge 空目录");
                Require(!window.versionInstallButton.IsEnabled, "空列表允许开始安装。");
                ClickFixtureAction(window.forgeBuildRetry);
                await WaitForQwenAsync(() => window.forgeBuildSelector.Items.Contains(chosen) &&
                    window.versionInstallButton.IsEnabled, "重试成功后选择 Forge 版本");
                window.forgeBuildSelector.SelectedItem = chosen;
                delayed.SetResult(new[] { "43.2.1" });
                await Ui();
                Require(Equals(window.forgeBuildSelector.SelectedItem, chosen) &&
                    !window.forgeBuildSelector.Items.Contains("43.2.1"), "上一游戏版本的延迟响应覆盖了当前选择。");
                ClickFixtureAction(window.versionInstallButton);
                await WaitForQwenAsync(() => queued == 1, "将选定构建交给下载队列");
                Require(actualGame == newGame.Id && actualLoader == "Forge" && actualBuild == chosen &&
                    window.downloads.Jobs.Count == 1 && !window.versionInstallButton.IsEnabled,
                    "安装未使用指定的 Minecraft 和 Forge 版本，或仍可创建重复任务。");
                install.SetResult(new GameProfile { Id = "selected-forge", Name = "测试 Forge", Loader = "forge",
                    BaseVersion = actualGame, LaunchVersion = actualGame + "-forge-" + actualBuild, Directory = window.settings.Root });
                await WaitForQwenAsync(() => !window.downloads.Jobs[0].IsRunning, "Forge 任务结束");
                Require(window.downloads.Jobs[0].Status == "已完成" &&
                    isolated.Load().Profiles.Any(p => p.LaunchVersion == newGame.Id + "-forge-" + chosen),
                    "没有登记已安装的精确构建。");
                window.versionLoader.SelectedItem = "Fabric";
                await Ui();
                Require(!window.forgeBuildPanel.IsVisible && window.versionInstallButton.IsEnabled,
                    "切换到 Fabric 后仍然显示 Forge 版本或无法安装。");
                var first = window.settings.Profiles.Single();
                string metadata = Path.Combine(first.Directory, "versions", first.LaunchVersion, first.LaunchVersion + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(metadata));
                File.WriteAllText(metadata, "{}");
                string sameRoot = await window.RunQwenGameActionAsync(newGame.Id, "Forge", true, CancellationToken.None);
                Require(sameRoot.Contains("已有本地实例") && window.downloads.Jobs.Count == 1,
                    "同一安装位置重复创建了游戏任务。");

                string nextRoot = Path.Combine(folder, "second-game");
                DownloadPathService.ApplyStorage(window.settings, nextRoot, window.settings.DownloadDirectory, false);
                window.qwenInstallGameFixture = (game, loader, progress, bytes, token) =>
                    Task.FromResult(new GameProfile { Id = "second-forge", Name = "新位置 Forge", Loader = "forge",
                        BaseVersion = game, LaunchVersion = first.LaunchVersion, Directory = nextRoot });
                await window.RunQwenGameActionAsync(newGame.Id, "Forge", true, CancellationToken.None);
                await WaitForQwenAsync(() => window.downloads.Jobs.Count == 2 && !window.downloads.Jobs[0].IsRunning,
                    "在新的游戏目录登记相同版本");
                var profiles = isolated.Load().Profiles;
                Require(profiles.Count == 2 && profiles.Any(p => p.Directory == first.Directory) &&
                    profiles.Any(p => p.Directory == nextRoot), "旧目录的相同版本覆盖了新目录的安装记录。");
            }
            finally
            {
                delayed.TrySetCanceled();
                install.TrySetCanceled();
                foreach (var job in window.downloads.Jobs.Where(job => job.IsRunning)) job.Cancel();
                window.Close();
                Directory.Delete(folder, true);
            }
        }
    }
}
