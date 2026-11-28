using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class NetworkDiagnostics
    {
        internal const string GitHubReleases =
            "https://api.github.com/repos/minecraftlu/BlockCraftLauncher/releases?per_page=1";

        internal static async Task<string[]> RunAsync(LauncherConfig config, CancellationToken token)
        {
            using var downloads = new HttpClient(NetworkDownloader.CreateHttpHandler(config));
            using var account = new HttpClient(NetworkDownloader.CreateHttpHandler(new LauncherConfig()));
            downloads.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher-WPF/0.9.34");
            var endpoints = new List<(string Label, string Url, bool NeedsAccount)>
            {
                ("Mojang 游戏版本", MinecraftVersionCatalog.Official, false),
                ("Fabric 版本", "https://meta.fabricmc.net/v2/versions/loader", false),
                ("Quilt 版本", "https://meta.quiltmc.org/v3/versions/loader", false),
                ("Forge 版本", "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", false),
                ("NeoForge 版本", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", false),
                ("Modrinth 搜索", "https://api.modrinth.com/v2/search?limit=1", false),
                ("GitHub 更新", GitHubReleases, false),
                ("微软登录页", "https://login.live.com/", true),
                ("Minecraft 正版服务", "https://api.minecraftservices.com/minecraft/profile", true)
            };
            if (NetworkDownloader.NormalizeMode(config.NetworkMode) != "official")
                endpoints.Insert(1, ("BMCLAPI 游戏版本",
                    NetworkDownloader.MirrorMojangUri(MinecraftVersionCatalog.Official), false));
            if (config.McimEnabled)
                endpoints.Insert(endpoints.Count - 3, ("MCIM / CurseForge",
                    "https://mod.mcimirror.top/curseforge/v1/mods/search?gameId=432&pageSize=1", false));
            return await Task.WhenAll(endpoints.Select(item => ProbeAsync(
                item.NeedsAccount ? account : downloads, item.Label, item.Url,
                item.NeedsAccount, token))).ConfigureAwait(false);
        }

        internal static async Task WriteLiveReportAsync(string path, CancellationToken token)
        {
            var checks = await RunAsync(new LauncherConfig { McimEnabled = true }, token).ConfigureAwait(false);
            var transferChecks = await Task.WhenAll(new[] { "international", "china" }
                .Select(mode => CheckRealGameMetadataAsync(mode, token))).ConfigureAwait(false);
            File.WriteAllLines(path, checks.Concat(transferChecks));
        }

        static async Task<string> CheckRealGameMetadataAsync(string mode, CancellationToken token)
        {
            string label = mode == "china" ? "镜像优先策略" : "官方优先策略";
            string folder = Path.Combine(Path.GetTempPath(), "blockcraft-live-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var client = new NetworkDownloader(new LauncherConfig
                    { NetworkMode = mode, DownloadRetries = 1 });
                var file = await client.GetMetadataWithFallbackAsync(
                    MinecraftVersionCatalog.Official, mode, body =>
                    {
                        var root = new JavaScriptSerializer { MaxJsonLength = 12 * 1024 * 1024 }
                            .DeserializeObject(body) as Dictionary<string, object>;
                        if (root == null || !root.TryGetValue("versions", out var values) ||
                            !(values is object[] versions))
                            throw new InvalidDataException("游戏清单格式无效");
                        foreach (var row in versions.OfType<Dictionary<string, object>>())
                        {
                            if (!row.TryGetValue("id", out var id) || Convert.ToString(id) != "1.21.1")
                                continue;
                            if (row.TryGetValue("url", out var url) && url is string source &&
                                NetworkDownloader.MirrorMojangUri(source) != null &&
                                row.TryGetValue("sha1", out var hash) && hash is string sha &&
                                NetworkDownloader.ValidSha1(sha))
                                return (Source: source, Sha1: sha);
                            break;
                        }
                        throw new InvalidDataException("清单缺少已知游戏版本的校验信息");
                    }, token).ConfigureAwait(false);
                string destination = Path.Combine(folder, "1.21.1.json");
                await client.DownloadVerifiedAsync(file.Source, destination, file.Sha1, token).ConfigureAwait(false);
                return label + "  ·  Minecraft 1.21.1 版本元数据 SHA-1 校验通过";
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is InvalidDataException)
            {
                token.ThrowIfCancellationRequested();
                return label + "  ·  " + ex.Message;
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        static async Task<string> ProbeAsync(HttpClient client, string name, string url,
            bool accountEndpoint, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await NetworkDownloader.SendWithHeaderTimeoutAsync(
                    client, request, NetworkDownloader.HeaderTimeout, token).ConfigureAwait(false);
                int status = (int)response.StatusCode;
                string result = response.IsSuccessStatusCode ? "可连接" :
                    accountEndpoint && response.StatusCode == HttpStatusCode.Unauthorized ? "可连接，等待账号验证" :
                    "HTTP " + status;
                return name + "  ·  " + result + "  ·  " + watch.ElapsedMilliseconds + " ms";
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException)
            {
                token.ThrowIfCancellationRequested();
                return name + "  ·  " + (ex is IOException ? "连接超时" : "连接失败");
            }
        }
    }
}
