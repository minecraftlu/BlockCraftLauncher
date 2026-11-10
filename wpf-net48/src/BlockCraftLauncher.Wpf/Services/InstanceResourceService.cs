using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class LocalResourceItem : INotifyPropertyChanged
    {
        BitmapSource icon;
        public string Name { get; set; }
        public string FullPath { get; set; }
        public bool Disabled { get; set; }
        public bool IsMod { get; set; }
        public BitmapSource Icon
        {
            get => icon;
            internal set
            {
                if (icon == value) return;
                icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public override string ToString() => (Disabled ? "[已停用] " : "") + Name;
    }
    /// <summary>Local, explicit resource management. Never auto-deletes arbitrary jars.</summary>
    public sealed class InstanceResourceService
    {
        public static string Folder(GameProfile profile, string kind)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Directory))
                throw new InvalidOperationException("请先选择有效的游戏实例");
            string relative;
            switch (kind)
            {
                case "模组": relative = "mods"; break;
                case "光影": relative = "shaderpacks"; break;
                case "资源包": relative = "resourcepacks"; break;
                default: throw new ArgumentException("不支持的资源类型");
            }
            return Path.Combine(Path.GetFullPath(profile.Directory), relative);
        }
        public static IReadOnlyList<LocalResourceItem> List(GameProfile profile, string kind)
        {
            var folder = Folder(profile, kind);
            if (!Directory.Exists(folder)) return new List<LocalResourceItem>();
            bool isMod = kind == "模组";
            string extension = isMod ? ".jar" : ".zip";
            return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(x => x.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                    x.EndsWith(extension + ".disabled", StringComparison.OrdinalIgnoreCase))
                .Select(x => new LocalResourceItem
                {
                    Name = Path.GetFileName(x),
                    FullPath = x,
                    Disabled = x.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    IsMod = isMod
                })
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        public static string Import(GameProfile profile, string kind, string file)
        {
            string folder = Folder(profile, kind);
            string ext = kind == "模组" ? ".jar" : ".zip";
            if (!File.Exists(file) || !file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("选择的文件格式不符合当前资源类型");
            const long maximumSize = 512L * 1024L * 1024L;
            if (new FileInfo(file).Length > maximumSize) throw new IOException("资源文件超过 512 MiB 的导入限制");
            string name = Path.GetFileName(file);
            if (name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("无效资源名称");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, name);
            if (File.Exists(target)) throw new IOException("目标资源已存在，为防止覆盖，请先检查：" + name);
            string temp = target + ".import-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(file, temp, false);
                File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return target;
        }
        public static string SetEnabled(GameProfile profile, string kind, LocalResourceItem item, bool enabled)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            string folder = Path.GetFullPath(Folder(profile, kind)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string source = Path.GetFullPath(item.FullPath);
            if (!source.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(source) != folder.TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("资源不属于当前实例");
            string dest = enabled
                ? (source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? source.Substring(0, source.Length - 9) : source)
                : (source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? source : source + ".disabled");
            if (!File.Exists(source)) throw new FileNotFoundException("找不到指定资源", source);
            if (!string.Equals(source, dest, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(dest)) throw new IOException("目标文件已存在，拒绝覆盖");
                File.Move(source, dest);
            }
            return dest;
        }
    }
}
