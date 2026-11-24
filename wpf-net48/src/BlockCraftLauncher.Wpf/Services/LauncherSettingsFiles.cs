using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Xml;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class LauncherSettingsFiles
    {
        internal static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockCraftLauncher", "settings.wpf-preview.json");

        internal static IEnumerable<string> ReadCandidates(string path)
        {
            yield return path;
            yield return Path.ChangeExtension(path, ".bak");
            var directory = Path.GetDirectoryName(path);
            yield return Path.Combine(directory, "settings.json");
            yield return Path.Combine(directory, "settings.bak");
        }

        internal static bool? ReadStartupTheme(string path)
        {
            foreach (string candidate in ReadCandidates(path))
            {
                try
                {
                    using (var file = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = JsonReaderWriterFactory.CreateJsonReader(file, XmlDictionaryReaderQuotas.Max))
                    {
                        if (reader.MoveToContent() != XmlNodeType.Element || reader.GetAttribute("type") != "object") continue;
                        reader.ReadStartElement();
                        while (reader.MoveToContent() == XmlNodeType.Element)
                        {
                            // Skip profile and account data without materializing the settings object.
                            if (reader.LocalName == "ui_mode" && reader.GetAttribute("type") == "string")
                            {
                                string mode = reader.ReadElementContentAsString();
                                if (mode == "深色") return true;
                                if (mode == "浅色") return false;
                                return null;
                            }
                            reader.Skip();
                        }
                        return null;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                    ex is SecurityException || ex is XmlException || ex is FormatException) { }
            }
            return null;
        }
    }
}
