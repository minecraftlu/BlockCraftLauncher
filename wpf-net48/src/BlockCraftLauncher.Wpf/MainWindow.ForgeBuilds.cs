using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        ComboBox forgeBuildSelector;
        FrameworkElement forgeBuildPanel;
        TextBlock forgeBuildStatus;
        Button forgeBuildRetry;
        CancellationTokenSource forgeBuildRequest;
        string listedForgeGame;
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> listForgeBuildsFixture;
        Func<string, string, string, IProgress<string>, IProgress<DownloadBytes>, CancellationToken, Task<GameProfile>> installGameBuildFixture;

        bool ForgeSelected => (string)versionLoader?.SelectedItem == "Forge";
        bool ForgeBuildReady => !ForgeSelected || (versionList?.SelectedItem is MinecraftVersionEntry game &&
            listedForgeGame == game.Id && forgeBuildSelector.SelectedIndex >= 0);

        FrameworkElement BuildForgeSelector()
        {
            forgeBuildSelector = new ComboBox();
            ApplySearchInputStyle(forgeBuildSelector, "选择此 Minecraft 版本的 Forge 构建");
            AutomationProperties.SetName(forgeBuildSelector, "Forge 版本");
            forgeBuildStatus = Txt("", 12, false, true);
            AutomationProperties.SetLiveSetting(forgeBuildStatus, AutomationLiveSetting.Polite);
            forgeBuildRetry = Btn("重新获取 Forge 版本", async () => await RefreshForgeBuildsAsync());
            forgeBuildPanel = Col(SearchFields(("Forge 版本", forgeBuildSelector)), forgeBuildStatus, forgeBuildRetry);
            forgeBuildPanel.Visibility = Visibility.Collapsed;
            versionLoader.SelectionChanged += async (sender, args) => await RefreshForgeBuildsAsync();
            versionList.SelectionChanged += async (sender, args) => await RefreshForgeBuildsAsync();
            return forgeBuildPanel;
        }

        async Task RefreshForgeBuildsAsync()
        {
            CancelAndDetach(ref forgeBuildRequest);
            listedForgeGame = null;
            forgeBuildSelector.Items.Clear();
            forgeBuildSelector.IsEnabled = false;
            forgeBuildRetry.Visibility = Visibility.Collapsed;
            forgeBuildPanel.Visibility = ForgeSelected ? Visibility.Visible : Visibility.Collapsed;
            RefreshVersionActions();
            if (!ForgeSelected || !(versionList.SelectedItem is MinecraftVersionEntry game))
            {
                forgeBuildStatus.Text = "请先选择 Minecraft 版本。";
                return;
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(updateCancellation.Token);
            forgeBuildRequest = operation;
            forgeBuildStatus.Text = "正在获取 Minecraft " + game.Id + " 的 Forge 版本…";
            try
            {
                var list = listForgeBuildsFixture ?? modLoaders.ListForgeVersionsAsync;
                var builds = await list(game.Id, operation.Token);
                if (operation.IsCancellationRequested || forgeBuildRequest != operation) return;
                if (builds.Count == 0)
                {
                    forgeBuildStatus.Text = "Minecraft " + game.Id + " 没有可安装的 Forge 版本。";
                    forgeBuildRetry.Visibility = Visibility.Visible;
                    return;
                }
                forgeBuildSelector.Items.Add("自动选择");
                foreach (var build in builds) forgeBuildSelector.Items.Add(build);
                listedForgeGame = game.Id;
                forgeBuildSelector.SelectedIndex = 0;
                forgeBuildSelector.IsEnabled = true;
                forgeBuildStatus.Text = "找到 " + builds.Count + " 个构建，可选择整合包或模组要求的版本。";
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (forgeBuildRequest != operation || operation.IsCancellationRequested) return;
                forgeBuildStatus.Text = "无法获取 Minecraft " + game.Id + " 的 Forge 版本，请检查下载线路后重试。";
                forgeBuildRetry.Visibility = Visibility.Visible;
                Log(ex.ToString());
            }
            finally
            {
                if (forgeBuildRequest == operation)
                {
                    forgeBuildRequest = null;
                    RefreshVersionActions();
                }
            }
        }
    }
}
