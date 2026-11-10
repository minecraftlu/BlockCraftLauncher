using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class LocalModIconSanityChecks
    {
        internal static IReadOnlyDictionary<string, int> CreateFixtures(string mods)
        {
            Directory.CreateDirectory(mods);
            var expectedWidths = new Dictionary<string, int>();
            void WriteMod(string filename, string metadataPath, string metadata, int expectedWidth,
                params (string Path, byte[] Bytes)[] images)
            {
                using (var jar = ZipFile.Open(Path.Combine(mods, filename), ZipArchiveMode.Create))
                {
                    if (metadataPath != null) Write(jar, metadataPath, Encoding.UTF8.GetBytes(metadata));
                    foreach (var image in images) Write(jar, image.Path, image.Bytes);
                }
                expectedWidths.Add(filename, expectedWidth);
            }

            WriteMod("01-forge.jar", "META-INF/mods.toml",
                "[[mods]]\nmodId=\"geckolib\"\ndescription='''\nlogoFile=\"wrong.png\"\n'''\n" +
                "logoFile=\"geckolib_icon.png\" # declared project icon\n", 40,
                ("geckolib_icon.png", Png(40, 170, 75, 30)), ("wrong.png", Png(12, 0, 0, 0)));
            WriteMod("02-fabric.jar", "fabric.mod.json",
                "{\"id\":\"fabric_demo\",\"icon\":{\"16\":\"small.png\",\"128\":\"large.png\"}}", 96,
                ("small.png", Png(16, 0, 0, 0)), ("large.png", Png(128, 40, 100, 180)));
            WriteMod("03-neoforge.jar.disabled", "META-INF/neoforge.mods.toml",
                "[[mods]]\nmodId='neo_demo'\nlogoFile='/assets/neo_demo/logo.png'\n", 36,
                ("assets/neo_demo/logo.png", Png(36, 205, 97, 37)));
            WriteMod("04-quilt.jar", "quilt.mod.json",
                "{\"quilt_loader\":{\"id\":\"quilt_demo\",\"metadata\":{\"icon\":\"quilt.png\"}}}", 48,
                ("quilt.png", Png(48, 111, 50, 175)));
            WriteMod("05-legacy.jar", "mcmod.info",
                "{\"modList\":[{\"modid\":\"old_demo\",\"logoFile\":\"legacy.png\"}]}", 32,
                ("legacy.png", Png(32, 35, 122, 85)));
            WriteMod("06-fallback.jar", "fabric.mod.json", "{ invalid json }", 24,
                ("icon.png", Png(24, 51, 122, 139)));
            WriteMod("07-no-icon.jar", "fabric.mod.json",
                "{\"icon\":{\"128\":\"../outside.png\",\"96\":\"https://example.invalid/icon.png\"}}", 0,
                ("../outside.png", Png(40, 0, 0, 0)), ("icon.png", new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            File.WriteAllText(Path.Combine(mods, "08-broken.jar"), "Invalid JAR fixture");
            expectedWidths.Add("08-broken.jar", 0);
            return expectedWidths;
        }

        static void Write(ZipArchive jar, string name, byte[] bytes)
        {
            using (var output = jar.CreateEntry(name).Open()) output.Write(bytes, 0, bytes.Length);
        }

        static byte[] Png(int width, byte red, byte green, byte blue)
        {
            byte[] pixels = Enumerable.Repeat(new[] { blue, green, red, (byte)255 }, width * width)
                .SelectMany(pixel => pixel).ToArray();
            var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new MemoryStream())
            {
                encoder.Save(output);
                return output.ToArray();
            }
        }
    }
}
