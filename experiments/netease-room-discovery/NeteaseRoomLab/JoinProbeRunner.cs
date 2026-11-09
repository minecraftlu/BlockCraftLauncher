using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
            { "WEBRTC_SCTP_HANDSHAKE_FAILED", "ICE/DTLS 已推进，但 SCTP 数据传输握手失败；需进一步核对底层关联错误。" },
            { "WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED", "SCTP INIT 重传耗尽，未收到有效 INIT ACK。确认手机房主仍在游戏中；可能存在对端 WebRTC/SCTP 协议不兼容。" },
            { "WEBRTC_SCTP_COOKIE_ACK_NOT_RECEIVED", "SCTP COOKIE ECHO 重传耗尽，没有完成 COOKIE ACK 阶段；优先排查网易客户端 SCTP 兼容及远端主动关闭。" },
            { "WEBRTC_SCTP_ASSOCIATION_CLOSED", "SCTP 关联建立前连接被关闭；可能是房主主动断开、DTLS 关闭或协议不兼容，不代表密码错误。" },
            { "WEBRTC_SCTP_HANDSHAKE_TIMEOUT", "SCTP 数据传输阶段超时（旧版诊断状态）。" },
            { "WEBRTC_SCTP_PROBE_DEADLINE", "外部 WebRTC 测试时限已到，SCTP 仍未完成；这不等于手机未发送 INIT ACK 或 COOKIE ACK。" },
            { "WEBRTC_DATA_CHANNEL_FAILED", "SCTP 阶段已推进，但可靠/不可靠数据通道创建失败。" },
            { "WEBRTC_REMOTE_REJECTED", "WebRTC 信令收到了远端拒绝或错误通知。" },
            { "WEBRTC_SIGNALING_INTERRUPTED", "WebRTC 协商期间信令连接中断。" },
            { "WEBRTC_PROBE_CANCELLED", "WebRTC 连接测试已取消，不代表服务器拒绝入房。" },
            { "WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED", "房主 WebRTC 传输曾建立，但此前没有检测连接存活时间；未进入 Minecraft 世界。" },
            { "WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED", "WebRTC 传输已成功建立，8 秒被动存活检查期间未发生断开；未发送 Minecraft 游戏包或进入世界。" },
            { "WEBRTC_EARLY_DISCONNECT_GAME_NOT_JOINED", "WebRTC 曾完成协商，但手机房主在稳定性检查期间断开；本次未判定稳定。" },
            { "WEBRTC_STABILITY_WINDOW_INTERRUPTED", "WebRTC 协商成功，但整体时限在稳定性检查结束前到达；尚不能判定稳定。" },
            { "LOCAL_PROBE_CANCELLED", "本机用户已取消测试，探针释放连接；这不是网易服务器拒绝入房。" },
            { "OBSERVATION_DURATION_INVALID", "被动观察仅支持 8、30 或 60 秒。" },
            { "WEBRTC_CHANNEL_OBSERVER_UNAVAILABLE", "此 EXE 缺少两条数据通道的本地只读诊断模块。" },
            { "WEBRTC_CHANNELS_NOT_OPEN_GAME_NOT_JOINED", "WebRTC 已协商，但观察结束时可靠和不可靠数据通道未同时处于 OPEN。" },
            { "WEBRTC_CHANNELS_EARLY_CLOSED_GAME_NOT_JOINED", "观察期间数据通道关闭或房主传输断开，不能判定连接稳定。" },
            { "WEBRTC_CHANNELS_OBSERVATION_INTERRUPTED", "观察时限中断，没有完成 30 或 60 秒测试。" },
            { "WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED", "两条数据通道均为 OPEN；30 秒被动观察内没有收到应用消息。尚未进入世界。" },
            { "WEBRTC_CHANNELS_OPEN_30S_DATA_OBSERVED_GAME_NOT_JOINED", "两条数据通道均为 OPEN；30 秒被动观察收到数据通道消息。尚未验证 Minecraft 游戏协议。" },
            { "WEBRTC_CHANNELS_OPEN_60S_NO_DATA_GAME_NOT_JOINED", "两条数据通道均为 OPEN；60 秒被动观察内没有收到应用消息。尚未进入世界。" },
            { "WEBRTC_CHANNELS_OPEN_60S_DATA_OBSERVED_GAME_NOT_JOINED", "两条数据通道均为 OPEN；60 秒被动观察收到数据通道消息。尚未验证 Minecraft 游戏协议。" },
            { "STAGE12_READONLY_WORKER_INVALID", "未启用有效的 Stage 12 本机只读接收组件；没有发起真实入房。" },
            { "STAGE12_READONLY_WORKER_UNAVAILABLE_GAME_NOT_JOINED", "真实房间传输曾建立，但本机只读工作进程无法启动。未发送游戏包。" },
            { "STAGE12_READONLY_IPC_FAILED_GAME_NOT_JOINED", "本机只读 IPC 接收发生错误；未发送 Minecraft 游戏包，也未进入世界。" }
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
                bool stable = false;
                if (response.TryGetValue("transport_stable", out optional))
                {
                    if (!(optional is bool))
                        return "JOIN_PROBE_IPC_SCHEMA_ERROR：稳定性状态格式不正确。";
                    stable = (bool)optional;
                }
                bool reliableOpen = false, unreliableOpen = false;
                long reliableMessages=0, unreliableMessages=0, reliableBytes=0, unreliableBytes=0;
                long observedSeconds=0;
                if (response.ContainsKey("reliable_channel_open")
                    && !ReadBool(response, "reliable_channel_open", out reliableOpen))
                    return "JOIN_PROBE_IPC_SCHEMA_ERROR：可靠通道状态格式错误。";
                if (response.ContainsKey("unreliable_channel_open")
                    && !ReadBool(response, "unreliable_channel_open", out unreliableOpen))
                    return "JOIN_PROBE_IPC_SCHEMA_ERROR：不可靠通道状态格式错误。";
                bool inboundIPCVerified=false;
                long inboundIPCMessages=0;
                if ((response.ContainsKey("stage12_readonly_ipc_verified")
                     && !ReadBool(response,"stage12_readonly_ipc_verified",out inboundIPCVerified))
                    || (response.ContainsKey("stage12_readonly_ipc_messages")
                     && !ReadUIntBounded(response,"stage12_readonly_ipc_messages",
                        256,out inboundIPCMessages)))
                    return "JOIN_PROBE_IPC_SCHEMA_ERROR：Stage 12.6 本机接收统计字段非法。";
                if ((response.ContainsKey("reliable_messages")
                     && !ReadUIntBounded(response, "reliable_messages", 1000000, out reliableMessages))
                    || (response.ContainsKey("unreliable_messages")
                     && !ReadUIntBounded(response, "unreliable_messages", 1000000, out unreliableMessages))
                    || (response.ContainsKey("reliable_bytes")
                     && !ReadUIntBounded(response, "reliable_bytes", 1000000000, out reliableBytes))
                    || (response.ContainsKey("unreliable_bytes")
                     && !ReadUIntBounded(response, "unreliable_bytes", 1000000000, out unreliableBytes))
                    || (response.ContainsKey("observation_seconds")
                     && !ReadUIntBounded(response, "observation_seconds", 60, out observedSeconds)))
                    return "JOIN_PROBE_IPC_SCHEMA_ERROR：被动统计字段不合法。";
                if (stable && !rtc)
                    return "JOIN_PROBE_IPC_INCONSISTENT：稳定性标志缺少实际 WebRTC 连接。";
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
                    || status == "WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED"
                    || status == "WEBRTC_SCTP_COOKIE_ACK_NOT_RECEIVED"
                    || status == "WEBRTC_SCTP_ASSOCIATION_CLOSED"
                    || status == "WEBRTC_SCTP_HANDSHAKE_TIMEOUT"
                    || status == "WEBRTC_SCTP_PROBE_DEADLINE"
                    || status == "WEBRTC_DATA_CHANNEL_FAILED"
                    || status == "WEBRTC_REMOTE_REJECTED"
                    || status == "WEBRTC_SIGNALING_INTERRUPTED"
                    || status == "WEBRTC_PROBE_CANCELLED"
                    || status == "WEBRTC_EARLY_DISCONNECT_GAME_NOT_JOINED"
                    || status == "WEBRTC_STABILITY_WINDOW_INTERRUPTED"
                    || status == "WEBRTC_CHANNEL_OBSERVER_UNAVAILABLE"
                    || status == "WEBRTC_CHANNELS_NOT_OPEN_GAME_NOT_JOINED"
                    || status == "WEBRTC_CHANNELS_EARLY_CLOSED_GAME_NOT_JOINED"
                    || status == "WEBRTC_CHANNELS_OBSERVATION_INTERRUPTED"
                    || status == "STAGE12_READONLY_WORKER_UNAVAILABLE_GAME_NOT_JOINED"
                    || status == "STAGE12_READONLY_IPC_FAILED_GAME_NOT_JOINED";
                bool rtcComplete = status == "WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED";
                bool rtcStable = status == "WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED";
                bool channels30 = status == "WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED"
                    || status == "WEBRTC_CHANNELS_OPEN_30S_DATA_OBSERVED_GAME_NOT_JOINED";
                bool channels60 = status == "WEBRTC_CHANNELS_OPEN_60S_NO_DATA_GAME_NOT_JOINED"
                    || status == "WEBRTC_CHANNELS_OPEN_60S_DATA_OBSERVED_GAME_NOT_JOINED";
                bool channelSuccess = channels30 || channels60;
                bool dataObserved = status == "WEBRTC_CHANNELS_OPEN_30S_DATA_OBSERVED_GAME_NOT_JOINED"
                    || status == "WEBRTC_CHANNELS_OPEN_60S_DATA_OBSERVED_GAME_NOT_JOINED";
                bool noData = status == "WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED"
                    || status == "WEBRTC_CHANNELS_OPEN_60S_NO_DATA_GAME_NOT_JOINED";
                bool isPostAdmission = acceptedOnly || readyOnly || signalingError
                    || pending || negotiationFailure || rtcComplete || rtcStable || channelSuccess;
                if ((acceptedOnly && (!login || !admitted || hostReady))
                    || (readyOnly && (!login || !admitted || !hostReady))
                    || (admitted && !isPostAdmission)
                    || (isPostAdmission && (!login || !admitted))
                    || ((signalingError || pending || negotiationFailure || rtcComplete || rtcStable) && !hostReady)
                    || (signalingError && (signaling || rtc))
                    || ((pending || negotiationFailure) && (!signaling || rtc))
                    || (rtcComplete && (!signaling || !rtc || stable))
                    || (rtcStable && (!signaling || !rtc || !stable))
                    || (channelSuccess && (!signaling || !rtc || !stable
                        || !reliableOpen || !unreliableOpen
                        || observedSeconds != (channels30 ? 30 : 60)))
                    || (channelSuccess && (dataObserved != (reliableMessages > 0
                        || unreliableMessages > 0)))
                    || (channelSuccess && (noData != (reliableMessages == 0
                        && unreliableMessages == 0)))
                    || (!channelSuccess && (reliableOpen && unreliableOpen && stable
                        && status != "WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED"))
                    || (stable && !rtcStable && !channelSuccess)
                    || (rtc && !rtcComplete && !rtcStable && !channelSuccess))
                    return "JOIN_PROBE_IPC_INCONSISTENT：服务器准入、信令和 WebRTC 状态不匹配。";
                if ((inboundIPCVerified && (!channelSuccess || !login || !admitted
                        || !hostReady || !signaling || !rtc || !stable
                        || !reliableOpen || !unreliableOpen
                        || inboundIPCMessages!=reliableMessages
                        || inboundIPCMessages>256))
                    || (!inboundIPCVerified && inboundIPCMessages!=0))
                    return "JOIN_PROBE_IPC_INCONSISTENT：无法核实 Stage 12.6 接收进程与网易传输状态。";
                if (status == "TAN_LOGIN_ACCEPTED" && !login)
                    return "JOIN_PROBE_IPC_INCONSISTENT：服务器登录标记与状态不匹配。";

                string safeStatus = ReportStatus(frame);
                // Stage 12.5 is a read-only prerequisite diagnosis. Recompute
                // the claimed result from independently parsed server/transport
                // booleans, rather than trusting an arbitrary child status.
                object readinessObject;
                if (response.TryGetValue("stage12_readiness", out readinessObject))
                {
                    bool identityVerified, protocolVerified, gameLoginAttempted;
                    string expected = ComputeStage12Readiness(login, admitted, hostReady,
                        signaling, rtc, stable, reliableOpen, unreliableOpen);
                    if (!(readinessObject is string)
                        || !String.Equals((string)readinessObject, expected,
                            StringComparison.Ordinal)
                        || !ReadBool(response, "stage12_game_identity_verified",
                            out identityVerified)
                        || !ReadBool(response, "stage12_game_protocol_verified",
                            out protocolVerified)
                        || !ReadBool(response, "stage12_game_login_attempted",
                            out gameLoginAttempted)
                        || identityVerified || protocolVerified || gameLoginAttempted)
                        return "JOIN_PROBE_IPC_INCONSISTENT：Stage 12 游戏授权或版本声明缺乏可信证据。";
                    safeStatus += Environment.NewLine
                        + expected + "：" + ExplainStage12Readiness(expected);
                }
                if (inboundIPCVerified)
                    safeStatus += Environment.NewLine
                        + "STAGE12_READONLY_IPC_VERIFIED_GAME_NOT_JOINED：本机只读进程已完成控制握手；"
                        + "本次从手机方向转交可靠消息 " + inboundIPCMessages
                        + " 条（可能为 0）。没有向手机发送 Minecraft 游戏包，未登录世界。";
                if (channelSuccess)
                    safeStatus += Environment.NewLine
                        + "被动观察：" + observedSeconds + " 秒；可靠通道 OPEN、不可靠通道 OPEN。"
                        + Environment.NewLine
                        + "收到的通道消息（不含内容）：可靠 " + reliableMessages
                        + " 条 / " + reliableBytes + " 字节；不可靠 "
                        + unreliableMessages + " 条 / " + unreliableBytes + " 字节。"
                        + " 零条消息也不等于连接失败。";
                return safeStatus;
            }
            catch (Exception)
            {
                return "JOIN_PROBE_IPC_SCHEMA_ERROR：探针机器输出解析失败，原始内容已隐藏。";
            }
        }

        // These stages establish transport prerequisites only. G79 auth_mode
        // is not a verified game identity chain or a protocol-version proof.
        public static string ComputeStage12Readiness(
            bool login, bool admitted, bool hostReady, bool signaling,
            bool transport, bool stable, bool reliableOpen, bool unreliableOpen)
        {
            if (!login || !admitted) return "STAGE12_ADMISSION_NOT_VERIFIED";
            if (!hostReady) return "STAGE12_HOST_NOT_READY";
            if (!signaling) return "STAGE12_SIGNALING_NOT_VERIFIED";
            if (!transport) return "STAGE12_TRANSPORT_NOT_CONNECTED";
            if (!stable) return "STAGE12_TRANSPORT_NOT_STABLE";
            if (!reliableOpen || !unreliableOpen)
                return "STAGE12_DATA_CHANNELS_NOT_VERIFIED";
            return "STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED";
        }

        private static string ExplainStage12Readiness(string status)
        {
            switch (status)
            {
                case "STAGE12_ADMISSION_NOT_VERIFIED":
                    return "尚未证明网易大厅允许第二账号进入当前房间。";
                case "STAGE12_HOST_NOT_READY":
                    return "尚未获得房主 NetherNet 就绪事件。";
                case "STAGE12_SIGNALING_NOT_VERIFIED":
                    return "尚未确认当前房间的信令会话成功。";
                case "STAGE12_TRANSPORT_NOT_CONNECTED":
                    return "尚未确认 WebRTC 传输连接已建立。";
                case "STAGE12_TRANSPORT_NOT_STABLE":
                    return "尚未完成当前传输的有界稳定性检查。";
                case "STAGE12_DATA_CHANNELS_NOT_VERIFIED":
                    return "尚未验证两个数据通道同时 OPEN。";
                default:
                    return "网络前置条件已观察到，但缺少网易认可的游戏身份提供者"
                        + "及当前 Bedrock 游戏协议版本证明；未尝试 Minecraft 登录。";
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

        private static bool ReadUIntBounded(Dictionary<string, object> response,
            string key, long maximum, out long value)
        {
            value = 0;
            object raw;
            if (!response.TryGetValue(key, out raw) || raw == null)
                return false;
            if (!(raw is int) && !(raw is long))
                return false;
            value = Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
            return value >= 0 && value <= maximum;
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
            return RunAsync(room, helperPath, false, CancellationToken.None);
        }

        public static Task<string> RunAsync(string room, string helperPath, bool tryWebRTC)
        {
            return RunAsync(room, helperPath, tryWebRTC, CancellationToken.None);
        }

        // WPF owns a cancellation token for the entire guest-room session.
        // On cancel it closes the child's stdin; the Go CLI then cancels its
        // context, which releases helper, RakNet, signaling and WebRTC sockets.
        // A bounded final kill is only a fallback for a stuck external helper.
        public static Task<string> RunAsync(
            string room, string helperPath, bool tryWebRTC,
            CancellationToken cancellationToken)
        {
            return RunAsync(room, helperPath, tryWebRTC, 8, cancellationToken);
        }

        public static Task<string> RunAsync(
            string room, string helperPath, bool tryWebRTC,
            int observationSeconds, CancellationToken cancellationToken)
        {
            return RunCoreAsync(room,helperPath,tryWebRTC,
                observationSeconds,cancellationToken,false);
        }

        public static Task<string> RunReadOnlyIPCAsync(
            string room, string helperPath, int observationSeconds,
            CancellationToken cancellationToken)
        {
            return RunCoreAsync(room,helperPath,true,
                observationSeconds,cancellationToken,true);
        }

        private static async Task<string> RunCoreAsync(
            string room,string helperPath,bool tryWebRTC,
            int observationSeconds,CancellationToken cancellationToken,
            bool useReadOnlyIPC)
        {
            if (observationSeconds != 8 && observationSeconds != 30 && observationSeconds != 60)
                return "OBSERVATION_DURATION_INVALID：只允许 8、30 或 60 秒被动观察。";
            if (useReadOnlyIPC && (observationSeconds != 30 && observationSeconds != 60))
                return "STAGE12_READONLY_WORKER_INVALID：只读接收需要 30 或 60 秒观察。";
            if (cancellationToken.IsCancellationRequested)
                return "LOCAL_PROBE_CANCELLED：本机用户取消了测试。";
            if (String.IsNullOrWhiteSpace(room) || room.Length > 10)
                return "JOIN_PROBE_INPUT_INVALID：房间号无效。";
            foreach (char c in room)
                if (c < '0' || c > '9')
                    return "JOIN_PROBE_INPUT_INVALID：房间号只能输入数字。";

            if (!GuestEmailAuth.IsAllowedHelperPath(helperPath) ||
                !File.Exists(helperPath) || !GuestEmailAuth.CredentialFileExists())
                return "JOIN_PROBE_NOT_READY：本机没有可用的第二账号认证组件或 Cookie 文件。";
            GuestEmailAuth.PreparePrivateWorkspace();

            Process process = null;
            CancellationTokenRegistration registration = default(CancellationTokenRegistration);
            try
            {
                string localExecutable = ExtractVerifiedExecutable();
                string inboundWorker = useReadOnlyIPC ? ExtractReadOnlyWorker() : null;
                if (useReadOnlyIPC && inboundWorker == null)
                    return "STAGE12_READONLY_WORKER_UNAVAILABLE_GAME_NOT_JOINED："
                        + "当前 EXE 未内置完整的只读接收工作进程。";
                if (localExecutable == null)
                    return "JOIN_PROBE_COMPONENT_UNAVAILABLE：当前 EXE 没有嵌入真实 RakNet 探针。"
                        + " 请使用新的稳定版实验构建。";
                if (cancellationToken.IsCancellationRequested)
                    return "LOCAL_PROBE_CANCELLED：本机用户取消了测试。";

                string cookie = GuestEmailAuth.GetCredentialFile();
                var startInfo = new ProcessStartInfo
                {
                    FileName = localExecutable,
                    Arguments = "-room \"" + room + "\""
                        + " -helper \"" + Path.GetFullPath(helperPath) + "\""
                        + " -cookie \"" + cookie + "\""
                        + " -host-consent -try-join -timeout "
                        + (105 + (observationSeconds > 8 ? observationSeconds : 0)).ToString(
                            System.Globalization.CultureInfo.InvariantCulture) + "s -machine-json"
                        + " -cancel-on-stdin-close"
                        + (tryWebRTC ? " -try-webrtc -observe-seconds "
                            + observationSeconds.ToString(
                                System.Globalization.CultureInfo.InvariantCulture) : "")
                        + (useReadOnlyIPC ? " -try-readonly-ipc -stage12-worker \""
                            + inboundWorker + "\"" : ""),
                    WorkingDirectory = Path.GetDirectoryName(localExecutable),
                    RedirectStandardInput = true,
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

                // Register after Start. Register synchronously closes the pipe
                // when the token was already cancelled before registration.
                registration = cancellationToken.Register(() => TryCloseInput(process));
                Task<string> stdout = Task.Run(() => ReadCapped(process.StandardOutput, MaxStdout));
                Task<string> stderr = Task.Run(() => ReadCapped(process.StandardError, 0));
                Task exit = Task.Run(() => process.WaitForExit());
                Task cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
                int timeoutMs = TimeoutMilliseconds
                    + (observationSeconds > 8 ? observationSeconds * 1000 : 0);
                Task completed = await Task.WhenAny(exit, Task.Delay(timeoutMs), cancelTask);
                if (completed != exit)
                {
                    // EOF first, to let Go close the lobby and child helper
                    // itself. Do not forcibly kill until the 4-second grace
                    // period has expired.
                    TryCloseInput(process);
                    bool stopped = await WaitForExitWithFallbackAsync(process, exit, 4000);
                    if (!stopped)
                        return "JOIN_PROBE_STOP_INCOMPLETE：本机子进程未能及时结束，请检查任务管理器。";
                    await Task.WhenAll(stdout, stderr);
                    return completed == cancelTask
                        ? "LOCAL_PROBE_CANCELLED：已请求取消并结束本机连接测试。"
                        : "JOIN_PROBE_TIMEOUT：已终止超时的本机连接探针。";
                }

                await exit;
                string result = await stdout;
                await stderr;
                if (cancellationToken.IsCancellationRequested)
                    return "LOCAL_PROBE_CANCELLED：已请求取消并结束本机连接测试。";
                if (process.ExitCode != 0)
                    return "JOIN_PROBE_IPC_PROCESS_FAILURE：本机探针异常退出，原始日志已隐藏。";
                return ParseMachineOutput(result);
            }
            catch (Exception)
            {
                return "JOIN_PROBE_FAILED：本机探针异常，未显示账号凭据或原始日志。";
            }
            finally
            {
                registration.Dispose();
                if (process != null)
                {
                    TryCloseInput(process);
                    process.Dispose();
                }
            }
        }

        private static void TryCloseInput(Process process)
        {
            if (process == null) return;
            try { process.StandardInput.Close(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (System.IO.IOException) { }
        }

        private static async Task<bool> WaitForExitWithFallbackAsync(
            Process process, Task exit, int gracefulMilliseconds)
        {
            if (await Task.WhenAny(exit, Task.Delay(gracefulMilliseconds)) != exit)
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                if (await Task.WhenAny(exit, Task.Delay(2000)) != exit)
                    return false;
            }
            try { await exit; return true; }
            catch (Exception) { return false; }
        }

        // Copy the reviewed embedded Stage12Worker to the private workspace.
        // A digest-specific directory avoids overwriting a running binary,
        // while the fixed basename is required by the Go safety gate.
        private static string ExtractReadOnlyWorker()
        {
            using (var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream(Stage12OfflineBridge.WorkerResource))
            {
                if (stream == null) return null;
                byte[] bytes;
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    bytes = memory.ToArray();
                }
                byte[] hash;
                using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
                string digest = BitConverter.ToString(hash).Replace("-","").ToLowerInvariant();
                string folder = Path.Combine(GuestEmailAuth.GetWorkspace(),
                    "stage12-receiver-" + digest.Substring(0,16));
                Directory.CreateDirectory(folder);
                string target = Path.Combine(folder,"Stage12Worker.exe");
                if (File.Exists(target))
                {
                    using (var existing = File.OpenRead(target))
                    using (var sha = SHA256.Create())
                    {
                        var got = sha.ComputeHash(existing);
                        bool same = got.Length == hash.Length;
                        for (int i=0;same && i<hash.Length;i++)
                            if (got[i]!=hash[i]) same=false;
                        if (same) {Array.Clear(bytes,0,bytes.Length);return target;}
                    }
                }
                File.WriteAllBytes(target,bytes);
                Array.Clear(bytes,0,bytes.Length);
                return target;
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
