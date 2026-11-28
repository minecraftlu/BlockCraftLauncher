using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installer.NeoForge;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Selects the real loader installer. Never registers an instance until
    /// the loader metadata and the Minecraft dependencies are fully installed.
    /// </summary>
    public sealed class ModLoaderInstaller
    {
        readonly LauncherConfig settings;
        public ModLoaderInstaller(LauncherConfig settings) { this.settings = settings; }

        ForgeInstallOptions ForgeOptions(CancellationToken token) => new ForgeInstallOptions
        {
            CancellationToken = token,
            JavaPath = string.IsNullOrWhiteSpace(settings.JavaPath) ? null : settings.JavaPath
        };

        public Task<GameProfile> InstallAsync(string minecraftVersion, string loader,
            IProgress<string> progress, CancellationToken token)
            => InstallAsync(minecraftVersion, loader, null, progress, token);

        public Task<GameProfile> InstallAsync(string minecraftVersion, string loader, string loaderVersion,
            IProgress<string> progress, CancellationToken token)
            => InstallAsync(minecraftVersion, loader, loaderVersion, progress, null, token);

        public async Task<GameProfile> InstallAsync(string minecraftVersion, string loader, string loaderVersion,
            IProgress<string> progress, IProgress<DownloadBytes> bytes, CancellationToken token)
        {
            if (!LoaderNetworkCatalog.ValidVersionId(minecraftVersion))
                throw new ArgumentException("Minecraft 版本号无效");
            if (!string.IsNullOrEmpty(loaderVersion) && !LoaderNetworkCatalog.ValidVersionId(loaderVersion))
                throw new ArgumentException("模组加载器版本号无效");
            if (loader != "原版" && loader != "Fabric" && loader != "Quilt" && loader != "Forge" && loader != "NeoForge")
                throw new ArgumentException("不支持的加载器：" + loader);
            token.ThrowIfCancellationRequested();
            var path = new MinecraftPath(settings.Root);
            using var download = MinecraftDownloadFactory.Create(path, settings, token);
            var launcher = download.Launcher;
            launcher.ByteProgressChanged += (sender, e) =>
                bytes?.Report(new DownloadBytes(e.ProgressedBytes, e.TotalBytes));
            launcher.FileProgressChanged += (sender, e) =>
            {
                if (e.TotalTasks > 0 && e.ProgressedTasks % 40 == 0)
                    progress?.Report("游戏文件 " + e.ProgressedTasks + "/" + e.TotalTasks + " · " + e.Name);
            };
            string installedId = minecraftVersion;
            progress?.Report("正在准备 Minecraft " + minecraftVersion);
            if (loader != "原版")
            {
                // Install the base game before applying a mod loader.
                await launcher.InstallAsync(minecraftVersion, token);
                token.ThrowIfCancellationRequested();
                progress?.Report("正在安装 " + loader + " 加载器...");
                switch (loader)
                {
                    case "Fabric":
                        var fabric = new FabricInstaller(download.Http);
                        installedId = string.IsNullOrWhiteSpace(loaderVersion)
                            ? await fabric.Install(minecraftVersion, path)
                            : await fabric.Install(minecraftVersion, loaderVersion, path);
                        break;
                    case "Quilt":
                        var quilt = new QuiltInstaller(download.Http);
                        installedId = string.IsNullOrWhiteSpace(loaderVersion)
                            ? await quilt.Install(minecraftVersion, path)
                            : await quilt.Install(minecraftVersion, loaderVersion, path);
                        break;
                    case "Forge":
                        using (var catalog = new LoaderNetworkCatalog(settings, download.Http, token, loaderVersion))
                        {
                            var forge = new ForgeInstaller(launcher, catalog, new ForgeInstallerVersionMapper());
                            var options = ForgeOptions(token);
                            installedId = string.IsNullOrWhiteSpace(loaderVersion)
                                ? await forge.Install(minecraftVersion, options)
                                : await forge.Install(minecraftVersion, loaderVersion, options);
                        }
                        break;
                    case "NeoForge":
                        using (var catalog = new LoaderNetworkCatalog(settings, download.Http, token, loaderVersion))
                        {
                            var version = await catalog.FindNeoForgeVersionAsync(minecraftVersion);
                            installedId = await new NeoForgeInstaller(launcher, download.Http)
                                .Install(version, ForgeOptions(token));
                        }
                        break;
                }
            }
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(installedId)) throw new IOException("加载器没有返回有效的版本 ID");
            progress?.Report("正在下载游戏依赖和运行库：" + installedId);
            await launcher.InstallAsync(installedId, token);
            token.ThrowIfCancellationRequested();
            string metadata = Path.Combine(settings.Root, "versions", installedId, installedId + ".json");
            if (!File.Exists(metadata)) throw new IOException("安装完成后未找到版本元数据：" + metadata);
            progress?.Report("安装完成：" + installedId);
            return new GameProfile
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Name = minecraftVersion + " [" + loader.ToLowerInvariant() + "]",
                BaseVersion = minecraftVersion,
                LaunchVersion = installedId,
                Loader = loader == "原版" ? "vanilla" : loader.ToLowerInvariant(),
                Directory = settings.Root
            };
        }
    }
}
