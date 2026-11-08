# Stage 12.0 · Bedrock 游戏协议传输适配（**仅离线验证**）

> 本阶段不宣称网易手机版联机成功，不会绕过网易房间授权或游戏会话认证。

## 已证实的接口差异

- Stage 11.1 NetEase NetherNet 分支返回一个已协商的 `net.Conn`，实现 `ReadPacket()`，并通过原有 `PacketHeader() (0,false)` 表达“无 RakNet `0xfe` 批次头”。
- 固定依赖 `github.com/sandertv/gophertunnel@v1.62.0` 的解码器使用较新的 `packet.BatchHeaderer`、`packet.EncryptionDisabler`、`packet.PacketReader`；直接把旧 `net.Conn` 交给新库会默认附加 `0xfe`，不能据此判定手机主机不兼容。
- `internal/gamebridge.PacketConn` 显式提供三种能力：`BatchHeader() == nil`、`DisableEncryption() == true`、`ReadPacket()` 原样读取完整可靠通道批次。真正的 DTLS、信令、房间授权仍由原来已验证的实现管理。

## 新增的可复用代码

- `gamebridge.NewAttachedNetwork(conn)` 实现 `minecraft.Network`，只允许以固定本地标记认领**已经存在**的单个连接；禁止 Ping、监听以及第二次/任意地址拨号。取消上下文会关闭该连接。
- `gamebridge.DialAuthenticated(ctx, conn, dialer)` 仅提供后续接入点。如果未提供经过相应平台认证的游戏身份提供者（`TokenSource` 或 `XBLClient`），立即返回 `ErrNoGameIdentity`，不会用 PE 大厅会话、手填姓名或离线 JWT 冒充官方游戏身份。**当前 Stage 9–11 不会调用此函数**。
- 测试在进程内利用 `net.Pipe` 验证字节级收发、消息边界、单次认领、取消关闭、拒绝隐式离线身份。无外部网络、网易账号、房间探测或 Cookie。

## 目前尚未打通

1. 网易版房间当前使用的 **Bedrock 游戏协议版本** 未确认；公开库默认协议不一定兼容。
2. 网易 PE 会话与 Bedrock 游戏级身份令牌/签名之间的合法映射尚无可信实现；标准 Xbox 游戏认证不一定适用于网易版。
3. 还未验证真实服务端的 `RequestNetworkSettings → NetworkSettings → Login → PlayStatus`、资源包、`StartGame`、`PlayerSpawn` 等响应。
4. 即使游戏握手通过，也需要正常处理区块流、同步、可靠退出、版本升级及实际用户游戏交互。

**验收口径**：必须在用户有权进入的真实手机房间，收到合法游戏层登录成功、完整世界初始化并产生实际玩家交互后，才能称为加入世界。WebRTC DataChannel OPEN、空闲观察 60s、手机列表中的离线玩家记录均不是该标准。

## 版本与回归

基础分支为 `fix/netease-channel-state-monitor-20261108`，保留 Stage10.4 CRC32c 和 Stage11.2 稳定性修复。使用 Windows GitHub Actions Go1.26.5 执行 `go mod tidy`、`go test ./...` 和 Windows EXE 构建；编译通过不等同真实网易兼容性测试。

参考上游：
- https://github.com/Sandertv/gophertunnel/blob/v1.62.0/minecraft/protocol/packet/transport.go
- https://github.com/Sandertv/gophertunnel/blob/v1.62.0/minecraft/dial.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/4dc3f1cdc57455398edb0bbd96b4e54832c6dad9/core/nethernet/conn.go
