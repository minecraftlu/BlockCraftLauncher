using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class LocalModIcon
    {
        const int MetadataLimit = 256 * 1024;
        const int ImageLimit = 2 * 1024 * 1024;
        const int ImageEdgeLimit = 2048;
        const int ThumbnailEdge = 96;
        static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        static readonly string[] CommonIcons = { "icon.png", "logo.png", "mod_icon.png", "pack.png" };
        // Consume multiline TOML values before looking for single-line mod fields.
        static readonly Regex ForgeFields = new Regex(
            @"^[ \t]*(?:[A-Za-z0-9_.-]+[ \t]*=[ \t]*(?:'{3}[\s\S]*?'{3}|""{3}[\s\S]*?""{3})|" +
            @"(?<key>modId|logoFile)[ \t]*=[ \t]*(?:""(?<value>[^""\r\n]+)""|'(?<value>[^'\r\n]+)')[ \t]*(?:#[^\r\n]*)?\r?$)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

        internal static BitmapSource Read(string path)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var jar = new ZipArchive(file, ZipArchiveMode.Read))
                {
                    var declared = new List<string>();
                    var modIds = new List<string>();
                    var fabric = ReadJson(jar, "fabric.mod.json");
                    AddJsonIcon(Field(fabric, "icon"), declared);
                    modIds.Add(Field(fabric, "id") as string);

                    var quilt = Field(ReadJson(jar, "quilt.mod.json"), "quilt_loader");
                    AddJsonIcon(Field(Field(quilt, "metadata"), "icon"), declared);
                    modIds.Add(Field(quilt, "id") as string);

                    AddForgeFields(jar, "META-INF/neoforge.mods.toml", declared, modIds);
                    AddForgeFields(jar, "META-INF/mods.toml", declared, modIds);

                    var legacy = ReadJson(jar, "mcmod.info");
                    var mods = (Field(legacy, "modList") ?? legacy) as object[] ?? new[] { legacy };
                    foreach (var mod in mods.Take(8))
                    {
                        declared.Add(Field(mod, "logoFile") as string);
                        modIds.Add(Field(mod, "modid") as string);
                    }

                    var conventional = modIds.Where(IsModId).Distinct(StringComparer.Ordinal).Take(8)
                        .SelectMany(id => new[] { "assets/" + id + "/icon.png", "assets/" + id + "/logo.png",
                            id + "_icon.png", id + "_logo.png", id + ".png" });
                    var candidates = declared.Take(24).Concat(conventional).Concat(CommonIcons)
                        .Select(ArchivePath).Where(name => name != null).Distinct(StringComparer.Ordinal);
                    foreach (string candidate in candidates)
                    {
                        var icon = ReadImage(jar.GetEntry(candidate));
                        if (icon != null) return icon;
                    }
                }
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                error is UnauthorizedAccessException || error is NotSupportedException)
            {
                // A missing, unreadable or incomplete JAR uses the default icon.
            }
            return null;
        }

        static bool IsModId(string id) => !string.IsNullOrEmpty(id) && id.Length <= 64 &&
            id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-');

        static string ArchivePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) return null;
            string name = value.Replace('\\', '/').TrimStart('/');
            return name.Contains(":") || name.Split('/').Any(part => part == "" || part == "." || part == "..")
                ? null : name;
        }

        static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
        {
            if (entry == null || entry.Length < 1 || entry.Length > limit) return null;
            var bytes = new byte[(int)entry.Length];
            using (var input = entry.Open())
            {
                int total = 0;
                while (total < bytes.Length)
                {
                    int count = input.Read(bytes, total, bytes.Length - total);
                    if (count == 0) return null;
                    total += count;
                }
                return input.ReadByte() == -1 ? bytes : null;
            }
        }

        static string ReadText(ZipArchive jar, string name)
        {
            try
            {
                var bytes = ReadEntry(jar.GetEntry(name), MetadataLimit);
                return bytes == null ? null : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            }
            catch (InvalidDataException) { return null; }
        }

        static object ReadJson(ZipArchive jar, string name)
        {
            string text = ReadText(jar, name);
            if (text == null) return null;
            try
            {
                return new JavaScriptSerializer { MaxJsonLength = MetadataLimit, RecursionLimit = 32 }
                    .DeserializeObject(text);
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        static object Field(object value, string key) =>
            value is IDictionary<string, object> row && row.TryGetValue(key, out var field) ? field : null;

        static void AddJsonIcon(object icon, List<string> declared)
        {
            if (icon is string path) declared.Add(path);
            if (!(icon is IDictionary<string, object> sizes)) return;
            declared.AddRange(sizes.Where(row => row.Value is string)
                .Select(row => (Size: int.TryParse(row.Key, NumberStyles.None, CultureInfo.InvariantCulture,
                    out int size) ? size : 0, Path: (string)row.Value))
                .Where(row => row.Size > 0)
                .OrderBy(row => row.Size < ThumbnailEdge)
                .ThenBy(row => row.Size < ThumbnailEdge ? -row.Size : row.Size)
                .Take(8).Select(row => row.Path));
        }

        static void AddForgeFields(ZipArchive jar, string name, List<string> declared, List<string> modIds)
        {
            string text = ReadText(jar, name);
            if (text == null) return;
            try
            {
                bool foundModId = false;
                foreach (Match field in ForgeFields.Matches(text))
                {
                    string key = field.Groups["key"].Value;
                    string value = field.Groups["value"].Value;
                    if (key == "logoFile") declared.Add(value);
                    if (key != "modId" || foundModId) continue;
                    modIds.Add(value);
                    foundModId = true;
                }
            }
            catch (RegexMatchTimeoutException) { }
        }

        static BitmapSource ReadImage(ZipArchiveEntry entry)
        {
            try
            {
                var bytes = ReadEntry(entry, ImageLimit);
                if (bytes == null || bytes.Length < 8) return null;
                bool png = bytes.Take(PngSignature.Length).SequenceEqual(PngSignature);
                bool jpeg = bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
                if (!png && !jpeg) return null;
                using (var buffer = new MemoryStream(bytes, false))
                {
                    var frame = BitmapDecoder.Create(buffer, BitmapCreateOptions.IgnoreColorProfile,
                        BitmapCacheOption.None).Frames.FirstOrDefault();
                    if (frame == null) return null;
                    int width = frame.PixelWidth, height = frame.PixelHeight;
                    if (width < 1 || height < 1 || width > ImageEdgeLimit || height > ImageEdgeLimit) return null;
                    buffer.Position = 0;
                    var icon = new BitmapImage();
                    icon.BeginInit();
                    icon.CacheOption = BitmapCacheOption.OnLoad;
                    icon.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    icon.StreamSource = buffer;
                    if (width >= height)
                        icon.DecodePixelWidth = Math.Min(ThumbnailEdge, width);
                    else
                        icon.DecodePixelHeight = Math.Min(ThumbnailEdge, height);
                    icon.EndInit();
                    icon.Freeze();
                    return icon;
                }
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException ||
                error is FormatException || error is ArgumentException || error is InvalidOperationException ||
                error is NotSupportedException || error is COMException)
            {
                return null;
            }
        }
    }
}
