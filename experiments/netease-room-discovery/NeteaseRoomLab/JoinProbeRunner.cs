using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Runs the embedded, purpose-built Stage9 Go RakNet guest probe in a local
    /// child process. No login credentials or session tokens are supplied via
    /// command arguments. Only room ID, helper path and local cookie path.
    /// No arbitrary stdout/stderr from either binary is exposed in the GUI.
    /// </summary>
    public static class JoinProbeRunner
    {
        private const string ResourceName = "BlockCraft.JoinProbe.exe";
        private const int MaxStdout = 8 * 1024;
        private const int TimeoutMilliseconds = 115000;
        private const string MachinePrefix = "BLOCKCRAFT_JOIN_PROBE_V1\t";

        private static readonly Dictionary<string, string> Messages =
            new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "HOST_CONSENT_REQUIRED", "没有确认房主许可，未执行请求。" },
            { "ROOM_ID_INVALID", "房间号无效。" },
            { "INVALID_CONTEXT", "连接上下文不可用。" },
            { "PE_AUTH_UNAVAILABLE", "第二账号无法获得当前 PE 会话。不要分享账号或 token。" },
            { "ROOM_LOOKUP_FAILED", "无法查询当前真实房间。" },
            { "ROOM_NOT_JOINABLE", "房间不存在、已满、需要密码或房间信息不兼容。" },
            { "LOBBY_NODE_LOOKUP_FAILED", "公开大厅节点查询失败。" },
            { "LOBBY_NODE_MAPPING_FAILED", "房间对应的大厅节点未可靠匹配。" },
            { "RAKNET_TRANSPORT_FAILED", "已尝试真实 RakNet 网络连接，但没有建立传输。" },
            { "LOCAL_KEY_DERIVATION_FAILED", "本机无法生成临时大厅会话密钥。" },
            { "RAKNET_CODEC_FAILED", "大厅协议编解码器未成功初始化。" },
            { "TAN_LOGIN_SEND_FAILED", "无法发送真实大厅登录请求。" },
            { "TAN_LOGIN_RESPONSE_MISSING", "已发送登录请求，但没有收到可识别的服务器回执。" },
            { "TAN_LOGIN_RESPONSE_UNEXPECTED", "服务器返回了未预期的大厅登录数据包。" },
            { "TAN_LOGIN_REJECTED", "服务器明确拒绝第二账号登录大厅。" },
            { "TAN_ENCRYPTION_SETUP_FAILED", "服务器接受大厅登录，但本机会话加密初始化失败。" },
            { "CLIENT_NETWORK_ID_FAILED", "本机游客连接 ID 生成失败。" },
            { "ENTER_ROOM_SEND_FAILED", "大厅登录成功，但入房请求发送失败。" },
            { "ENTER_ROOM_RESPONSE_MISSING", "已发送入房请求，但没有收到可识别的回执。" },
            { "ENTER_ROOM_RESPONSE_UNEXPECTED", "服务器入房回执格式不符合预期。" },
            { "ENTER_ROOM_REJECTED", "服务器明确拒绝第二账号进入该房间。" },
            { "TAN_LOGIN_ACCEPTED", "真实网易大厅登录已被服务端接受，但尚未收到房间准入回执。" },
            { "SERVER_ROOM_ADMISSION_ACCEPTED", "真实服务器入房回执成功，但游戏客户端并未连接。" },
            { "HOST_NETHERNET_READY_GAME_NOT_CONNECTED", "大厅入房成功且房主 NetherNet 已就绪，但尚未建立 WebRTC 游戏连接。" },
            { "HOST_NETHERNET_ID_INVALID", "收到房主就绪事件，但 NetherNet ID 格式不兼容。" },
            { "SIGNALING_NODE_UNAVAILABLE", "大厅节点没有可靠的信令 WebSocket 端口。" },
            { "SIGNALING_TICKET_DERIVATION_FAILED", "第二账号临时会话无法生成该版本信令票据。" },
            { "SIGNALING_CONNECTION_FAILED", "大厅准入成功，但信令 WebSocket 握手或 ICE 凭据阶段没有完成。" },
            { "SIGNALING_AUTHENTICATED_WEBRTC_PENDING", "信令已返回 ICE 凭据，但 WebRTC 尚未建立。" },
            { "WEBRTC_NEGOTIATION_FAILED", "真实信令连接成功，但与房主的 ICE/DTLS/SCTP 协商失败。" },
            { "WEBRTC_ICE_CREDENTIALS_FAILED", "信令连接成功，但无法读取或使用 ICE 服务器授权信息。" },
            { "WEBRTC_LOCAL_CANDIDATE_GATHER_FAILED", "电脑无法收集本地 ICE 候选；检查 Windows 防火墙和网络接口。" },
            { "WEBRTC_LOCAL_CANDIDATE_GATHER_TIMEOUT", "电脑未能及时完成 ICE 候选收集；排查网络接口和 STUN/TURN。" },
            { "WEBRTC_OFFER_SEND_FAILED", "无法向手机房主发送 WebRTC 连接邀请。" },
            { "WEBRTC_CANDIDATE_SEND_FAILED", "无法向手机房主发送 ICE 候选。" },
            { "WEBRTC_ANSWER_TIMEOUT", "连接邀请已发送，但没有及时收到手机房主的有效应答；确认房间在线和手机版允许电脑版进入。" },
            { "WEBRTC_ANSWER_INVALID", "手机房主的 WebRTC 应答格式不兼容或不符合预期。" },
            { "WEBRTC_REMOTE_CANDIDATE_TIMEOUT", "收到房主连接应答，但未及时收到可用远端 ICE 候选。" },
            { "WEBRTC_REMOTE_CANDIDATE_UNAVAILABLE", "房主应答后未获得可用 ICE 候选。" },
            { "WEBRTC_ICE_CONNECT_FAILED", "收到 ICE 候选，但局域网或中继连接失败；排查路由器客户端隔离、Windows 防火墙及 TURN。" },
            { "WEBRTC_ICE_CONNECT_TIMEOUT", "ICE 建连超时；即使同一个 Wi-Fi，也可能存在设备隔离或 UDP 被拦截。" },
            { "WEBRTC_DTLS_HANDSHAKE_FAILED", "ICE 阶段已推进，但 DTLS 安全握手失败。" },
            { "WEBRTC_DTLS_HANDSHAKE_TIMEOUT", "ICE 阶段已推进，但 DTLS 安全握手超时。" },
            { "WEBRTC_SCTP_HANDSHAKE_FAILED", "ICE/DTLS 已推进，但 SCTP 数据传输握手失败。" },
            { "WEBRTC_SCTP_HANDSHAKE_TIMEOUT", "SCTP 数据传输阶段超时。" },
            { "WEBRTC_DATA_CHANNEL_FAILED", "SCTP 阶段已推进，但可靠/不可靠数据通道创建失败。" },
            { "WEBRTC_REMOTE_REJECTED", "WebRTC 信令收到了远端拒绝或错误通知。" },
            { "WEBRTC_SIGNALING_INTERRUPTED", "WebRTC 协商期间信令连接中断。" },
            { "WEBRTC_PROBE_CANCELLED", "WebRTC 连接测试已取消，不代表服务器拒绝入房。" },
            { "WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED", "已完成房主 NetherNet/WebRTC 传输连接；未进行 Minecraft 游戏握手或加载世界。" }
        };

        public static bool IsEmbedded
        {
            get
            {
                using (var resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(ResourceName))
                {
                    return resource != null;
                }
            }
        }

        /// <summary>
        /// Stage 9.1: parse precisely one framed machine record. Never scan
        /// for the first/last braces of arbitrary console text; malformed
        /// framing and unexpected stdout are rejected without echoing bytes.
        /// </summary>
        public static string ParseMachineOutput(string rawOutput)
        {
            if (String.IsNullOrWhiteSpace(rawOutput))
                return "JOIN_PROBE_IPC_NO_ENVELOPE：探针没有返回版本化状态记录。";
            if (rawOutput.Length > MaxStdout)
                return "JOIN_PROBE_IPC_TRUNCATED：探针输出超出安全长度，已隐藏内容。";

            string frame = null;
            foreach (string rawLine in rawOutput.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0) continue;
                if (!line.StartsWith(MachinePrefix, StringComparison.Ordinal)
                    || frame != null)
                    return "JOIN_PROBE_IPC_UNEXPECTED_OUTPUT：收到非预期或重复的子进程输出，已隐藏内容。";
                frame = line.Substring(MachinePrefix.Length);
            }
            if (frame == null)
                return "JOIN_PROBE_IPC_NO_ENVELOPE：探针没有返回版本化状态记录。";

            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxStdout };
                var response = serializer.Deserialize<Dictionary<string, object>>(frame);
                object statusObject;
                if (response == null || !response.TryGetValue("status", out statusObject)
                    || !(statusObject is string))
                    return "JOIN_PROBE_IPC_SCHEMA_ERROR：探针没有返回可识别的状态字段。";

                // The Go component must not claim world admission. Server
                // acceptance booleans must also match each other and the
                // corresponding status before the GUI describes success.
                bool login, admitted, hostReady, worldJoined;
                if (!ReadBool(response, "login_accepted", out login)
                    || !ReadBool(response, "room_admission_accepted", out admitted)
                    || !ReadBool(response, "host_ready_seen", out hostReady)
                    || !ReadBool(response, "minecraft_world_joined", out worldJoined)
                    || worldJoined || (admitted && !login) || (hostReady && !admitted))
                    return "JOIN_PROBE_IPC_INCONSISTENT：子进程状态标志不一致，未判定成功。";

                bool signaling = false, rtc = false;
                object optional;
                if (response.TryGetValue("signaling_authenticated", out optional))
                {
                    if (!(optional is bool))
                        return "JOIN_PROBE_IPC_SCHEMA_ERROR：信令状态格式不正确。";
                    signaling = (bool)optional;
                }
                if (response.TryGetValue("webrtc_transport_connected", out optional))
                {
                    if (!(optional is bool))
                        return "JOIN_PROBE_IPC_SCHEMA_ERROR：WebRTC 状态格式不正确。";
                    rtc = (bool)optional;
                }
                if ((signaling && !hostReady) || (rtc && !signaling))
                    return "JOIN_PROBE_IPC_INCONSISTENT：WebRTC 状态与大厅准入不一致。";

                string status = (string)statusObject;
                bool acceptedOnly = status == "SERVER_ROOM_ADMISSION_ACCEPTED";
                bool readyOnly = status == "HOST_NETHERNET_READY_GAME_NOT_CONNECTED";
                bool signalingError = status == "HOST_NETHERNET_ID_INVALID"
                    || status == "SIGNALING_NODE_UNAVAILABLE"
                    || status == "SIGNALING_TICKET_DERIVATION_FAILED"
                    || status == "SIGNALING_CONNECTION_FAILED";
                bool pending = status == "SIGNALING_AUTHENTICATED_WEBRTC_PENDING";
                bool negotiationFailure = status == "WEBRTC_NEGOTIATION_FAILED"
                    || status == "WEBRTC_ICE_CREDENTIALS_FAILED"
                    || status == "WEBRTC_LOCAL_CANDIDATE_GATHER_FAILED"
                    || status == "WEBRTC_LOCAL_CANDIDATE_GATHER_TIMEOUT"
                    || status == "WEBRTC_OFFER_SEND_FAILED"
                    || status == "WEBRTC_CANDIDATE_SEND_FAILED"
                    || status == "WEBRTC_ANSWER_TIMEOUT"
                    || status == "WEBRTC_ANSWER_INVALID"
                    || status == "WEBRTC_REMOTE_CANDIDATE_TIMEOUT"
                    || status == "WEBRTC_REMOTE_CANDIDATE_UNAVAILABLE"
                    || status == "WEBRTC_ICE_CONNECT_FAILED"
                    || status == "WEBRTC_ICE_CONNECT_TIMEOUT"
                    || status == "WEBRTC_DTLS_HANDSHAKE_FAILED"
                    || status == "WEBRTC_DTLS_HANDSHAKE_TIMEOUT"
                    || status == "WEBRTC_SCTP_HANDSHAKE_FAILED"
                    || status == "WEBRTC_SCTP_HANDSHAKE_TIMEOUT"
                    || status == "WEBRTC_DATA_CHANNEL_FAILED"
                    || status == "WEBRTC_REMOTE_REJECTED"
                    || status == "WEBRTC_SIGNALING_INTERRUPTED"
                    || status == "WEBRTC_PROBE_CANCELLED";
                bool rtcComplete = status == "WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED";
                bool isPostAdmission = acceptedOnly || readyOnly || signalingError
                    || pending || negotiationFailure || rtcComplete;
                if ((acceptedOnly && (!login || !admitted || hostReady))
                    || (readyOnly && (!login || !admitted || !hostReady))
                    || (admitted && !isPostAdmission)
                    || (isPostAdmission && (!login || !admitted))
                    || ((signalingError || pending || negotiationFailure || rtcComplete) && !hostReady)
                    || (signalingError && (signaling || rtc))
                    || ((pending || negotiationFailure) && (!signaling || rtc))
                    || (rtcComplete && (!signaling || !rtc))
                    || (rtc && !rtcComplete))
                    return "JOIN_PROBE_IPC_INCONSISTENT：服务器准入、信令和 WebRTC 状态不匹配。";
                if (status == "TAN_LOGIN_ACCEPTED" && !login)
                    return "JOIN_PROBE_IPC_INCONSISTENT：服务器登录标记与状态不匹配。";

                return ReportStatus(frame);
            }
            catch (Exception)
            {
                return "JOIN_PROBE_IPC_SCHEMA_ERROR：探针机器输出解析失败，原始内容已隐藏。";
            }
        }

        private static bool ReadBool(Dictionary<string, object> response, string key, out bool value)
        {
            value = false;
            object raw;
            if (!response.TryGetValue(key, out raw) || !(raw is bool))
                return false;
            value = (bool)raw;
            return true;
        }

        public static string ReportStatus(string json)
        {
            if (String.IsNullOrWhiteSpace(json) || json.Length > MaxStdout)
                return "JOIN_PROBE_OUTPUT_INVALID：未获得受控的探针状态。";
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaxStdout };
                var response = serializer.Deserialize<Dictionary<string, object>>(json);
                object statusObject;
                if (response == null || !response.TryGetValue("status", out statusObject)
                    || !(statusObject is string))
                    return "JOIN_PROBE_OUTPUT_INVALID：无法识别状态格式。";

                string status = (string)statusObject;
                string message;
                if (!Messages.TryGetValue(status, out message))
                    return "JOIN_PROBE_STATUS_UNKNOWN：探针返回了未知状态，未显示任何原始内容。";

                // The Go CLI emits only a whitelist of fixed statuses and no
                // credential data. We display only the already-whitelisted name
                // and constant explanation, and never any raw JSON detail.
                return status + "：" + message
                    + Environment.NewLine
                    + "说明：服务端大厅登录、服务器入房和 Minecraft 世界进入属于不同状态。"
                    + " 此探针绝不报告游戏已启动。";
            }
            catch (Exception)
            {
                return "JOIN_PROBE_OUTPUT_INVALID：输出格式不合法，原始内容已隐藏。";
            }
        }

        public static Task<string> RunAsync(string room, string helperPath)
        {
            return RunAsync(room, helperPath, false);
        }

        public static async Task<string> RunAsync(string room, string helperPath, bool tryWebRTC)
        {
            if (String.IsNullOrWhiteSpace(room) || room.Length > 10)
                return "JOIN_PROBE_INPUT_INVALID：房间号无效。";
            foreach (char c in room)
                if (c < '0' || c > '9')
                    return "JOIN_PROBE_INPUT_INVALID：房间号只能输入数字。";

            if (!GuestEmailAuth.IsAllowedHelperPath(helperPath) ||
                !File.Exists(helperPath) || !GuestEmailAuth.CredentialFileExists())
                return "JOIN_PROBE_NOT_READY：本机没有可用的第二账号认证组件或 Cookie 文件。";
            GuestEmailAuth.PreparePrivateWorkspace();

            string localExecutable = null;
            Process process = null;
            try
            {
                localExecutable = ExtractVerifiedExecutable();
                if (localExecutable == null)
                    return "JOIN_PROBE_COMPONENT_UNAVAILABLE：当前 EXE 尚未嵌入真实 RakNet 探针。"
                        + " 请下载 Stage9 专用发布版。";

                string cookie = GuestEmailAuth.GetCredentialFile();
                var startInfo = new ProcessStartInfo
                {
                    FileName = localExecutable,
                    Arguments = "-room \"" + room + "\""
                        + " -helper \"" + Path.GetFullPath(helperPath) + "\""
                        + " -cookie \"" + cookie + "\""
                        + " -host-consent -try-join -timeout 90s -machine-json"
                        + (tryWebRTC ? " -try-webrtc" : ""),
                    WorkingDirectory = Path.GetDirectoryName(localExecutable),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true),
                    StandardErrorEncoding = new UTF8Encoding(false, true),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    ErrorDialog = false
                };
                process = new Process { StartInfo = startInfo };
                if (!process.Start())
                    return "JOIN_PROBE_START_FAILED：无法启动本地 RakNet 探针。";

                Task<string> stdout = Task.Run(() => ReadCapped(process.StandardOutput, MaxStdout));
                Task<string> stderr = Task.Run(() => ReadCapped(process.StandardError, 0));
                Task exit = Task.Run(() => process.WaitForExit());
                if (await Task.WhenAny(exit, Task.Delay(TimeoutMilliseconds)) != exit)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    try { await exit; } catch (Exception) { }
                    await Task.WhenAll(stdout, stderr);
                    return "JOIN_PROBE_TIMEOUT：已终止长时间无响应的本地探针。";
                }
                await exit;
                string result = await stdout;
                await stderr;
                // Stage 9.1 machine mode always exits 0 after emitting one
                // status frame, including genuine denial. Failure to emit a
                // well-formed frame is a distinct IPC error, not server denial.
                if (process.ExitCode != 0)
                    return "JOIN_PROBE_IPC_PROCESS_FAILURE：本机探针异常退出。未输出原始日志。";
                return ParseMachineOutput(result);
            }
            catch (Exception)
            {
                return "JOIN_PROBE_FAILED：本机网络探针异常，未显示任何登录凭据或原始日志。";
            }
            finally
            {
                if (process != null) process.Dispose();
            }
        }

        private static string ExtractVerifiedExecutable()
        {
            using (var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(ResourceName))
            {
                if (stream == null) return null;

                string targetDir = Path.Combine(GuestEmailAuth.GetWorkspace(), "helpers");
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                string destination = Path.Combine(targetDir, ResourceName);
                byte[] resourceBytes;
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    resourceBytes = memory.ToArray();
                }

                // Avoid replacing an executable while another instance may be
                // running. Bind filename to SHA-256 of the embedded executable.
                byte[] hash;
                using (var algorithm = SHA256.Create())
                    hash = algorithm.ComputeHash(resourceBytes);
                string suffix = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                destination = Path.Combine(targetDir, "BlockCraft.JoinProbe."
                    + suffix.Substring(0, 16) + ".exe");

                if (File.Exists(destination))
                {
                    using (var existing = File.OpenRead(destination))
                    using (var algorithm = SHA256.Create())
                    {
                        byte[] previousHash = algorithm.ComputeHash(existing);
                        bool same = previousHash.Length == hash.Length;
                        for (int i = 0; same && i < hash.Length; i++)
                            if (previousHash[i] != hash[i]) same = false;
                        if (same) return destination;
                    }
                }
                // Written only into the current Windows user's private
                // workspace; never allow user-supplied binary paths here.
                File.WriteAllBytes(destination, resourceBytes);
                Array.Clear(resourceBytes, 0, resourceBytes.Length);
                return destination;
            }
        }

        private static string ReadCapped(StreamReader source, int cap)
        {
            var text = new StringBuilder();
            var chunk = new char[1024];
            int count;
            while ((count = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                int remaining = cap + 1 - text.Length;
                if (remaining > 0)
                    text.Append(chunk, 0, Math.Min(count, remaining));
                Array.Clear(chunk, 0, chunk.Length);
            }
            return text.ToString();
        }
    }
}
