using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Enumerates the existing local skin collection and validates a selected
    /// PNG before preview/official use. No OAuth token or network access here.
    /// All paths come from the local library, never an online catalog URL.
    /// </summary>
    internal static class LocalSkinLibrary
    {
        internal sealed class Entry
        {
            internal string Path { get; set; }
            internal string Name { get; set; }
            internal bool Slim { get; set; }
            internal bool HasAttribution { get; set; }
            public override string ToString() => Name;
        }

        internal static List<Entry> List(string library = null)
        {
            string root = System.IO.Path.GetFullPath(library ?? SkinEditorDocument.LocalLibrary);
            Directory.CreateDirectory(root);
            var result = new List<Entry>();
            // Skip launcher draft/staging files and symbolic links. A malformed
            // individual PNG must not prevent the rest of the library loading.
            foreach (string file in Directory.GetFiles(root, "*.png", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (result.Count >= 250) break;
                try
                {
                    ValidatePath(file, root);
                    result.Add(new Entry
                    {
                        Path = file,
                        Name = System.IO.Path.GetFileNameWithoutExtension(file),
                        Slim = ReadModel(file),
                        HasAttribution = File.Exists(file + ".source.txt")
                    });
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is InvalidDataException || ex is ArgumentException)
                {
                    // Broken/temporary entries are not shown.
                }
            }
            return result;
        }

        internal static void ValidatePath(string path, string library = null)
        {
            string root = System.IO.Path.GetFullPath(library ?? SkinEditorDocument.LocalLibrary);
            string absolute = System.IO.Path.GetFullPath(path);
            string name = System.IO.Path.GetFileName(absolute);
            if (!string.Equals(System.IO.Path.GetDirectoryName(absolute), root.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(System.IO.Path.GetExtension(absolute), ".png", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".", StringComparison.Ordinal) ||
                (File.GetAttributes(absolute) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("请选择皮肤库中的普通 PNG 文件，不支持符号链接或外部路径。");
            long size = new FileInfo(absolute).Length;
            if (size <= 0 || size > 4L * 1024 * 1024)
                throw new InvalidDataException("皮肤 PNG 文件大小必须在 4 MiB 以内。");
        }

        internal static bool ReadModel(string path)
        {
            string metadata = path + ".model.txt";
            try
            {
                if (File.Exists(metadata) &&
                    (File.GetAttributes(metadata) & FileAttributes.ReparsePoint) == 0 &&
                    new FileInfo(metadata).Length <= 32)
                    return string.Equals(File.ReadAllText(metadata).Trim(), "slim",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            // Files from older launcher builds did not store an arm model.
            // Default to Steve and let the user choose Alex before uploading.
            return false;
        }

        internal static void WriteModel(string path, bool slim, string library = null)
        {
            ValidatePath(path, library);
            string destination = path + ".model.txt";
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, slim ? "slim" : "classic", Encoding.ASCII);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null, true);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }

        internal static SkinEditorDocument Load(string path, bool slim, string library = null)
        {
            ValidatePath(path, library);
            var document = new SkinEditorDocument();
            document.LoadPng(path, slim);
            return document;
        }

        internal static void RunOfflineSelfTest()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "BlockCraftSkinLibraryTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string skinPath = System.IO.Path.Combine(root, "my-skin.png");
                new SkinEditorDocument().SavePng(skinPath);
                var items = List(root);
                if (items.Count != 1 || items[0].Slim || items[0].Name != "my-skin")
                    throw new InvalidOperationException("Skin library discovery regression");
                var normalized = Load(skinPath, false, root);
                if (normalized.ExportPngBytes().Length < 50 || normalized.SlimArms)
                    throw new InvalidOperationException("Skin library PNG validation regression");
                WriteModel(skinPath, true, root);
                if (!List(root).Single().Slim || !Load(skinPath, true, root).SlimArms)
                    throw new InvalidOperationException("Skin library model persistence regression");
                File.WriteAllBytes(System.IO.Path.Combine(root, ".blockcraft-recovery.png"),
                    normalized.ExportPngBytes());
                if (List(root).Count != 1)
                    throw new InvalidOperationException("Skin library exposed a hidden draft");
                try
                {
                    Load(System.IO.Path.Combine(root, "..", "outside.png"), false, root);
                    throw new InvalidOperationException("Skin library accepted external path");
                }
                catch (FileNotFoundException) { }
                catch (InvalidDataException) { }
                string invalid = System.IO.Path.Combine(root, "broken.png");
                File.WriteAllText(invalid, "not a PNG");
                try
                {
                    Load(invalid, false, root);
                    throw new InvalidOperationException("Skin library accepted invalid PNG");
                }
                catch (InvalidDataException) { }
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
