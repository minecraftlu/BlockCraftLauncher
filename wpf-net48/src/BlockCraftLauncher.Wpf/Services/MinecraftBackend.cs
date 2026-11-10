using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>Native C# Minecraft bridge. No Python runtime or Go bootstrap.</summary>
    public sealed class MinecraftBackend
    {
        readonly LauncherConfig config;
        public MinecraftBackend(LauncherConfig config) { this.config = config; }
        public async Task<IReadOnlyList<string>> ListVersionsAsync()
        {
            using var download = MinecraftDownloadFactory.Create(new MinecraftPath(config.Root), config);
            var all = await download.Launcher.GetAllVersionsAsync();
            return all.Select(v => v.Name).ToArray();
        }

        async Task<Process> BuildLaunchProcessAsync(GameProfile profile, string version, MLaunchOption options)
        {
            var location = MinecraftInstanceLocator.Resolve(profile, config);
            var path = new MinecraftPath(location.GameRoot);
            if (location.MetadataFound)
            {
                var installed = MinecraftDownloadFactory.CreateLocal(path);
                try
                {
                    // A complete installed version, including loader inheritance,
                    // does not need to contact the public version catalog to launch.
                    return await installed.BuildProcessAsync(version, options);
                }
                catch (KeyNotFoundException)
                {
                    // A partially imported version may still need its parent metadata.
                }
            }
            using var online = MinecraftDownloadFactory.Create(path, config);
            return await online.Launcher.BuildProcessAsync(version, options);
        }

        public async Task<GameProfile> InstallVanillaAsync(string version, IProgress<string> progress, CancellationToken cancellation)
        {
            if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("未选择版本");
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("准备安装 Minecraft " + version);
            using var download = MinecraftDownloadFactory.Create(new MinecraftPath(config.Root), config, cancellation);
            await download.Launcher.InstallAsync(version, cancellation);
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("安装完成：" + version);
            return new GameProfile { Id = Guid.NewGuid().ToString("N").Substring(0, 12), Name = version + " [vanilla]",
                BaseVersion = version, LaunchVersion = version, Loader = "vanilla", Directory = config.Root };
        }

        public async Task<Process> LaunchOfflineAsync(GameProfile profile, string serverAddress = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (config.AccountMode != "offline")
                throw new InvalidOperationException("请在启动页切换到离线玩家，或使用当前微软账号启动。");
            var username = (config.OfflineName ?? "").Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(username, "^[A-Za-z0-9_]{3,16}$"))
                throw new InvalidOperationException("离线玩家名必须是 3～16 位英文字母、数字或下划线。");
            string id = string.IsNullOrWhiteSpace(profile.LaunchVersion) ? profile.BaseVersion : profile.LaunchVersion;
            var server = string.IsNullOrWhiteSpace(serverAddress) ? null : MultiplayerService.ParseAddress(serverAddress);
            var process = await BuildLaunchProcessAsync(profile, id, new MLaunchOption {
                Session = MSession.CreateOfflineSession(username),
                MaximumRamMb = Math.Max(512, config.MemoryMb),
                JavaPath = string.IsNullOrWhiteSpace(config.JavaPath) ? null : config.JavaPath,
                GameLauncherName = "BlockCraftLauncher",
                GameLauncherVersion = "0.9.34",
                ScreenWidth = 1280,
                ScreenHeight = 720,
                ServerIp = server == null ? null : server.Item1,
                ServerPort = server == null ? 25565 : server.Item2
            });
            process.Start();
            return process;
        }
        public async Task<Process> LaunchMicrosoftAsync(GameProfile profile, MSession session, string serverAddress = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (session == null || string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.UUID))
                throw new InvalidOperationException("请先完成微软正版登录，并确认 Minecraft Java 购买资格。");
            string version = string.IsNullOrWhiteSpace(profile.LaunchVersion) ? profile.BaseVersion : profile.LaunchVersion;
            var server = string.IsNullOrWhiteSpace(serverAddress) ? null : MultiplayerService.ParseAddress(serverAddress);
            var process = await BuildLaunchProcessAsync(profile, version, new MLaunchOption
            {
                Session = session,
                MaximumRamMb = Math.Max(512, config.MemoryMb),
                JavaPath = string.IsNullOrWhiteSpace(config.JavaPath) ? null : config.JavaPath,
                GameLauncherName = "BlockCraftLauncher",
                GameLauncherVersion = "0.9.34",
                ScreenWidth = 1280,
                ScreenHeight = 720,
                ServerIp = server == null ? null : server.Item1,
                ServerPort = server == null ? 25565 : server.Item2
            });
            process.Start();
            return process;
        }
    }
}
