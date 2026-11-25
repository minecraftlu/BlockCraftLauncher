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
        static string QwenFileSha512(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA512.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        async Task<string> VerifyQwenInstalledFileAsync(ManagedResource record, string subfolder, CancellationToken token)
        {
            // Consult the selected release by version ID. Identical ZIPs can be published for different game versions.
            string path = Path.Combine(activeProfile.Directory, subfolder, record.Filename);
            string hash = QwenFileSha512(path);
            using (var network = new NetworkDownloader(settings))
            {
                string json = await network.GetStringWithFallbackAsync(
                    "https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(record.VersionId), "official", token);
                var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                bool Has(string field, string value) => release[field] is System.Collections.IEnumerable items &&
                    items.Cast<object>().Any(item => Equals(item, value));
                var files = ((System.Collections.IEnumerable)release["files"]).Cast<Dictionary<string, object>>();
                if (!Equals(release["id"], record.VersionId) || !Equals(release["project_id"], record.ProjectId) ||
                    !Equals(release["version_type"], "release") || !Has("game_versions", activeProfile.BaseVersion) ||
                    (record.Kind == "mod" && !Has("loaders", "fabric")) || !files.Any(f => Equals(f["filename"], record.Filename) &&
                        f["hashes"] is Dictionary<string, object> hashes && Equals(hashes["sha512"], hash)))
                    throw new InvalidDataException("Incompatible release or different file contents for " + record.Filename);
            }
            return record.Filename + " (" + new FileInfo(path).Length + " bytes, SHA-512 " + hash + ")";
        }

        async Task WaitForQwenJobAsync(DownloadJob job, CancellationToken token)
        {
            while (job.IsRunning) await Task.Delay(100, token);
            if (job.Status != "已完成") throw new IOException(job.Title + ": " + job.Status + " " + job.Error);
        }

        async Task<string> VerifyLiveSemanticInstallAsync(Uri endpoint, Action<string> log, CancellationToken token)
        {
            const string instruction = "帮我装一个提升帧率的模组";
            Task<string> Submit() => RunLocalQwenAsync(endpoint,
                new[] { new QwenChatTurn("user", instruction) }, null, token);

            log("正在用中文用途指令安装一个兼容的模组。");
            string status = await Submit();
            var job = downloads.Jobs.SingleOrDefault();
            if (currentPage != "资源中心" || job?.Kind != "Modrinth")
                throw new InvalidDataException("The local purpose-based install did not enqueue a native Modrinth job. " + status);
            await WaitForQwenJobAsync(job, token);
            // Look up the selected project from the actual job and visible catalog; required dependencies can add extra records.
            var chosen = resourceResults.SingleOrDefault(hit => job.Title == hit.Name + " → " + activeProfile.Name);
            if (chosen == null) throw new InvalidDataException("Download job does not correspond to a returned catalog item. " + job.Title);
            var record = new ManagedModUpdateService(settings).Load(activeProfile).Single(r => r.Kind == "mod" && r.ProjectId == chosen.ProjectId);
            string verified = await VerifyQwenInstalledFileAsync(record, "mods", token);
            string mods = Path.Combine(activeProfile.Directory, "mods");
            string[] Contents() => Directory.GetFiles(mods, "*.jar").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(p => Path.GetFileName(p) + "/" + QwenFileSha512(p)).ToArray();
            var before = Contents();
            log("兼容的目录项目已安装：" + chosen.Name + " / " + verified);
            int count = downloads.Jobs.Count;
            string repeat = await Submit();
            if (downloads.Jobs.Count > count + 1) throw new InvalidDataException("One repeated install created multiple jobs. " + repeat);
            if (downloads.Jobs.Count != count) await WaitForQwenJobAsync(downloads.Jobs[0], token);
            if (!before.SequenceEqual(Contents()) || new ManagedModUpdateService(settings).Load(activeProfile)
                    .Count(r => r.Kind == "mod" && r.ProjectId == chosen.ProjectId) != 1)
                throw new InvalidDataException("The repeated install created duplicate or different mod files. " + repeat);
            return "The explicit Chinese purpose-based command installed " + chosen.Name + " in Fabric " +
                activeProfile.BaseVersion + ": " + verified + ". A repeated request left the same verified JARs.";
        }

        async Task<string> VerifyLivePackCommandsAsync(Func<string, Task> submit, CancellationToken token)
        {
            var evidence = new List<string>();
            foreach (var item in new[] {
                (Label: "资源包", Slug: "faithful-32x", Id: "w0TnApzs", Kind: "resourcepack", Folder: "resourcepacks"),
                (Label: "光影", Slug: "complementary-reimagined", Id: "HVnmMxH1", Kind: "shader", Folder: "shaderpacks")
            })
            {
                int count = downloads.Jobs.Count;
                await submit("搜索" + item.Label + " " + item.Slug);
                if (downloads.Jobs.Count != count || !resourceResults.Any(h => h.Kind == item.Kind && h.ProjectId == item.Id))
                    throw new InvalidDataException("The search did not display " + item.Slug + " without installing.");
                await submit("下载" + item.Label + " " + item.Slug);
                if (downloads.Jobs.Count != count + 1)
                    throw new InvalidDataException("The install did not enqueue one job for " + item.Slug + ". " + StatusText.Text);
                await WaitForQwenJobAsync(downloads.Jobs[0], token);
                var record = new ManagedModUpdateService(settings).Load(activeProfile).Single(r =>
                    r.Kind == item.Kind && r.ProjectId == item.Id);
                evidence.Add(await VerifyQwenInstalledFileAsync(record, item.Folder, token));
            }
            return "Named resource pack and shader installs passed: " + string.Join("; ", evidence) + ".";
        }
    }
}
