# Stage 10 · 真实网易手机房间 NetherNet/WebRTC 游客传输实验

## 真实起点

用户 Windows Stage 9.1 实机截图显示 `HOST_NETHERNET_READY_GAME_NOT_CONNECTED`，房间 `280474`（截图当时 1/30、房间节点标记 9370）。根据探针严谨的状态机，该结果意味着 `TanLoginResponse` 成功、`TanEnterRoomResponse` 成功及 `TanNotifyServerReady` 房主就绪消息到达。它**不是**进入 Minecraft 游戏世界；Stage10 正是从这里继续。

## Stage 10 实际新增网络操作

在已得到服务器入房成功和房主 NetherNet ID 后，独立游客端在当前仍保持连接的 RakNet 会话期间：

1. 从当前网易公开大厅节点列表中读取对应 `SignalWebPort`，不猜测或扫描端口。
2. 使用第二账号的合法、临时 PE 游戏会话令牌，经 AES-ECB/PKCS7 和随机 16 字节 seed 在本机内存创建信令 ticket。该加密方式来自社区旧版兼容协议，并非新的密码学设计建议。
3. 以**同一个** `TanEnterRoomRequest.NetherNetID` 为游客身份，使用已收到的房主 NetherNet ID，通过 `protocol/service/signaling.Dialer` 访问该节点真实的信令 WebSocket。该协议收到带 ICE/TURN 认证信息的信令回执才表示握手阶段成功。
4. 信令成功后使用 `core/nethernet.Dialer` 发起 ICE offer、候选信令，并在房主回复后建立 ICE → DTLS → SCTP 数据传输。只验证网络协商和数据通道创建，不发送 Minecraft 游戏帧。
5. 不进行重复重试/扫描。一次用户明确同意的调用只有一次认证/大厅入房/房主连接尝试；结束时释放房间连接、WebSocket、Pion 传输及在内存中可清零的 ticket/seed。

参考上游 MIT 代码：
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/service/dial.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/service/signaling/dial.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/core/nethernet/dial.go

## 本地状态（不得误报入房或世界加载）

- `SIGNALING_NODE_UNAVAILABLE`：当前共享节点没有有效 `SignalWebPort`。
- `SIGNALING_TICKET_DERIVATION_FAILED`：本地信令票据无法生成。
- `HOST_NETHERNET_ID_INVALID`：房主就绪消息没有可识别的 NetherNet ID。
- `SIGNALING_CONNECTION_FAILED`：大厅已经批准入房，但是 WebSocket 握手或信令返回 ICE 凭据的阶段失败。
- `SIGNALING_AUTHENTICATED_WEBRTC_PENDING`：已拿到 ICE 凭据，后续尚未完成；这是中间状态。
- `WEBRTC_NEGOTIATION_FAILED`：信令已完成，但 Pion ICE/DTLS/SCTP 尚未完成，不能宣称房主游戏连接成功。
- `WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED`：真实 NetherNet 客户端协商返回成功。**仅代表传输可用，不等于 Minecraft LoginPacket、资源包、区块、玩家实体或世界加载成功。**

所有状态由内置 Go 客户端以 `BLOCKCRAFT_JOIN_PROBE_V1` 的纯 ASCII JSON 单帧发送给 .NET Framework WPF；C# 核对 login/admission/host-ready/signaling/webrtc 等布尔位后才显示相应阶段。其他未在白名单的状态及原始服务器异常文本始终隐藏。

## Windows 运行方式

1. 手机保持你本人开的网易房间在线。房间号经常变化，使用手机当前显示的房间号。
2. 下载 Stage10 GitHub Release 中的单独 EXE 或 ZIP，不需要安装网易 PC 版、Go 工具链、另一个 RakNet 程序。
3. 本机选择原来已审查过的 Opentransfer `connect.exe`，使用第二账号已经保存在当前 Windows 用户私有目录中的 Cookie；**不在聊天提供密码、Cookie、token 或验证码**。
4. 在 WPF 中输入房间号，勾选本人账号及房主许可，点击「第十阶段 · 连接手机房主 NetherNet / WebRTC（一次）」并确认第三方协议风险。
5. 只提供 WPF 出现的**固定状态代码**，绝不提供原始 `-dump-auth` 输出、信令 WebSocket 地址、ICE/TURN 密码或 SDP。

## 构建与准确边界

Windows CI 用 Go 模拟凭据验证 16/24/32 字节临时 AES 信令 ticket 的加解密、各个节点端口字段以及敏感材料清零；运行独立 Go EXE 与 .NET Framework 的 IPC 测试，并验证 WPF 单 EXE 内含 Go 子程序。CI **没有访问用户的真实服务器、Cookie 或私人房间**。

上游原 NetherNet 使用 Pion ICE v4.0.10 + transport/v3 API；本实验将本地 Pion WebRTC 依赖对齐 v4.1.0，以避免后期 v4.4 + transport/v4 的编译接口冲突。

当前 Stage10 不提供 Minecraft Java 客户端转译或 Minecraft Bedrock 资源包/游戏世界完整客户端。完成 WebRTC 只能证明传输连接，后续仍需研究能合规使用的游戏协议客户端；不能绕过网易官方账号实名、风控或服务器许可检查。

授权边界：只针对用户本人开设或已获得房主明确许可的房间使用，第三方协议及认证工具可能有服务条款与账号限制风险。
