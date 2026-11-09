using System;
using System.IO;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// The exact version metadata lookup shared by both CmlLib launch
    /// selection and the "检测本地文件" UI. This is a local file check only,
    /// not a complete game, Java runtime, assets or account integrity test.
    /// </summary>
    public sealed class MinecraftInstanceLocation
    {
        public string VersionId { get; internal set; }
        public string GameRoot { get; internal set; }
        public string MetadataPath { get; internal set; }
        public string InstanceMetadataPath { get; internal set; }
        public string SharedMetadataPath { get; internal set; }
        public bool MetadataFound { get; internal set; }
        public bool UsesInstanceDirectory { get; internal set; }
    }

    public static class MinecraftInstanceLocator
    {
        static string ValidateVersionId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id == "." || id == ".." ||
                id != Path.GetFileName(id) ||
                id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                id.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                throw new InvalidDataException("实例记录中的 Minecraft 版本 ID 不合法");
            return id;
        }

        /// <summary>
        /// Import the selected versions/id/id.json from its actual Minecraft
        /// root. Merely using the configured default root loses external
        /// installations and makes a valid game appear "not detected".
        /// No metadata contents are executed or downloaded during import.
        /// </summary>
        public static GameProfile ImportVersionMetadata(string versionJsonPath)
        {
            if (string.IsNullOrWhiteSpace(versionJsonPath))
                throw new InvalidDataException("请先选择 Minecraft 版本 JSON 文件");

            string fullPath = Path.GetFullPath(versionJsonPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("选中的 Minecraft 版本 JSON 文件不存在", fullPath);

            string versionDirectory = Path.GetDirectoryName(fullPath);
            string versionsDirectory = Path.GetDirectoryName(versionDirectory);
            string gameRoot = Path.GetDirectoryName(versionsDirectory);
            if (string.IsNullOrWhiteSpace(gameRoot) ||
                !string.Equals(Path.GetFileName(versionsDirectory), "versions",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择游戏根目录下 versions\\版本ID\\版本ID.json 格式的文件");

            string id = ValidateVersionId(Path.GetFileName(versionDirectory));
            if (!string.Equals(Path.GetFileName(fullPath), id + ".json",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("版本 JSON 文件名必须与所在版本文件夹名称一致");

            return new GameProfile
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 12),
                Name = id,
                BaseVersion = id,
                LaunchVersion = id,
                Loader = "vanilla",
                Directory = Path.GetFullPath(gameRoot)
            };
        }

        public static MinecraftInstanceLocation Resolve(GameProfile profile, LauncherConfig config)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (config == null) throw new ArgumentNullException(nameof(config));

            string id = ValidateVersionId(string.IsNullOrWhiteSpace(profile.LaunchVersion)
                ? profile.BaseVersion : profile.LaunchVersion);

            if (string.IsNullOrWhiteSpace(config.Root))
                throw new InvalidDataException("启动器未配置 Minecraft 游戏根目录");
            string shared = Path.GetFullPath(config.Root);
            string instance = string.IsNullOrWhiteSpace(profile.Directory)
                ? shared : Path.GetFullPath(profile.Directory);
            string ownMetadata = Path.Combine(instance, "versions", id, id + ".json");
            string sharedMetadata = Path.Combine(shared, "versions", id, id + ".json");
            bool ownFound = File.Exists(ownMetadata);
            // CmlLib uses the profile's own MinecraftPath when it contains
            // the actual version metadata; otherwise it uses the shared root.
            string chosenRoot = ownFound ? instance : shared;
            string chosenMetadata = ownFound ? ownMetadata : sharedMetadata;
            return new MinecraftInstanceLocation
            {
                VersionId = id,
                GameRoot = chosenRoot,
                MetadataPath = chosenMetadata,
                InstanceMetadataPath = ownMetadata,
                SharedMetadataPath = sharedMetadata,
                MetadataFound = File.Exists(chosenMetadata),
                UsesInstanceDirectory = ownFound
            };
        }
    }
}
