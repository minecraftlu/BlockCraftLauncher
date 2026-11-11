using System;
using System.Collections.Generic;
using System.IO;

namespace BlockCraftLauncher.Wpf.Models
{
    public sealed class GameProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string BaseVersion { get; set; }
        public string LaunchVersion { get; set; }
        public string Loader { get; set; }
        public string Directory { get; set; }
        public override string ToString() => Name + "  ·  " + BaseVersion + " / " + Loader;
    }

    public sealed class LauncherConfig
    {
        public string Root { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockCraftLauncher", "minecraft");
        // Root is the Minecraft installation destination; DownloadDirectory
        // is separate and only hosts launcher-owned temporary packages.
        // Empty retains the legacy LocalAppData cache root.
        public string DownloadDirectory { get; set; } = "";
        public string JavaPath { get; set; } = "";
        public string Proxy { get; set; } = "";
        public bool McimEnabled { get; set; } = false;
        // For new installs, verified stable updates are downloaded and installed automatically.
        // Previously saved user preferences continue to take precedence.
        // Installation must wait while Minecraft, downloads or unsaved edits are active.
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoDownloadUpdates { get; set; } = true;
        public bool AutoInstallUpdatesOnExit { get; set; } = false;
        public bool AutoRestartAfterUpdateDownload { get; set; } = true;
        public string UpdateChannel { get; set; } = "stable";
        public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;
        public string NetworkMode { get; set; } = "auto";
        public string Theme { get; set; } = "浅色";
        public string Accent { get; set; } = "#1875CE";
        public int Zoom { get; set; } = 100;
        public int MemoryMb { get; set; } = 4096;
        public int DownloadThreads { get; set; } = 12;
        public int DownloadRetries { get; set; } = 4;
        public string AccountMode { get; set; } = "offline";
        public string OfflineName { get; set; } = "Player123";
        public string AccountName { get; set; } = "";
        public List<GameProfile> Profiles { get; } = new List<GameProfile>();
        public Dictionary<string, object> Raw { get; set; } = new Dictionary<string, object>();
    }
}
