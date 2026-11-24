using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class QwenConnectionSettings
    {
        public const string DefaultEndpoint = QwenSearchService.DefaultEndpoint;
        const int MaxFileBytes = 1024 * 1024;
        string endpoint = DefaultEndpoint;

        public string Endpoint
        {
            get => endpoint;
            set
            {
                if (!string.Equals(endpoint, value, StringComparison.Ordinal)) ApiKey = "";
                endpoint = value;
            }
        }
        public string Model { get; set; } = QwenSearchService.DefaultModel;
        public string ApiKey { get; set; } = "";
        public bool RememberApiKey { get; set; }

        static string LauncherPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlockCraftLauncher", "qwen.json");

        static string CompanionPath()
        {
            string alternate = Environment.GetEnvironmentVariable("TIDECOMPANION_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(alternate) && alternate.Length >= 3 &&
                char.IsLetter(alternate[0]) && alternate[1] == ':' &&
                (alternate[2] == '\\' || alternate[2] == '/'))
                return Path.Combine(alternate, "settings.json");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TideCompanion", "settings.json");
        }

        // Called only from the import action. Ignore other settings as the reader advances.
        public static QwenConnectionSettings ImportCompanion(string path = null)
        {
            try
            {
                using (var file = OpenBounded(path ?? CompanionPath()))
                using (var reader = JsonReaderWriterFactory.CreateJsonReader(file, new XmlDictionaryReaderQuotas
                {
                    MaxDepth = 32, MaxStringContentLength = MaxFileBytes, MaxArrayLength = MaxFileBytes
                }))
                {
                    if (reader.MoveToContent() != XmlNodeType.Element || reader.GetAttribute("type") != "object")
                        throw new InvalidDataException();
                    reader.ReadStartElement();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    string address = null, model = null;
                    bool demo = false;
                    while (reader.MoveToContent() == XmlNodeType.Element)
                    {
                        string name = reader.LocalName;
                        if (name != "ApiEndpoint" && name != "Model" && name != "UseDemoProvider")
                        {
                            reader.Skip();
                            continue;
                        }
                        if (!seen.Add(name) ||
                            reader.GetAttribute("type") != (name == "UseDemoProvider" ? "boolean" : "string"))
                            throw new InvalidDataException();
                        switch (name)
                        {
                            case "ApiEndpoint": address = reader.ReadElementContentAsString(); break;
                            case "Model": model = reader.ReadElementContentAsString(); break;
                            case "UseDemoProvider": demo = reader.ReadElementContentAsBoolean(); break;
                        }
                    }
                    reader.ReadEndElement();
                    if (reader.MoveToContent() != XmlNodeType.None) throw new InvalidDataException();
                    if (demo) throw new InvalidOperationException("桌面伙伴正在使用演示模式，请先在桌面伙伴中配置千问模型。");
                    return FromPublicFields(address, model);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
            {
                throw new InvalidOperationException("未找到桌面伙伴的设置。请先在桌面伙伴中保存连接，或在此填写。");
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                throw new InvalidOperationException("无法读取桌面伙伴的设置文件。请检查文件后重试，或在此填写。");
            }
        }

        static QwenConnectionSettings FromPublicFields(string address, string model)
        {
            if (!QwenSearchService.IsValidModel(model))
                throw new InvalidOperationException("请填写有效的千问模型 ID。");
            if (!QwenSearchService.TryGetEndpoint(address, out Uri uri))
                throw new InvalidOperationException("当前服务地址不受支持，请使用阿里云百炼的千问兼容接口。");
            return new QwenConnectionSettings { Endpoint = uri.AbsoluteUri, Model = model.Trim() };
        }

        static FileStream OpenBounded(string path)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            try
            {
                if (file.Length == 0 || file.Length > MaxFileBytes) throw new InvalidDataException();
                if (file.ReadByte() != 0xef || file.ReadByte() != 0xbb || file.ReadByte() != 0xbf)
                    file.Position = 0;
                return file;
            }
            catch { file.Dispose(); throw; }
        }

        static bool IsFileError(Exception ex) => ex is IOException || ex is InvalidDataException ||
            ex is UnauthorizedAccessException || ex is SecurityException || ex is ArgumentException ||
            ex is XmlException || ex is SerializationException || ex is FormatException ||
            ex is NotSupportedException;

        [DataContract]
        sealed class LauncherConnectionFile
        {
            [DataMember] public string Endpoint { get; set; }
            [DataMember] public string Model { get; set; }
            [DataMember(EmitDefaultValue = false)] public string ProtectedKey { get; set; }
        }

        static byte[] KeyContext(string canonicalEndpoint) =>
            Encoding.UTF8.GetBytes("BlockCraftLauncher/Qwen/v1/" + canonicalEndpoint);

        public static QwenConnectionSettings Load(string path = null)
        {
            try
            {
                using (var file = OpenBounded(path ?? LauncherPath))
                {
                    var serializer = new DataContractJsonSerializer(typeof(LauncherConnectionFile));
                    var saved = (LauncherConnectionFile)serializer.ReadObject(file);
                    var connection = FromPublicFields(saved?.Endpoint, saved?.Model);
                    if (string.IsNullOrEmpty(saved.ProtectedKey)) return connection;
                    try
                    {
                        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(saved.ProtectedKey),
                            KeyContext(connection.Endpoint), DataProtectionScope.CurrentUser);
                        try
                        {
                            connection.ApiKey = Encoding.UTF8.GetString(plain);
                            connection.RememberApiKey = true;
                        }
                        finally { Array.Clear(plain, 0, plain.Length); }
                    }
                    catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
                    {
                        // Public settings are still usable when the current user cannot unlock this credential.
                    }
                    return connection;
                }
            }
            catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException)
            {
                return new QwenConnectionSettings();
            }
        }

        public string ResolveApiKey()
        {
            if (!QwenSearchService.TryGetEndpoint(Endpoint, out _)) return "";
            return !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() :
                (Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY") ?? "").Trim();
        }

        public void Save(string path = null)
        {
            var connection = FromPublicFields(Endpoint, Model);
            var saved = new LauncherConnectionFile { Endpoint = connection.Endpoint, Model = connection.Model };
            if (RememberApiKey && !string.IsNullOrWhiteSpace(ApiKey))
            {
                byte[] plain = Encoding.UTF8.GetBytes(ApiKey.Trim());
                try
                {
                    saved.ProtectedKey = Convert.ToBase64String(ProtectedData.Protect(
                        plain, KeyContext(saved.Endpoint), DataProtectionScope.CurrentUser));
                }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            string destination = Path.GetFullPath(path ?? LauncherPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    new DataContractJsonSerializer(typeof(LauncherConnectionFile)).WriteObject(file, saved);
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { File.Replace(temporary, destination, null); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
