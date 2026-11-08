using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Explicitly opted-in *real account + real room metadata* preflight.
    /// Launches user-provided login helper locally, queries read-only NetEase
    /// room and public-node endpoints, and derives volatile candidate session
    /// parameters. DOES NOT dial RakNet, send EnterRoom, or connect WebRTC.
    /// </summary>
    public static class RoomSessionPreflight
    {
        private const string RoomEndpoint =
            "https://g79mcltransfer.minecraft.cn/room-with-name";
        private const string NodeEndpoint =
            "https://g79.update.netease.com/transferserver_obt_new.list";
        private const int MaxText = 1024 * 1024;
        private const int MaxAuthChars = 8192;

        public sealed class LocalSession
        {
            public uint PlayerID { get; internal set; }
            public string Token { get; internal set; }
            public string ModeCategory { get; internal set; }
            public bool IsPE { get; internal set; }
        }

        public static bool TryParseLocalSession(string output, out LocalSession session)
        {
            session = null;
            if (String.IsNullOrWhiteSpace(output) || output.Length > MaxAuthChars)
                return false;

            uint uid = 0;
            string token = null;
            bool pe = false;
            string mode = "missing";
            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim().TrimStart('\uFEFF');
                int equal = line.IndexOf('=');
                if (equal < 1) continue;
                string label = line.Substring(0, equal).Trim();
                string value = line.Substring(equal + 1).Trim();
                if (label.Equals("uid", StringComparison.OrdinalIgnoreCase))
                    UInt32.TryParse(value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out uid);
                else if (label.Equals("platform", StringComparison.OrdinalIgnoreCase))
                    pe = value == "2";
                else if (label.Equals("login_token", StringComparison.OrdinalIgnoreCase))
                {
                    // Do not accept free-form output as a token. Only ASCII
                    // characters in the expected bounded key length.
                    if (Regex.IsMatch(value, @"^[\x21-\x7E]{16,32}$"))
                        token = value;
                }
                else if (label.Equals("auth_mode", StringComparison.OrdinalIgnoreCase))
                {
                    mode = value.Equals("g79", StringComparison.OrdinalIgnoreCase)
                        ? "g79"
                        : value.Equals("x19", StringComparison.OrdinalIgnoreCase)
                            ? "x19" : "other";
                }
            }
            if (uid == 0 || !pe || token == null
                || mode == "x19" || mode == "other") return false;

            session = new LocalSession
            {
                PlayerID = uid,
                Token = token,
                IsPE = pe,
                ModeCategory = mode
            };
            return true;
        }

        public static async Task<string> RunAsync(string helperPath, string roomNumber)
        {
            if (!Regex.IsMatch(roomNumber ?? "", @"^[0-9]{4,10}$"))
                return "LIVE_PREFLIGHT_INPUT_ERROR: 房间号必须是 4 到 10 位数字。";
            if (!GuestEmailAuth.IsAllowedHelperPath(helperPath)
                || !File.Exists(helperPath) || !GuestEmailAuth.CredentialFileExists())
                return "LIVE_PREFLIGHT_BLOCKED: 请先选择受信任的 connect.exe 并确保本机 Cookie 已保存。";

            string output = null;
            LocalSession session = null;
            try
            {
                output = await ReadHelperSessionAsync(helperPath).ConfigureAwait(false);
                if (!TryParseLocalSession(output, out session))
                    return "LIVE_PE_SESSION_INCONCLUSIVE: 未取得可用的 PE 会话信息或模式不兼容。"
                        + " 不会显示或记录原始令牌。";

                string roomJson;
                string nodeJson;
                using (var client = new HttpClient(new HttpClientHandler
                {
                    AllowAutoRedirect = false, UseCookies = false
                }))
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    var body = new JavaScriptSerializer().Serialize(
                        new Dictionary<string, object> {
                            { "name", roomNumber }, { "uid", session.PlayerID }
                        });
                    using (var request = new HttpRequestMessage(HttpMethod.Post, RoomEndpoint))
                    {
                        request.Headers.TryAddWithoutValidation("user-id",
                            session.PlayerID.ToString(CultureInfo.InvariantCulture));
                        request.Content = new StringContent(body, Encoding.UTF8,
                            "application/json");
                        using (var response = await client.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                            roomJson = await ReadBoundedAsync(response).ConfigureAwait(false);
                    }
                    if (roomJson == null)
                        return "LIVE_ROOM_LOOKUP_INCONCLUSIVE: 本次没有获得有效的房间信息。";

                    RoomConnectionMetadata.Room room;
                    if (!RoomConnectionMetadata.TryParseRoom(roomJson, roomNumber, out room))
                        return "LIVE_ROOM_LOOKUP_INCONCLUSIVE: 房间查找成功返回数据，"
                            + "但无法确认房主 ID 和大厅节点。房间可能已关闭。";
                    if (room.IsFull)
                        return "LIVE_ROOM_FULL: 房间元数据提示人数已满；未发入房请求。";
                    if (room.NeedsPassword)
                        return "LIVE_ROOM_PASSWORD_REQUIRED: 该房间需密码；"
                            + "当前预检不收集或尝试密码。";

                    using (var response = await client.GetAsync(NodeEndpoint,
                        HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        nodeJson = await ReadBoundedAsync(response).ConfigureAwait(false);
                    RoomConnectionMetadata.Node node;
                    if (nodeJson == null ||
                        !RoomConnectionMetadata.TryParseNode(nodeJson, room.NodeID, out node))
                        return "LIVE_NODE_LOOKUP_INCONCLUSIVE: 房间节点无法在公开列表中可靠匹配。";

                    if (!RoomConnectionMaterial.SupportsTokenKey(session.Token))
                        return "LIVE_TOKEN_KEY_INCOMPATIBLE: 该会话令牌长度不适用于"
                            + "目前已研究的 AES 参数派生格式，未尝试入房。";

                    using (var material = RoomConnectionMaterial.CreateRandom(session.Token))
                    {
                        // Sanity check, not evidence of authorization.
                        if (material.RaknetRand.Length != 16 ||
                            material.RaknetAESRand.Length != 32 ||
                            material.SignalingSeed.Length != 16 ||
                            material.SignalingTicket.Length != 32 ||
                            material.EncryptKeyBytes.Length != 32 ||
                            material.DecryptKeyBytes.Length != 32)
                            return "LIVE_MATERIAL_DERIVATION_INCONCLUSIVE: 结构长度校验失败。";
                    }

                    // Intentionally no port/IP or UID in the response. The node
                    // record identifies shared infrastructure, not a game host.
                    return "LIVE_ROOM_SESSION_MATERIAL_PREPARED_NOT_ADMITTED: "
                        + "使用第二账号会话对真实房间 " + roomNumber
                        + " 完成只读元数据核对、节点映射和本机临时加密材料构造。"
                        + " 模式标记：" + session.ModeCategory + "。"
                        + " 未向 RakNet 大厅发送登录或 EnterRoom 请求，"
                        + "未得到服务器准入回执，也未连接手机游戏。";
                }
            }
            catch (TaskCanceledException)
            {
                return "LIVE_PREFLIGHT_TIMEOUT: 房间或节点网络请求超时。";
            }
            catch (HttpRequestException)
            {
                return "LIVE_PREFLIGHT_NETWORK_ERROR: 房间与节点接口网络请求失败。";
            }
            catch (Exception)
            {
                // A third-party error message may contain passwords or tokens.
                return "LIVE_PREFLIGHT_INCONCLUSIVE: 本地会话或网络准备遇到错误。"
                    + " 未输出原始错误、账号数据或令牌。";
            }
            finally
            {
                if (session != null) session.Token = null;
                output = null;
            }
        }

        private static async Task<string> ReadHelperSessionAsync(string helperPath)
        {
            GuestEmailAuth.PreparePrivateWorkspace();
            string config = Path.Combine(GuestEmailAuth.GetWorkspace(),
                "live-room-preflight-config.json");
            Process process = null;
            try
            {
                File.WriteAllText(config,
                    GuestG79Verifier.BuildConfig(GuestEmailAuth.GetCredentialFile()),
                    new UTF8Encoding(false));
                process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Path.GetFullPath(helperPath),
                        Arguments = "-dump-auth -c \"" + config + "\"",
                        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath)),
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                if (!process.Start()) return null;
                // Bound and drain both streams. stderr is discarded always.
                Task<string> outTask = Task.Run(() =>
                    ReadLimited(process.StandardOutput, MaxAuthChars));
                Task<string> errTask = Task.Run(() =>
                    ReadLimited(process.StandardError, 0));
                Task wait = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(wait, Task.Delay(90000)).ConfigureAwait(false) != wait)
                {
                    try { process.Kill(); } catch (Exception) { }
                    await wait.ConfigureAwait(false);
                    await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                    return null;
                }
                await wait.ConfigureAwait(false);
                string output = await outTask.ConfigureAwait(false);
                await errTask.ConfigureAwait(false);
                if (process.ExitCode != 0 || output.Length > MaxAuthChars)
                    return null;
                return output;
            }
            catch (Exception) { return null; }
            finally
            {
                if (process != null) process.Dispose();
                try { if (File.Exists(config)) File.Delete(config); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string ReadLimited(StreamReader input, int limit)
        {
            var text = new StringBuilder();
            char[] buffer = new char[1024];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length <= limit)
                {
                    int n = Math.Min(count, limit + 1 - text.Length);
                    if (n > 0) text.Append(buffer, 0, n);
                }
                Array.Clear(buffer, 0, buffer.Length);
            }
            return text.ToString();
        }

        private static async Task<string> ReadBoundedAsync(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode
                || (response.Content.Headers.ContentLength.HasValue
                    && response.Content.Headers.ContentLength.Value > MaxText))
                return null;
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream))
            {
                var buffer = new char[4096];
                var outText = new StringBuilder();
                int n;
                while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)
                    .ConfigureAwait(false)) > 0)
                {
                    outText.Append(buffer, 0, n);
                    if (outText.Length > MaxText) return null;
                }
                return outText.ToString();
            }
        }
    }
}
