namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class ModDownloadFile
    {
        public string VersionId { get; set; }
        public string DisplayName { get; set; }
        public string FileName { get; set; }
        public string Published { get; set; }
        public string Release { get; set; }
        public int RequiredDependencies { get; set; }

        public override string ToString() => string.Join("  ·  ", DisplayName, Release, Published);
    }

    internal static class ModDownloadTarget
    {
        internal static string Loader(string loader)
        {
            switch (loader?.Trim().ToLowerInvariant())
            {
                case "fabric": return "Fabric";
                case "forge": return "Forge";
                case "neoforge": return "NeoForge";
                case "quilt": return "Quilt";
                default: return null;
            }
        }
    }
}
