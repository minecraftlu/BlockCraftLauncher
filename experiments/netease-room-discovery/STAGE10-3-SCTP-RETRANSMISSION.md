# Stage10.3 · 修正 NetherNet forked SCTP 的握手重传时限与错误类型

## 最新可确认的用户问题

用户 Windows Stage10.2 截图显示房间 `643871`、`WEBRTC_SCTP_HANDSHAKE_TIMEOUT`。此前本人第二账号已通过网易真实大厅登录和服务器入房授权，手机房间列表能看到该账号，但未进入世界。用户确认手机与 Windows 电脑处于同一网络。Stage10.1 已证实 WebSocket 信令和 ICE/DTLS 进入后续 SCTP 阶段。

**重要区别：** 旧报错的 `WEBRTC_SCTP_HANDSHAKE_TIMEOUT` 是本地 `context.DeadlineExceeded`，并不是网易房主明确拒绝 SCTP INIT 的服务器回执，也不能仅凭此证明手机端不会使用 SCTP。

## 本次发现并修复的两个代码问题

### 1. SCTP 类型来自错误的依赖

上游 `nemc-tan-lobby-solver/core/webrtc/sctptransport.go` 调用的是
`github.com/Happy2018new/nemc-tan-lobby-solver/core/sctp.Client`，不是标准 `github.com/pion/sctp.Client`。前一版 Stage10.2 在 `errors.Is` 中错误地使用标准 Pion SCTP 错误类型，使得某些**真正的** INIT ACK/COOKIE ECHO/关联关闭错误无法匹配。

Stage10.3 的 `webrtc_diagnostics.go` 和 `webrtc_diagnostics_test.go` 改为导入同一个 forked `core/sctp`，按其实际的 `ErrHandshakeInitAck`、`ErrHandshakeCookieEcho`、`ErrAssociationClosedBeforeConn` 和 `ErrAssociationClosed` 分类。Go 离线测试用**真实相同包实例**检查包装错误 `errors.Is`，不需要用户上传原始日志或密钥。

### 2. WebRTC 探针超时比 SCTP 原生握手重传更早

上游 forked `core/sctp/rtx_timer.go` 定义 `rtoInitial=1000ms`、`maxInitRetrans=8`、默认 `rtoMax=60000ms`。INIT/Cookie 的重传间隔按指数退避增长，完整原生失败有可能在原来的 **45 秒**信令/ICE/DTLS/SCTP 共享时限之后才返回，因此此前频繁出现 `context.DeadlineExceeded` 而不是具体 `ErrHandshakeInitAck` / `ErrHandshakeCookieEcho`。

Stage10.3 通过 NetherNet fork 自己的 `core/webrtc.SettingEngine.SetSCTPRTOMax(3*time.Second)` 对**当前授权的单次探针**设置有界 3 秒 RTO 上限；**保留上游 8 次重传次数，不修改 SCTP INIT/COOKIE、DTLS 身份验证或网易服务器认证数据**。按理想无阻塞条件，SCTP 首次重传及后续 8 次重传的等待时间由长期指数回退降到约数十秒；真实网络调度、ICE/DTLS 开销仍会影响实际所需时间。

同时将信令+ICE+DTLS+SCTP 子会话预算由 **45 秒调整到 70 秒**，用户批准的单次 CLI 总预算由 **90 秒调整到 105 秒**；WPF 已有 115 秒本地进程超时保持不变。超过上限则明确返回新的 `WEBRTC_SCTP_PROBE_DEADLINE`，表示**外部测试时限**到达，不假装是从 SCTP 收到失败消息。

## 本阶段目标与限制

这属于**根据真实上游代码进行的重传时序兼容性尝试**，不仅改变 UI 文案。它可能让 NetherNet 传输更快得到有效连接或准确的 INIT/COOKIE 错误；但也可能仍无法与网易手机版现有 SCTP 实现互通。没有任何用户实机 Stage10.3 成功结果，不能宣称连接成功。

所有 Go-C# IPC 仍只允许固定 ASCII 状态及布尔值；不显示 Cookie、密码、UID、游戏 token、房主网络标识、私有 IP/ICE 候选、SDP、SCTP 原始错误或房间主机地址。真正服务器入房许可仍是强制前提；不会因这次重试参数修改而绕过网易账号认证、实名和服务器许可。

## 用户验证

下载实验分支的 Stage10.3 GitHub Release `BlockCraftRoomStage10_3.exe`，使用原来已审查的 `connect.exe` 和本地第二账号 Cookie，只对自己创建或已获房主许可的当前在线手机房间主动执行一次 WebRTC 按钮。

预期输出之一可能是：

- `WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED`：已建立 WebRTC，但仍**没有发送 Minecraft 游戏握手或加载世界**。
- `WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED`：真正 forked SCTP INIT 退避重传失败。
- `WEBRTC_SCTP_COOKIE_ACK_NOT_RECEIVED`：真正 forked SCTP COOKIE 退避重传失败。
- `WEBRTC_SCTP_ASSOCIATION_CLOSED`：真实关联提前关闭。
- `WEBRTC_SCTP_PROBE_DEADLINE`：外部有界测试预算用尽，不能据此认定手机端一定没有响应。

只需报告**屏幕上的最终固定状态代码**，不要提供原始 SCTP、SDP、ICE 信息或账号文件。是否能进入 Minecraft 世界仍需要后续合法完整游戏客户端协议实现，现有网络探针不能做到。
