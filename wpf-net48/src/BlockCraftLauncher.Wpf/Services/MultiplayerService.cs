using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class ServerEntry
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public override string ToString() => Name + "  ·  " + Address;
    }

    /// <summary>Private, local-only server bookmarks and opt-in TCP reachability.</summary>
    public sealed class MultiplayerService
    {
        readonly string file;
        readonly JavaScriptSerializer json = new JavaScriptSerializer();

        public MultiplayerService(string filePath = null)
        {
            file = filePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockCraftLauncher", "servers.wpf-preview.json");
        }

        public static Tuple<string, int> ParseAddress(string raw)
        {
            var value = (raw ?? "").Trim();
            if (value.Length == 0 || value.Length > 300 || value.Any(char.IsWhiteSpace) ||
                value.IndexOfAny(new[] { '/', '\\', '@', '?', '#', '\0' }) >= 0)
                throw new ArgumentException("请输入合法的 Minecraft 服务器域名或 IP（可附端口）");
            string host;
            string portText = "";
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                int end = value.IndexOf(']');
                if (end < 2) throw new ArgumentException("IPv6 地址必须使用 [地址]:端口 格式");
                host = value.Substring(1, end - 1);
                if (!IPAddress.TryParse(host, out IPAddress ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                    throw new ArgumentException("无效的 IPv6 地址");
                if (end + 1 < value.Length)
                {
                    if (value[end + 1] != ':') throw new ArgumentException("服务器端口格式错误");
                    portText = value.Substring(end + 2);
                }
            }
            else if (value.Count(c => c == ':') > 1)
            {
                if (!IPAddress.TryParse(value, out IPAddress ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                    throw new ArgumentException("无效的 IPv6 地址");
                host = value;
            }
            else
            {
                int colon = value.LastIndexOf(':');
                host = colon < 0 ? value : value.Substring(0, colon);
                portText = colon < 0 ? "" : value.Substring(colon + 1);
                if (host.Length == 0 || host.Length > 253 || host.Contains("..") ||
                    host.StartsWith("-", StringComparison.Ordinal) || host.EndsWith("-", StringComparison.Ordinal) ||
                    !host.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    throw new ArgumentException("服务器域名或 IP 格式错误");
            }
            int port = 25565;
            if (value.EndsWith(":", StringComparison.Ordinal) ||
                (portText.Length > 0 && (!int.TryParse(portText, out port) || port < 1 || port > 65535)))
                throw new ArgumentException("端口必须是 1～65535 的数字");
            return Tuple.Create(host, port);
        }

        public List<ServerEntry> Load()
        {
            if (!File.Exists(file)) return new List<ServerEntry>();
            if (new FileInfo(file).Length > 1024 * 1024) throw new IOException("服务器收藏文件过大");
            var result = json.Deserialize<List<ServerEntry>>(File.ReadAllText(file, Encoding.UTF8))
                ?? new List<ServerEntry>();
            foreach (var entry in result) ParseAddress(entry.Address);
            return result.Take(200).ToList();
        }

        public void Save(IEnumerable<ServerEntry> entries)
        {
            var list = entries.ToList();
            if (list.Count > 200) throw new InvalidOperationException("最多收藏 200 个服务器");
            foreach (var e in list)
            {
                if (string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 100)
                    throw new ArgumentException("服务器名称不能为空或超过 100 字");
                ParseAddress(e.Address);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, json.Serialize(list), new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(temp, file, null);
                else File.Move(temp, file);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public async Task<string> CheckReachabilityAsync(string address, CancellationToken ct)
        {
            var parsed = ParseAddress(address);
            // This TCP probe is requested by the user, not a continuous scan.
            using (var client = new TcpClient())
            {
                var connect = client.ConnectAsync(parsed.Item1, parsed.Item2);
                var timeout = Task.Delay(TimeSpan.FromSeconds(4), ct);
                var winner = await Task.WhenAny(connect, timeout);
                ct.ThrowIfCancellationRequested();
                if (winner != connect) return "连接超时（4 秒）";
                await connect;
                return "TCP 可连接（仅连通性，非 Minecraft 协议状态）";
            }
        }
    }
}