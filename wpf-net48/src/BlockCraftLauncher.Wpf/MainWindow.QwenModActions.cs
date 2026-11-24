using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        CancellationTokenSource qwenModActionCancellation;

        GameProfile ResolveQwenModTarget(string name, string version, string loader)
        {
            bool Matches(GameProfile p) =>
                (string.IsNullOrEmpty(version) || string.Equals(p.BaseVersion, version, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(loader) || string.Equals(p.Loader, loader, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(name))
            {
                var named = settings.Profiles.Where(p =>
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                return named.Count == 1 && Matches(named[0]) ? named[0] : null;
            }
            if (activeProfile != null)
                return settings.Profiles.Contains(activeProfile) && Matches(activeProfile) ? activeProfile : null;
            var candidates = settings.Profiles.Where(Matches).Take(2).ToList();
            return candidates.Count == 1 ? candidates[0] : null;
        }

        static ResourceHit MatchQwenMod(string query, IEnumerable<ResourceHit> hits, bool more)
        {
            if (ModSearchNames.TryGetValue(query, out var alias)) query = alias;
            bool Same(string value) => string.Equals(value, query, StringComparison.OrdinalIgnoreCase);
            var slug = string.Join("-", query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            bool MatchesSlug(string value) => string.Equals(value, slug, StringComparison.OrdinalIgnoreCase);
            var matches = hits.Where(h => h.Kind == "mod" && (Same(h.Name) || MatchesSlug(h.Slug) || Same(h.ProjectId)))
                .Take(2).ToList();
            if (matches.Count != 1) return null;
            var hit = matches[0];
            // Titles can repeat beyond the first page. Slugs and project IDs are unique within a catalog.
            return !more || MatchesSlug(hit.Slug) || Same(hit.ProjectId) ? hit : null;
        }

        static ModDownloadFile MatchQwenRelease(IReadOnlyList<ModDownloadFile> rows)
        {
            var stable = rows.Where(file => file.Release == "正式版" &&
                !string.IsNullOrWhiteSpace(file.VersionId)).ToList();
            if (stable.Count < 2) return stable.FirstOrDefault();
            DateTimeOffset latest = DateTimeOffset.MinValue;
            ModDownloadFile choice = null;
            foreach (var file in stable)
            {
                if (!DateTimeOffset.TryParse(file.PublishedTimestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var time)) return null;
                if (time > latest) { latest = time; choice = file; }
                else if (time == latest) choice = null;
            }
            return choice;
        }

        async Task<ModDownloadFile> FindQwenReleaseAsync(ResourceHit project, GameProfile target, CancellationToken token)
        {
            var versions = new List<ModDownloadFile>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            // A CurseForge file page contains up to 50 files. Leave unusually long histories to the normal chooser.
            for (int page = 0; page < 10; page++)
            {
                token.ThrowIfCancellationRequested();
                var result = await LoadModFilesPageAsync(project, target, page, token);
                token.ThrowIfCancellationRequested();
                foreach (var file in result.Files)
                    if (!string.IsNullOrEmpty(file.VersionId) && ids.Add(file.VersionId)) versions.Add(file);
                if (!result.HasMore) return MatchQwenRelease(versions);
            }
            return null;
        }

        async Task<string> RunQwenModActionAsync(string query, bool install, string version = null,
            string loader = null, string targetName = null, string sourceName = null)
        {
            query = query?.Trim();
            if (string.IsNullOrEmpty(query) || query.Length > 150) return "请填写 1 至 150 字的模组名称。";
            ModSource? requestedSource = null;
            if (!string.IsNullOrEmpty(sourceName))
            {
                if (sourceName == "CurseForge") requestedSource = ModSource.CurseForge;
                else if (sourceName == "Modrinth") requestedSource = ModSource.Modrinth;
                else return "请选择 CurseForge 或 Modrinth。";
                preferredModSource = requestedSource;
            }
            NavigateToQwenActionPage("资源中心");
            if (requestedSource.HasValue) resourceSource.SelectedIndex = (int)requestedSource.Value;
            CancelAndDetach(ref qwenModActionCancellation);
            var source = SelectedModSource;
            var target = ResolveQwenModTarget(targetName, version, loader);
            string directory = target?.Directory, baseVersion = target?.BaseVersion, targetLoader = target?.Loader;
            modTarget.SelectedItem = target;
            resourceVersion.Text = string.IsNullOrEmpty(version) ? "全部版本" : version;
            resourceLoader.SelectedIndex = string.IsNullOrEmpty(loader) ? 0 :
                Math.Max(0, resourceLoader.Items.IndexOf(ModDownloadTarget.Loader(loader)));
            resourceCategory.SelectedIndex = resourceSort.SelectedIndex = 0;
            resourceMatchVersion.IsChecked = target != null && (install || !string.IsNullOrEmpty(targetName));
            resourceList.SelectedIndex = -1;
            resourceQuery.Text = query;
            var field = resourceQuery;
            var targetBox = modTarget;
            var sourceBox = resourceSource;
            var list = resourceList;
            var matchBox = resourceMatchVersion;
            var versionBox = resourceVersion;
            var loaderBox = resourceLoader;
            var sortBox = resourceSort;
            var categoryBox = resourceCategory;
            using (var request = new CancellationTokenSource())
            {
                qwenModActionCancellation = request;
                bool Current() => !request.IsCancellationRequested &&
                    ReferenceEquals(qwenModActionCancellation, request) &&
                    currentPage == "资源中心" && ReferenceEquals(field, resourceQuery);
                string Tell(string message)
                {
                    if (!Current()) return "";
                    modSourceStatus.Text = message;
                    modSourceStatus.Visibility = Visibility.Visible;
                    return message;
                }
                string ChooseFile(ResourceHit hit, string message)
                {
                    string notice = Tell(message);
                    if (Current()) list.SelectedItem = hit;
                    return notice;
                }
                void Interrupt(object sender, RoutedEventArgs args) => request.Cancel();
                void CategoryChanged(object sender, SelectionChangedEventArgs args)
                {
                    // Replacing the initial "all" option when remote categories arrive does not change the filter.
                    if (categoryBox.SelectedIndex > 0) request.Cancel();
                }
                TextChangedEventHandler changed = Interrupt;
                SelectionChangedEventHandler selected = Interrupt;
                field.TextChanged += changed;
                targetBox.SelectionChanged += selected;
                sourceBox.SelectionChanged += selected;
                list.SelectionChanged += selected;
                loaderBox.SelectionChanged += selected;
                sortBox.SelectionChanged += selected;
                categoryBox.SelectionChanged += CategoryChanged;
                versionBox.AddHandler(TextBoxBase.TextChangedEvent, changed);
                matchBox.Checked += Interrupt;
                matchBox.Unchecked += Interrupt;
                try
                {
                    if (source == ModSource.CurseForge && !CanSearchResources())
                        await modDiscovery;
                    if (!Current()) return "";
                    if (!CanSearchResources()) return Tell("当前无法连接 CurseForge 官方服务，可重新检测或切换来源再搜索。");
                    if (!await SearchResourcesAsync(0, request.Token) || !Current())
                        return Current() ? Tell(resourceCounter.Text) : "";
                    if (resourceResults.Count == 0) return Tell("没有找到匹配的模组，可更换关键词或来源后重试。");
                    if (!install) return Tell("已搜索「" + query + "」，可在结果中查看模组和兼容文件。");
                    if (target == null) return Tell("请核对实例名称、游戏版本和加载器，在「目标实例」选好安装位置后选择文件。");
                    if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(baseVersion) ||
                        ModDownloadTarget.Loader(targetLoader) == null)
                        return Tell("此实例尚未配置可安装模组的加载器，请在「目标实例」选择 Fabric、Forge、NeoForge 或 Quilt 实例。");
                    var project = MatchQwenMod(query, resourceResults, resourceHasMore);
                    if (project == null) return Tell("请从搜索结果选择要安装的模组，再选择兼容文件。");
                    if (project is CurseForgeHit restricted && !restricted.DistributionAllowed)
                        return ChooseFile(project, "作者已关闭第三方分发，可在项目详情查看官方主页。");
                    if (source == ModSource.GitHub)
                        return ChooseFile(project, "已找到项目，请在详情中核对仓库后选择安装。");
                    Tell("已找到「" + project.Name + "」，正在查找 " + target.Name + " 的兼容正式版…");
                    using var lookup = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                    lookup.CancelAfter(TimeSpan.FromSeconds(40));
                    var file = await FindQwenReleaseAsync(project, target, lookup.Token);
                    if (!Current()) return "";
                    if (!settings.Profiles.Contains(target) || target.Directory != directory ||
                        target.BaseVersion != baseVersion || target.Loader != targetLoader)
                        return Tell("目标实例配置已变更，请重新搜索后安装。");
                    if (file == null)
                        return ChooseFile(project, "未能确定兼容的最新正式版，请在文件列表中选择版本。");
                    string title = project.Name + " → " + target.Name;
                    if (downloads.Jobs.Any(job => job.IsRunning && job.Title == title && job.Kind == project.SourceLabel))
                        return Tell("下载任务中已有「" + title + "」。");
                    var previousJob = downloads.Jobs.FirstOrDefault();
                    await InstallResourceAsync(project, target, file.VersionId);
                    var queued = downloads.Jobs.FirstOrDefault();
                    if (queued == null || ReferenceEquals(queued, previousJob) ||
                        queued.Title != title || queued.Kind != project.SourceLabel)
                        return ChooseFile(project, "此文件未能加入下载任务，请在详情中选择兼容文件。");
                    return Tell("已加入下载任务：" + title + "（" + file.FileName + "）。");
                }
                catch (OperationCanceledException)
                {
                    return Current() ? Tell("查询兼容文件超时，请重试。") : "";
                }
                catch (Exception error)
                {
                    return Tell("未能完成模组查询：" + error.Message);
                }
                finally
                {
                    field.TextChanged -= changed;
                    targetBox.SelectionChanged -= selected;
                    sourceBox.SelectionChanged -= selected;
                    list.SelectionChanged -= selected;
                    loaderBox.SelectionChanged -= selected;
                    sortBox.SelectionChanged -= selected;
                    categoryBox.SelectionChanged -= CategoryChanged;
                    versionBox.RemoveHandler(TextBoxBase.TextChangedEvent, changed);
                    matchBox.Checked -= Interrupt;
                    matchBox.Unchecked -= Interrupt;
                    if (ReferenceEquals(qwenModActionCancellation, request)) qwenModActionCancellation = null;
                }
            }
        }
    }
}
