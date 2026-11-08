# Stage10.2 · 网易手机版 NetherNet 的 SCTP 子阶段诊断

## 基于用户实机截图

Stage10.1 Windows 实机截图明确返回 `WEBRTC_SCTP_HANDSHAKE_FAILED`。此前同网测试中，真实网易大厅登录、房间准入、房主 NetherNet 就绪、WebSocket 信令均已证实。根据当前上游 `nethernet.Dialer.startTransports` 的执行顺序，只有 `ICETransport.Start()` 和 `DTLSTransport.Start()` 已经返回成功，才会调用 `SCTPTransport.Start()`。因此当前最有价值的故障在 SCTP 建联过程，而不是重新登录账号或尝试更改房间节点。

截图底部的房间 `702795` 是之前的查询记录，无法据此断言当前正在测试的房间号；用户执行新的测试时仍应确保实际输入的房间号与手机当前显示的房间一致。

## 本次源码修改

参考公开源码：https://github.com/pion/sctp/blob/v1.8.39/association.go

上游 Pion SCTP v1.8.39 在 INIT 报文重传耗尽时返回 `ErrHandshakeInitAck`，COOKIE ECHO 报文重传耗尽时返回 `ErrHandshakeCookieEcho`，连接在建立前关闭时可能返回 `ErrAssociationClosedBeforeConn` 或 `ErrAssociationClosed`。这些都是确定的导出错误值，不需要查看私人 SDP/ICE/局域网 IP 或原始异常字符串。

`GuestTransportLab/internal/joinprobe/webrtc_diagnostics.go` 现在先使用 `errors.Is` 检查以上具体错误，以及有上下文包装的 `context.DeadlineExceeded`，**之后**才匹配旧版模糊 `start SCTP` 错误前缀。避免了旧分类逻辑把超时和具体错误统统折叠成 `WEBRTC_SCTP_HANDSHAKE_FAILED`。

对应的安全状态码：

- `WEBRTC_SCTP_INIT_ACK_NOT_RECEIVED`：SCTP INIT 重传耗尽，未收到有效 INIT ACK。需要检查房主游戏是否继续接收 SCTP、是否由客户端兼容性或远端协议栈导致。
- `WEBRTC_SCTP_COOKIE_ACK_NOT_RECEIVED`：SCTP COOKIE ECHO 重传耗尽，COOKIE ACK 阶段没完成。需检查远端关联是否在 COOKIE 阶段关闭。
- `WEBRTC_SCTP_ASSOCIATION_CLOSED`：SCTP 关联尚未成功就关闭；无法直接推断房主主动退出或服务端踢人。
- `WEBRTC_SCTP_HANDSHAKE_TIMEOUT`：真正触发 SCTP 建连超时。
- `WEBRTC_SCTP_HANDSHAKE_FAILED`：上游返回其他尚未分类的 SCTP 错误；保留真实失败而不猜原因。

所有状态均为固定枚举。C# UI 依旧严格核对已有大厅登录、房间准入、房主就绪、信令认证以及 WebRTC 尚未成功等状态布尔位，绝不输出底层网络错误原文、Cookie、密码、游戏 token 或 ICE 私人资料。新增 Go `errors.Is` 测试覆盖包裹的 Pion SCTP typed errors、上下文超时；C# 单测验证未经准入不能显示进阶状态。

## 实机操作及边界

Windows Release `netease-roomlab-stage10-2-preview` 发布后，用同一台电脑和你自己的合法第二网易账号打开 Stage10.2 单 EXE，保留原来的 Cookie/认证助手，确保手机房间在线，主动点击最新 WebRTC 测试按钮一次。只需返回上述固定状态名称；**不要上传原始 ICE、SCTP、SDP 或凭据资料**。

这次是实机故障的可靠**进一步分类**，而不是已经完成游戏握手或修复网易手机端 SCTP 协议。只有新的实机状态明确指出失败子阶段，才有依据选择接下来的版本兼容性研究。探针仍在结束时释放已有的 RakNet、WebSocket 和 WebRTC 资源；`TanLeaveRoomRequest.TeamID` 与房间匹配关系未证实，故暂不发送不确定的离房请求。
