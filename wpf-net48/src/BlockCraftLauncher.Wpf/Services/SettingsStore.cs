using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>Reads the original Python settings.json without discarding unrelated keys.</summary>
    public sealed class SettingsStore
    {
        public string FilePath { get; }
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 };
        public SettingsStore(string filePath = null)
        {
            FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockCraftLauncher", "settings.wpf-preview.json");
        }
        static string S(IDictionary<string, object> d, string key, string fallback = "") => d.ContainsKey(key) && d[key] != null ? Convert.ToString(d[key]) : fallback;
        static int N(IDictionary<string, object> d, string key, int fallback)
        {
            try { return d.ContainsKey(key) ? Convert.ToInt32(d[key]) : fallback; } catch { return fallback; }
        }
        public LauncherConfig Load()
        {
            var config = new LauncherConfig();
            foreach (string path in new[] { FilePath, Path.ChangeExtension(FilePath, ".bak"), Path.Combine(Path.GetDirectoryName(FilePath), "settings.json"), Path.Combine(Path.GetDirectoryName(FilePath), "settings.bak") })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var data = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                    if (data == null) continue;
                    config.Raw = data;
                    config.Root = S(data, "root", config.Root);
                    config.JavaPath = S(data, "java");
                    config.Proxy = S(data, "proxy");
                    config.McimEnabled = S(data, "mcim_enabled", "False").Equals("True", StringComparison.OrdinalIgnoreCase);
                    config.NetworkMode = S(data, "network_mode", "auto");
                    config.Theme = S(data, "ui_mode", "深色");
                    config.Accent = S(data, "ui_accent", "#1875CE");
                    config.Zoom = Math.Max(80, Math.Min(140, N(data, "ui_zoom", 100)));
                    config.MemoryMb = N(data, "memory_mb", 4096);
                    config.DownloadThreads = Math.Max(2, Math.Min(32, N(data, "download_threads", 12)));
                    config.DownloadRetries = Math.Max(1, Math.Min(6, N(data, "download_retries", 4)));
                    config.AccountMode = S(data, "account_mode", "offline");
                    config.OfflineName = S(data, "offline_name", "Player123");
                    config.AccountName = S(data, "account_name");
                    if (data.TryGetValue("profiles", out var items) && items is IEnumerable profiles)
                        foreach (var item in profiles)
                        {
                            if (!(item is IDictionary<string, object> p)) continue;
                            config.Profiles.Add(new GameProfile { Id = S(p, "id"), Name = S(p, "name"), BaseVersion = S(p, "base_version"),
                                LaunchVersion = S(p, "launch_version"), Loader = S(p, "loader", "vanilla"), Directory = S(p, "dir") });
                        }
                    return config;
                }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException) { }
            }
            return config;
        }
        public void Save(LauncherConfig c)
        {
            var data = new Dictionary<string, object>(c.Raw);
            data["root"] = c.Root; data["java"] = c.JavaPath; data["proxy"] = c.Proxy;
            data["mcim_enabled"] = c.McimEnabled;
            data["network_mode"] = c.NetworkMode; data["ui_mode"] = c.Theme; data["ui_accent"] = c.Accent;
            data["ui_zoom"] = c.Zoom; data["memory_mb"] = c.MemoryMb; data["download_threads"] = c.DownloadThreads;
            data["download_retries"] = c.DownloadRetries; data["account_mode"] = c.AccountMode;
            data["offline_name"] = c.OfflineName; data["account_name"] = c.AccountName;
            data["profiles"] = c.Profiles.Select(p => new Dictionary<string, object> {
                ["id"] = p.Id, ["name"] = p.Name, ["base_version"] = p.BaseVersion,
                ["launch_version"] = p.LaunchVersion, ["loader"] = p.Loader, ["dir"] = p.Directory
            }).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json.Serialize(data), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, Path.ChangeExtension(FilePath, ".bak"), true);
            else File.Move(temp, FilePath);
            c.Raw = data;
        }
    }
}
