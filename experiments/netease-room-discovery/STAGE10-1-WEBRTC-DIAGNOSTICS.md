# Stage 10.1：同网络下 WebRTC 协商分阶段诊断

## 用户 Windows + 手机实机现象

用户的网易手机版自建房间能够看到第二网易账号加入房间，但显示为离线并未进入 Minecraft 世界。BlockCraft Stage10 返回 **`WEBRTC_NEGOTIATION_FAILED`**，之前已返回：

- `TanLoginResponse` 实际大厅登录成功。
- `TanEnterRoomResponse` 实际入房成功。
- `TanNotifyServerReady` 房主 NetherNet 就绪。
- `signaling.DialContext` 已返回 ICE/TURN 信令凭据（真实 WebSocket 信令连接成功）。

用户明确确认 Windows 电脑和网易手机版 **在同一个网络**。同网不保证 ICE 连接一定成功：仍可能出现路由器 AP/客户端隔离、Windows 防火墙拦截 UDP、对端没有回传 WebRTC answer/ICE candidates、双方协议版本或 DTLS/SCTP 格式不兼容。

Stage10 只显示笼统失败，无法从这段实机记录区分究竟哪一种；本次不推断具体故障根因。

## 本阶段实际代码

新增 `GuestTransportLab/internal/joinprobe/webrtc_diagnostics.go`，并在原来的真实 NetherNet `Dialer.DialContext` 中启用只观察协议阶段的 `trackedSignaling` 与 `stageLogger`。仅记录：

- 是否向房主发送连接邀请或本地候选。
- 是否收到房主 WebRTC 应答、远端 ICE 候选或远端错误信号。
- 上游实现打印的固定调试消息对应的阶段：收到首个候选、开始 ICE / DTLS / SCTP。

**不记录**信令内容、SDP、ICE 候选 IP 或端口、指纹、ICE/TURN 密码、房主 ID、游戏 token、账号身份信息。尽管这些敏感信息需在内存中用于实际对等连接，UI、GitHub CI、日志和 JSON 状态中仅使用固定的状态枚举。

根据协议错误类型与进度阶段分类（不回显 `err.Error()` 或服务器日志）：

| 状态 | 含义与操作提示 |
| --- | --- |
| `WEBRTC_ANSWER_TIMEOUT` | 已发送 offer，但未及时收到房主 answer；确认手机房间仍在线且允许 PC 加入 |
| `WEBRTC_REMOTE_CANDIDATE_TIMEOUT` | 收到 answer，但未及时收到房主 ICE 候选 |
| `WEBRTC_LOCAL_CANDIDATE_GATHER_FAILED` | Windows 本地 ICE 发现失败，排查网络接口和防火墙 |
| `WEBRTC_ICE_CONNECT_FAILED` / `WEBRTC_ICE_CONNECT_TIMEOUT` | ICE 路由未成功，检查同 Wi-Fi 是否有 AP 隔离、Windows Defender 防火墙和 UDP 限制 |
| `WEBRTC_DTLS_HANDSHAKE_FAILED` / `WEBRTC_DTLS_HANDSHAKE_TIMEOUT` | ICE 已推进，但 DTLS 握手失败 |
| `WEBRTC_SCTP_HANDSHAKE_FAILED` / `WEBRTC_SCTP_HANDSHAKE_TIMEOUT` | DTLS 已推进，SCTP 层失败 |
| `WEBRTC_DATA_CHANNEL_FAILED` | SCTP 已推进，创建数据通道失败 |
| `WEBRTC_REMOTE_REJECTED` | 收到对端的错误/拒绝消息 |
| `WEBRTC_SIGNALING_INTERRUPTED` | WebRTC 协商过程中信令断开 |
| `WEBRTC_NEGOTIATION_FAILED` | 上游版本不支持细分的其他故障，**不猜测根因** |

### 关键约束

- 不更改已经在用户电脑验证成功的真实网易大厅身份认证、服务端入房或房间号/节点映射流程。
- Go CLI 的 `-machine-json` 仍只输出固定 enum + bool，不含私有数据。
- C# WPF `JoinProbeRunner.ParseMachineOutput` 为每个新枚举加入固定中文提示，并严格要求已经真实 `login_accepted=true`、`room_admission_accepted=true`、`host_ready_seen=true`、`signaling_authenticated=true`。没有服务器准入时任何 WebRTC 状态都不会被升级为成功。
- Stage10.1 不尝试扫描、抓包、修改或伪造网易服务器身份凭据，不上传任何 Cookie。
- 探针成功后仍会关闭 WebRTC、信令与 RakNet 会话。**手机列表保留离线账号的现象可能与服务端玩家信息缓存有关，不能仅凭本次源码推断服务器留人原因。** 上游有 `TanLeaveRoomRequest`，但 `TeamID` 与本实验入房流程的对应关系未经验证，所以没有盲目发送可能错误的离房请求。
- WebRTC 成功仅代表传输连接，**没有实现 Minecraft 游戏握手、资源包、地图或世界加入**。

### 测试和后续

Windows GitHub Actions 的 Go 测试覆盖代表性 answer/候选超时、ICE/DTLS/SCTP 错误、远端拒绝、错误取消、固定阶段消息排序以及防止凭据/IP/SDP 回显；C# 测试覆盖各个新状态只能在已允许入房且信令成功的状态下显示。CI 中绝不使用用户真实账号或手机房间。

运行 Stage10.1 单 EXE 后，用户只需把安全的固定状态码（例如 `WEBRTC_ANSWER_TIMEOUT` 或 `WEBRTC_ICE_CONNECT_FAILED`）发回；**不需要日志或原始 ICE/IP 资料**。有了具体阶段才选择是否需要修改本地 ICE 地址策略或 DTLS 数据通道行为，而不是在没有证据时改动全部网络协议。
