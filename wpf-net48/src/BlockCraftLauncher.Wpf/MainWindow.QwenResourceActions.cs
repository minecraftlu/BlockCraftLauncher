using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        static string CatalogInstallKey(ResourceHit hit, GameProfile target) =>
            string.Join("\n", hit.Kind, hit.ProjectId, target?.Directory?.ToUpperInvariant(),
                target?.BaseVersion ?? hit.TargetGameVersion, (target?.Loader ?? hit.TargetLoader)?.ToLowerInvariant());

        static ResourceHit MatchQwenCatalogProject(string query, IEnumerable<ResourceHit> hits, bool more, string kind)
        {
            string slug = string.Join("-", query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            bool Identifier(ResourceHit hit) => Same(hit.Slug, slug) || Same(hit.ProjectId, query);
            var matches = hits.Where(hit => hit.Kind == kind && (Identifier(hit) || Same(hit.Name, query)))
                .Take(2).ToArray();
            if (matches.Length != 1) return null;
            return !more || Identifier(matches[0]) ? matches[0] : null;
        }

        async Task<string> RunQwenResourceActionAsync(LauncherSearchCommand command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string type = command.ResourceType;
            bool pack = type == "modpack";
            if (!pack && type != "shader" && type != "resourcepack") return "请选择资源包、光影包或整合包。";
            if (!string.IsNullOrEmpty(command.SourceName) && command.SourceName != "Modrinth")
                return "资源包、光影和整合包使用 Modrinth 目录，请指定 Modrinth 或省略来源。";
            string query = command.Query?.Trim() ?? "";
            if (query.Length > 150 || command.Install && query.Length == 0) return "请输入 1 至 150 字的项目名称。";
            if (type == "shader" && query.Equals("BSL", StringComparison.OrdinalIgnoreCase)) query = "bsl-shaders";
            if (pack && command.Install && !string.IsNullOrEmpty(command.InstanceName))
                return "整合包会创建独立实例，请直接指定整合包名称、游戏版本或加载器。";

            bool needsTarget = !pack && command.Install || !string.IsNullOrEmpty(command.InstanceName);
            var active = activeProfile;
            var target = needsTarget ? ResolveQwenModTarget(command.InstanceName, command.GameVersion, command.Loader) : null;
            if (needsTarget && (target == null || string.IsNullOrWhiteSpace(target.BaseVersion)))
                return "没有找到匹配的目标实例，请检查实例名称、游戏版本和加载器。";
            if (!pack && !needsTarget && !string.IsNullOrEmpty(command.Loader))
                return "资源包和光影目录按 Minecraft 版本筛选，请指定游戏版本或目标实例。";
            string version = target?.BaseVersion ?? command.GameVersion;
            string directory = target?.Directory;
            string targetLoader = target?.Loader;
            string page = pack ? "整合包" : type == "shader" ? "光影引擎" : "资源包";
            NavigateToQwenActionPage(page);
            var field = pack ? packSearchQuery : resourceQuery;
            var versionBox = pack ? packVersionFilter : resourceVersion;
            var list = pack ? packSearchResults : resourceList;
            var sort = pack ? packSortMode : resourceSort;
            var status = pack ? packSearchStatus : resourceCounter;
            field.Text = query;
            versionBox.Text = string.IsNullOrEmpty(version) ? "全部版本" : version;
            if (!pack && target != null) resourceInstallTarget.SelectedItem = target;
            if (pack)
            {
                packPage = 0;
                string loader = string.IsNullOrEmpty(command.Loader) ? targetLoader : command.Loader;
                if (!string.IsNullOrEmpty(loader))
                {
                    string native = ModDownloadTarget.Loader(loader);
                    if (native == null && !loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
                        return "整合包支持筛选 Fabric、Forge、NeoForge 或 Quilt 加载器。";
                    int index = native == null ? 0 : packLoaderFilter.Items.IndexOf(native);
                    packLoaderFilter.SelectedIndex = index;
                }
            }

            string visibleVersion = versionBox.Text;
            int loaderIndex = pack ? packLoaderFilter.SelectedIndex : 0;
            using (var request = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                bool loaded = pack ? await SearchModpacksAsync(request.Token) : await SearchResourcesAsync(0, request.Token);
                token.ThrowIfCancellationRequested();
                // A new page, manual search or changed selection takes precedence over this instruction.
                if (currentPage != page || !ReferenceEquals(field, pack ? packSearchQuery : resourceQuery) ||
                    field.Text != query || versionBox.Text != visibleVersion || sort.SelectedIndex != 0 ||
                    (pack ? packPage != 0 || packLoaderFilter.SelectedIndex != loaderIndex
                          : resourcePage != 0 || resourceMatchVersion.IsChecked == true ||
                              target != null && !ReferenceEquals(resourceInstallTarget.SelectedItem, target)) || list.SelectedItem != null) return "";
                if (!loaded) return status.Text;
                var hits = pack ? (packSearchResults.ItemsSource as IEnumerable<ResourceHit>) : resourceResults;
                var found = (hits ?? Enumerable.Empty<ResourceHit>()).ToList();
                string Tell(string message) { status.Text = message; return message; }
                if (found.Count == 0) return Tell("没有找到匹配的项目，可修改名称或 Minecraft 版本再搜索。");
                if (!command.Install) return Tell("已找到 " + found.Count + " 个候选项目，可从列表选择查看。");
                var project = MatchQwenCatalogProject(query, found, pack ? packNextButton.IsEnabled : resourceHasMore, type);
                if (project == null) return Tell("项目名称未能唯一匹配，请从搜索结果选择要安装的项目。");
                list.SelectedItem = project;
                if (target != null && (!settings.Profiles.Contains(target) || target.Directory != directory ||
                    target.BaseVersion != version || target.Loader != targetLoader ||
                    (string.IsNullOrEmpty(command.InstanceName) && !ReferenceEquals(activeProfile, active))))
                    return Tell("目标实例已更改，请重新发送安装指令。");
                string title = pack ? project.Name : project.Name + " → " + target.Name;
                string key = CatalogInstallKey(project, target);
                if (downloads.Jobs.Any(job => job.CatalogKey == key && job.IsRunning))
                    return Tell("下载队列中已有「" + title + "」。");
                if (pack) QueueMrpackInstall(project.Name, null, project);
                else await InstallResourceAsync(project, target);
                return Tell("已加入下载队列：" + title + (pack ? "。安装完成后将创建独立实例。" : "。"));
            }
        }
    }
}
