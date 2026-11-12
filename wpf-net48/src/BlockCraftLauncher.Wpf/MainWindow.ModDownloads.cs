using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;
using Microsoft.Win32;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        enum ModSource { Modrinth, CurseForge, GitHub, Mcim }

        static readonly string[] ModrinthCategories =
        {
            "全部分类", "性能优化", "科技", "冒险", "装饰", "实用工具", "世界生成",
            "生物", "支持库", "玩法", "装备", "存储", "管理"
        };
        static readonly string[] ModrinthCategoryIds =
        {
            null, "optimization", "technology", "adventure", "decoration", "utility",
            "worldgen", "mobs", "library", "gameplay", "equipment", "storage", "management"
        };
        static readonly Dictionary<string, string> ModSearchNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["钠"] = "Sodium",
            ["锂"] = "Lithium",
            ["机械动力"] = "Create",
            ["暮色森林"] = "The Twilight Forest",
            ["玉"] = "Jade",
            ["模组菜单"] = "Mod Menu",
            ["应用能源2"] = "Applied Energistics 2",
            ["传送石碑"] = "Waystones",
            ["精妙背包"] = "Sophisticated Backpacks",
            ["旅行地图"] = "JourneyMap",
            ["物品管理器"] = "Just Enough Items"
        };

        readonly Dictionary<ModSource, IReadOnlyList<CurseForgeCategory>> modCategories =
            new Dictionary<ModSource, IReadOnlyList<CurseForgeCategory>>();
        (ModSource Source, string Query, string Version, string Loader, int Sort, int Category)? resourceSearchFilters;
        ModSource? preferredModSource;
        bool resourceHasMore;
        ComboBox modTarget;
        TextBlock modSourceStatus;
        ContentControl modDetailsHost;
        CancellationTokenSource modCategoryCancellation, modFileCancellation;

        ModSource SelectedModSource => resourceType == "mod" && resourceSource?.SelectedIndex >= 0
            ? (ModSource)resourceSource.SelectedIndex : ModSource.Modrinth;

        static bool IsCurseForgeSource(ModSource source) =>
            source == ModSource.CurseForge || source == ModSource.Mcim;

        static string ModSourceName(ModSource source)
        {
            switch (source)
            {
                case ModSource.CurseForge: return "CurseForge 官方";
                case ModSource.Mcim: return "CurseForge · MCIM";
                case ModSource.GitHub: return "GitHub 开源";
                default: return "Modrinth";
            }
        }

        bool CanSearchResources() => SelectedModSource != ModSource.CurseForge ||
            CurseForgeMirrorClient.HasLocalOfficialKey;

        UIElement BuildModDownloadPage(string title, WrapPanel actions)
        {
            var sources = settings.McimEnabled
                ? new[] { ModSource.Modrinth, ModSource.CurseForge, ModSource.GitHub, ModSource.Mcim }
                : new[] { ModSource.Modrinth, ModSource.CurseForge, ModSource.GitHub };
            var source = preferredModSource ?? (CurseForgeMirrorClient.HasLocalOfficialKey
                ? ModSource.CurseForge : ModSource.Modrinth);
            resourceSource = new ComboBox
            {
                ItemsSource = sources.Select(ModSourceName).ToArray(),
                SelectedIndex = sources.Contains(source) ? (int)source : 0
            };
            resourceCategory = new ComboBox();
            resourceLoader = new ComboBox
            {
                ItemsSource = new[] { "全部加载器", "Fabric", "Forge", "NeoForge", "Quilt" },
                SelectedIndex = 0
            };
            modTarget = new ComboBox
            {
                ItemsSource = settings.Profiles,
                SelectedItem = activeProfile ?? settings.Profiles.FirstOrDefault()
            };
            ApplySearchInputStyle(resourceSource, "选择下载源");
            ApplySearchInputStyle(resourceCategory, "筛选模组分类");
            ApplySearchInputStyle(resourceLoader, "选择模组加载器");
            ApplySearchInputStyle(modTarget, "选择要安装模组的实例");
            modSourceStatus = Txt("", 12, false, true);

            modDetailsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            resourceList.SelectionChanged += (sender, args) => RefreshModDetails();
            resourceSource.SelectionChanged += (sender, args) => RefreshModSource();
            resourceMatchVersion.Checked += (sender, args) => RefreshModFilters();
            resourceMatchVersion.Unchecked += (sender, args) => RefreshModFilters();
            modTarget.SelectionChanged += (sender, args) =>
            {
                RefreshModFilters();
                RefreshModDetails();
            };
            void Open() => modDetailsHost.BringIntoView();
            var details = Btn("查看详情", Open);
            WireResultActions(resourceList, Open, details);

            var search = SearchCard(Col(Txt(title, 17, true),
                SearchFields(("名称", resourceQuery), ("来源", resourceSource),
                    ("版本", resourceVersion), ("加载器", resourceLoader),
                    ("分类", resourceCategory), ("排序", resourceSort), ("安装到", modTarget)),
                resourceMatchVersion, modSourceStatus, actions));
            var results = ProjectResults(resourceCounter, resourceList, Row(details),
                resourcePrevButton, resourceNextButton);
            var content = ArrangeModResults(results, modDetailsHost);
            RefreshModSource();
            RefreshModDetails();
            return Col(search, content);
        }

        static Grid ArrangeModResults(Border results, ContentControl details)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(details, 1);
            grid.Children.Add(results);
            grid.Children.Add(details);
            grid.SizeChanged += (sender, args) =>
            {
                if (!args.WidthChanged) return;
                bool sideBySide = args.NewSize.Width >= 860;
                grid.ColumnDefinitions[0].Width = new GridLength(sideBySide ? 53 : 1, GridUnitType.Star);
                grid.ColumnDefinitions[1].Width = sideBySide
                    ? new GridLength(47, GridUnitType.Star) : new GridLength(0);
                Grid.SetColumn(details, sideBySide ? 1 : 0);
                Grid.SetRow(details, sideBySide ? 0 : 1);
                details.Margin = sideBySide ? new Thickness(12, 0, 0, 0) : new Thickness(0);
            };
            return grid;
        }

        void RefreshModSource()
        {
            var source = SelectedModSource;
            preferredModSource = source;
            CancelAndDetach(ref modCategoryCancellation);
            ResetResourceResults();
            if (IsCurseForgeSource(source))
            {
                modCategories.TryGetValue(source, out var categories);
                ShowModCategories(categories);
                if (categories == null && CanSearchResources() && IsLoaded)
                    _ = LoadModCategoriesAsync(source);
            }
            else
            {
                resourceCategory.ItemsSource = ModrinthCategories;
                resourceCategory.SelectedIndex = 0;
            }

            string hint = source == ModSource.GitHub
                ? "GitHub 按关键词搜索。安装时核对目标实例和 Releases 文件。"
                : !CanSearchResources()
                    ? "请配置本机环境变量 BLOCKCRAFT_CURSEFORGE_API_KEY，然后重新打开启动器。"
                    : modTarget.Items.Count == 0 ? "安装模组前，请先创建带模组加载器的游戏实例。" : "";
            modSourceStatus.Text = hint;
            modSourceStatus.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            RefreshModFilters();
        }

        void RefreshModFilters()
        {
            bool supportsFilters = SelectedModSource != ModSource.GitHub;
            resourceMatchVersion.IsEnabled = supportsFilters && modTarget.SelectedItem is GameProfile;
            bool matchTarget = resourceMatchVersion.IsEnabled && resourceMatchVersion.IsChecked == true;
            resourceVersion.IsEnabled = resourceLoader.IsEnabled = supportsFilters && !matchTarget;
            resourceCategory.IsEnabled = resourceSort.IsEnabled = supportsFilters;
        }

        void ShowModCategories(IReadOnlyList<CurseForgeCategory> categories)
        {
            resourceCategory.ItemsSource = new[] { new CurseForgeCategory { Name = "全部分类" } }
                .Concat(categories ?? Array.Empty<CurseForgeCategory>()).ToArray();
            resourceCategory.SelectedIndex = 0;
        }

        async Task LoadModCategoriesAsync(ModSource source)
        {
            using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                modCategoryCancellation = request;
                try
                {
                    IReadOnlyList<CurseForgeCategory> categories;
                    using (var client = new CurseForgeMirrorClient(settings, source == ModSource.Mcim))
                        categories = await client.GetCategoriesAsync(request.Token);
                    if (request.IsCancellationRequested ||
                        !ReferenceEquals(modCategoryCancellation, request)) return;
                    modCategories[source] = categories;
                    ShowModCategories(categories);
                    resourceCategory.ToolTip = "按官方分类搜索模组";
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(modCategoryCancellation, request))
                        resourceCategory.ToolTip = "分类加载失败：" + ex.Message + "。切换下载源后可重试。";
                }
                finally
                {
                    if (ReferenceEquals(modCategoryCancellation, request)) modCategoryCancellation = null;
                }
            }
        }

        void ResetResourceResults()
        {
            CancelAndDetach(ref resourceSearchCancellation);
            resourcePage = 0;
            resourceHasMore = false;
            resourceSearchFilters = null;
            resourceResults.Clear();
            resourceCounter.Text = "输入关键词或直接搜索。";
            UpdateResourcePaging();
        }

        void UpdateResourcePaging()
        {
            bool idle = resourceSearchCancellation == null;
            resourceSearchButton.IsEnabled = idle && CanSearchResources();
            resourcePrevButton.IsEnabled = idle && resourcePage > 0;
            resourceNextButton.IsEnabled = idle && resourceHasMore;
        }

        async Task SearchResourcesAsync(int page = 0)
        {
            if (page < 0 || !CanSearchResources()) return;
            bool isMod = resourceType == "mod";
            var source = SelectedModSource;
            var profile = isMod ? modTarget.SelectedItem as GameProfile : activeProfile;
            bool matchTarget = resourceMatchVersion.IsEnabled && resourceMatchVersion.IsChecked == true;
            string typedVersion = resourceVersion.Text.Trim();
            string version = matchTarget ? profile?.BaseVersion :
                typedVersion.Length > 0 && typedVersion != "全部版本" ? typedVersion : null;
            string loader = !isMod ? null : matchTarget ? ModDownloadTarget.Loader(profile?.Loader) :
                resourceLoader.SelectedIndex > 0 ? resourceLoader.SelectedItem as string : null;
            int category = IsCurseForgeSource(source)
                ? (resourceCategory.SelectedItem as CurseForgeCategory)?.Id ?? 0
                : isMod ? Math.Max(0, resourceCategory.SelectedIndex) : 0;
            int sort = Math.Max(0, resourceSort.SelectedIndex);
            string query = resourceQuery.Text.Trim();
            if (isMod && ModSearchNames.TryGetValue(query, out var translated)) query = translated;
            var filters = (source, query, version, loader, sort, category);
            if (!Nullable.Equals(resourceSearchFilters, filters)) page = 0;
            if (IsCurseForgeSource(source) && loader != null && string.IsNullOrWhiteSpace(version))
            {
                resourceCounter.Text = "按加载器筛选 CurseForge 时，请同时选择 Minecraft 版本。";
                return;
            }

            CancelAndDetach(ref resourceSearchCancellation);
            using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
            {
                resourceSearchCancellation = request;
                UpdateResourcePaging();
                string name = ModSourceName(source);
                resourceCounter.Text = "正在搜索 " + name + "…";
                try
                {
                    IReadOnlyList<ResourceHit> hits;
                    bool hasMore;
                    if (IsCurseForgeSource(source))
                    {
                        using (var client = new CurseForgeMirrorClient(settings, source == ModSource.Mcim))
                        {
                            var result = await client.SearchPageAsync(query, version, loader,
                                sort, page, category, request.Token);
                            hits = result.Hits;
                            hasMore = result.HasMore;
                        }
                    }
                    else if (source == ModSource.GitHub)
                    {
                        using (var client = new GitHubOpenSourceModClient(settings))
                        {
                            var result = await client.SearchAsync(query, page, request.Token);
                            hits = result.Hits;
                            hasMore = (page + 1) * 40 < Math.Min(1000, result.TotalHits);
                        }
                    }
                    else
                    {
                        using (var client = new ModrinthClient(settings))
                        {
                            string order = new[] { "relevance", "downloads", "updated", "newest" }[sort];
                            var result = await client.SearchPageAsync(query, resourceType, request.Token,
                                version, loader?.ToLowerInvariant(), order, page * 20, ModrinthCategoryIds[category]);
                            hits = result.Hits;
                            hasMore = (page + 1) * 20 < Math.Min(2000, result.TotalHits);
                        }
                    }

                    if (request.IsCancellationRequested ||
                        !ReferenceEquals(resourceSearchCancellation, request)) return;
                    resourcePage = page;
                    resourceHasMore = hasMore;
                    resourceSearchFilters = filters;
                    resourceResults.Clear();
                    foreach (var hit in hits) resourceResults.Add(hit);
                    resourceCounter.Text = hits.Count == 0 ? "当前页没有匹配项目。" :
                        name + " · 第 " + (page + 1) + " 页 · " + hits.Count + " 项";
                }
                catch (OperationCanceledException)
                {
                    if (ReferenceEquals(resourceSearchCancellation, request))
                        resourceCounter.Text = "搜索超时，请重试。";
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(resourceSearchCancellation, request))
                        resourceCounter.Text = "搜索失败：" + ex.Message;
                }
                finally
                {
                    if (ReferenceEquals(resourceSearchCancellation, request))
                    {
                        resourceSearchCancellation = null;
                        UpdateResourcePaging();
                    }
                }
            }
        }

        void RefreshModDetails()
        {
            CancelAndDetach(ref modFileCancellation);
            if (modDetailsHost == null) return;
            var hit = resourceList.SelectedItem as ResourceHit;
            if (hit == null)
            {
                modDetailsHost.Content = Card(Txt("选择一个模组查看详情和文件版本。", 13, false, true));
                return;
            }

            var profile = modTarget.SelectedItem as GameProfile;
            var meta = string.Join(" · ", new[] { hit.SourceLabel, hit.Author, hit.DownloadsLabel }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var details = Col(Txt(hit.Name, 19, true), Txt(meta, 12, false, true));
            if (!string.IsNullOrWhiteSpace(hit.Description))
                details.Children.Add(Txt(hit.Description, 13));

            modDetailsHost.Content = Card(details);
            if (!(hit is CurseForgeHit project) || project.OfficialUrl != null)
                details.Children.Add(Row(Btn("项目主页", () => OpenResourceProject(hit))));
            if (hit is CurseForgeHit curseForge)
            {
                if (curseForge.Screenshots.Count > 0) details.Children.Add(ModGallery(curseForge));
                if (!curseForge.DistributionAllowed)
                {
                    details.Children.Add(Txt("作者限制第三方分发，当前文件无法在启动器内安装。", 12, false, true));
                    return;
                }
            }

            bool canInstall = profile != null && ModDownloadTarget.Loader(profile.Loader) != null &&
                !string.IsNullOrWhiteSpace(profile.BaseVersion) && !string.IsNullOrWhiteSpace(profile.Directory);
            if (!canInstall)
            {
                details.Children.Add(Txt("安装需选择带模组加载器的实例。", 12, false, true));
                if (hit is CurseForgeHit) BuildModFileChooser(details, hit, null);
                return;
            }
            if (hit.Source == "GitHub")
            {
                details.Children.Add(Txt("从 Releases 查找与 " + profile.BaseVersion + " / " +
                    profile.Loader + " 兼容的文件。", 12, false, true));
                details.Children.Add(Row(Btn("安装到 " + profile.Name,
                    async () => await InstallResourceAsync(hit, profile), true)));
                return;
            }

            BuildModFileChooser(details, hit, profile);
        }

        static Expander ModGallery(CurseForgeHit hit)
        {
            var gallery = new WrapPanel();
            foreach (var shot in hit.Screenshots)
            {
                var image = new Image
                {
                    Width = 150, Height = 96, Stretch = Stretch.UniformToFill,
                    ToolTip = shot.Title, Margin = new Thickness(0, 4, 8, 8)
                };
                image.SetBinding(Image.SourceProperty, new Binding(nameof(CurseForgeScreenshot.ThumbnailUrl))
                {
                    Source = shot, Mode = BindingMode.OneWay
                });
                gallery.Children.Add(image);
            }
            var expander = new Expander
            {
                Header = "项目截图（" + hit.Screenshots.Count + "）",
                Content = gallery, Margin = new Thickness(0, 2, 0, 7)
            };
            expander.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            return expander;
        }

        void BuildModFileChooser(StackPanel details, ResourceHit hit, GameProfile profile)
        {
            var files = new ObservableCollection<ModDownloadFile>();
            var picker = new ListBox
            {
                ItemsSource = files, MinHeight = 82, MaxHeight = 196
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(picker, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(picker, "兼容的模组文件");
            var status = Txt("兼容文件版本", 12, false, true);
            var dependencyStatus = Txt("", 12, false, true);
            int page = 0;
            var curseForge = hit as CurseForgeHit;
            var filter = profile;
            if (curseForge != null && filter == null)
            {
                string version = resourceVersion.Text.Trim();
                filter = new GameProfile
                {
                    BaseVersion = version == "全部版本" ? "" : version,
                    Loader = resourceLoader.SelectedIndex > 0 ? resourceLoader.SelectedItem as string : null
                };
            }

            Button more = null;
            Button install = null;
            if (profile != null)
            {
                install = Btn("安装所选版本", async () =>
                {
                    if (picker.SelectedItem is ModDownloadFile selected)
                        await InstallResourceAsync(hit, profile, selected.VersionId);
                }, true);
                install.IsEnabled = false;
            }
            Button save = null;
            if (curseForge != null)
            {
                save = Btn("另存为 JAR", () =>
                {
                    if (picker.SelectedItem is ModDownloadFile selected)
                        QueueCurseForgeSave(curseForge, selected);
                }, profile == null);
                save.IsEnabled = false;
            }
            picker.SelectionChanged += (sender, args) =>
            {
                var file = picker.SelectedItem as ModDownloadFile;
                if (install != null) install.IsEnabled = file != null;
                if (save != null) save.IsEnabled = file != null;
                dependencyStatus.Text = file == null || profile == null ? file?.FileName ?? "" : file.FileName + " · " +
                    (file.RequiredDependencies == 0 ? "必需前置 0 项" :
                        "自动安装 " + file.RequiredDependencies + " 项直接前置及其必需依赖");
                dependencyStatus.Visibility = file == null ? Visibility.Collapsed : Visibility.Visible;
            };
            dependencyStatus.Visibility = Visibility.Collapsed;

            async Task LoadFilesAsync()
            {
                CancelAndDetach(ref modFileCancellation);
                using (var request = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                {
                    modFileCancellation = request;
                    more.IsEnabled = false;
                    status.Text = "正在读取兼容文件…";
                    try
                    {
                        IReadOnlyList<ModDownloadFile> rows;
                        bool hasMore = false;
                        if (curseForge != null)
                        {
                            using (var client = new CurseForgeMirrorClient(settings, hit.Source == "MCIM"))
                            {
                                var result = await client.ListCompatibleFilesAsync(
                                    curseForge.Id, filter, page, request.Token);
                                rows = result.Files;
                                hasMore = result.HasMore;
                            }
                        }
                        else
                        {
                            using (var client = new ModrinthClient(settings))
                                rows = await client.ListModFilesAsync(hit, profile, request.Token);
                        }
                        if (request.IsCancellationRequested || !ReferenceEquals(modFileCancellation, request)) return;
                        var loadedIds = new HashSet<string>(files.Select(file => file.VersionId), StringComparer.Ordinal);
                        foreach (var row in rows)
                            if (loadedIds.Add(row.VersionId)) files.Add(row);
                        page++;
                        if (picker.SelectedIndex < 0 && files.Count > 0) picker.SelectedIndex = 0;
                        status.Text = files.Count > 0 ? "兼容文件 · 已载入 " + files.Count + " 个" :
                            hasMore ? "本页没有兼容文件，可继续加载。" : "当前 Minecraft 版本和加载器没有兼容文件。";
                        more.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;
                        more.Content = "更多版本";
                    }
                    catch (Exception ex)
                    {
                        if (!ReferenceEquals(modFileCancellation, request)) return;
                        status.Text = ex is OperationCanceledException ? "读取文件版本超时。" :
                            "读取文件版本失败：" + ex.Message;
                        more.Content = "重试";
                        more.Visibility = Visibility.Visible;
                    }
                    finally
                    {
                        if (ReferenceEquals(modFileCancellation, request))
                        {
                            modFileCancellation = null;
                            more.IsEnabled = true;
                        }
                    }
                }
            }

            more = Btn("更多版本", async () => await LoadFilesAsync());
            more.Visibility = Visibility.Collapsed;
            if (profile != null)
                details.Children.Add(Txt(profile.Name + " · " + profile.BaseVersion + " / " + profile.Loader,
                    12, true));
            details.Children.Add(status);
            details.Children.Add(picker);
            details.Children.Add(dependencyStatus);
            var actions = Row();
            if (install != null) actions.Children.Add(install);
            if (save != null) actions.Children.Add(save);
            actions.Children.Add(more);
            details.Children.Add(actions);
            if (IsLoaded) _ = LoadFilesAsync();
        }

        void QueueCurseForgeSave(CurseForgeHit hit, ModDownloadFile file)
        {
            if (!hit.DistributionAllowed || !long.TryParse(file.VersionId, out long fileId) || fileId <= 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "保存模组文件",
                FileName = file.FileName,
                Filter = "Minecraft 模组 (*.jar)|*.jar",
                DefaultExt = ".jar",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (Directory.Exists(settings.DownloadDirectory))
                dialog.InitialDirectory = settings.DownloadDirectory;
            if (dialog.ShowDialog(this) != true) return;

            string path = dialog.FileName;
            bool mirror = hit.Source == "MCIM";
            downloads.Enqueue(hit.SourceLabel, file.FileName, async (token, stage, bytes) =>
            {
                stage.Report("正在下载所选文件…");
                using (var client = new CurseForgeMirrorClient(settings, mirror))
                    await client.SaveFileAsync(hit.Id, fileId, path, bytes, token);
                stage.Report("已保存到 " + path);
                Status("已保存：" + file.FileName);
            });
            Status("已加入下载任务：" + file.FileName);
        }

        void QueueCurseForgeInstall(CurseForgeHit hit, GameProfile profile, string versionId)
        {
            if (!hit.DistributionAllowed || !long.TryParse(versionId, out long fileId) || fileId <= 0)
                return;

            bool mirror = hit.Source == "MCIM";
            downloads.Enqueue(hit.SourceLabel, hit.Name + " → " + profile.Name, async (token, stage, bytes) =>
            {
                using (var client = new CurseForgeMirrorClient(settings, mirror))
                {
                    var installed = await client.InstallAsync(hit.Id, profile, stage, token, fileId, bytes);
                    stage.Report(installed.Count == 0 ? "所选文件和必需前置已安装。" :
                        "安装完成 · 新增 " + installed.Count + " 个文件");
                    Status("已安装：" + hit.Name + " → " + profile.Name);
                }
            });
            Status("已加入下载任务：" + hit.Name + " → " + profile.Name);
        }
    }
}
