# Stage 12.7 · 网易 PE 游戏协议的离线版本参考检查

**状态：源码与离线测试已实现。没有连接网易游戏世界，没有游戏认证，不能据此发送任何 Minecraft 登录包。**

## 问题背景

Stage 12.6 实机测试里，已获房主允许的网易房间通过大厅入房及 WebRTC 双通道 OPEN；30 秒的可靠/不可靠应用消息数均为 0。Bedrock 连接通常需要客户端先发 `RequestNetworkSettings`（包 ID 193），服务端才会给出 `NetworkSettings`。因此 **双通道 OPEN 且没有游戏消息，不足以证明传输失败**。但国际版发送游戏首包的方式不代表当前网易 PE 接受同一套登录认证；本阶段不向真实房主发送游戏包。

## 已验证的公共资料

- [AllayMC/Protocol README](https://github.com/AllayMC/Protocol/blob/main/README.md) 明确有 `Bedrock_v630_NetEase`、`Bedrock_v686_NetEase`、`Bedrock_v766_NetEase`、`Bedrock_v819_NetEase`。
- [AllayMC/ProtocolExtension README](https://github.com/AllayMC/ProtocolExtension/blob/main/README.md) 说明网易变种可能采用 raw DEFLATE 压缩和网易公钥登录链验证。本项目**没有引入 Java 库或复制其身份密钥**。
- [Minecraft 官方 RequestNetworkSettings 文档](https://mojang.github.io/bedrock-protocol-docs/1.26.50-preview.26/packets/request-network-settings-packet/) 指明初始请求包 ID 193（0xC1），字段为 4 字节大端 `ClientNetworkVersion`；此包由客户端先发。
- 注意 `Bedrock_v859`、`Bedrock_v860` 等标准版本，不等于存在已发布的 `Bedrock_v859_NetEase` 或 `Bedrock_v860_NetEase`；本阶段基于公开、明确命名的网易编解码参考类，不自动推测其未来版本的兼容性。

这些都是*公开第三方兼容性资料*，并不能证明当前网易手机版房间用的是其中任何协议。

## 新代码（不读用户账号，也不访问服务器）

- `BedrockProtocolBridge/internal/neteasecompat/protocol.go` 严格解析 **一条完整、未压缩、没有 RakNet 0xFE 前缀** 的初始 Bedrock `RequestNetworkSettings` 消息：VarUint 长度（必须规范）、请求 ID 193、4 字节大端游戏协议号。拒绝额外字节、多包、子客户端位、非规范 VarUint、超长/截断包、版本为零或异常大、错误 ID 等。
- `InspectClaimedVersion` 仅查固定的公开网易专用编解码实现列表；返回 `NETEASE_CODEC_REFERENCE_FOUND_GAME_NOT_JOINED` 或 `NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED`。所有 `GameIdentityVerified`、`LiveRoomProtocolVerified`、`LoginAttempted`、`JoinedWorld` 都始终是 **false**。
- `InspectLobbyMetadata` 无论输入 48、630、686、766、819、898 还是其他数字，都返回 `NETEASE_GAME_PROTOCOL_UNKNOWN_GAME_NOT_JOINED`；绝不把大厅房间版本号当作游戏协议号。
- `Stage12Worker.exe -offline-codec-reference <纯数字>` 只返回固定状态，不使用 Cookie、房间号、网络资源或游戏数据。不接受含分号、控制字符、命令行拼接等字符。
- WPF 中新增 **Stage 12.7 · 网易游戏协议兼容性参考（离线）** 输入框和按钮。必须主动填写独立证据获得的**游戏协议号**才能查询。它只是查询固定参考表，**不是对当前真实手机房间的版本检测**。UI 不显示不可信子进程输出。
- C# .NET Framework 4.8 与 Go 测试覆盖公开参考版本、普通国际版版本、格式错误、伪造成功、匿名游戏认证及版本来源混淆。GitHub Actions 构建仍是独立的 Stage 12.7 WPF 单 EXE，不会覆盖旧版。

## 状态与下一阶段

| 状态 | 意义 |
| --- | --- |
| `NETEASE_GAME_PROTOCOL_UNKNOWN_GAME_NOT_JOINED` | 没有任何可信的真实游戏协议来源 |
| `NETEASE_CODEC_REFERENCE_FOUND_GAME_NOT_JOINED` | 用户提供的协议号在公开专用编解码表中有参考实现 |
| `NETEASE_CODEC_UNVERIFIED_GAME_NOT_JOINED` | 固定参考表没有明确命名的网易专用实现 |
| `STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED` | 真实 WebRTC 前置条件已满足，但仍缺少网易认可的游戏身份/实际协议版本 |

**后续必须解决的关键问题**是从授权的网易 PE 客户端或其公开协议资料中独立确认实际游戏协议，以及合法的游戏层身份链/服务器验证。单靠大厅房间号、G79 模式、IP/Cookie 或 Stage12.6 的“0 条消息”，无法得出游戏协议版本。只有合法游戏认证和协议匹配都得到验证，才能考虑在房主许可的实际房间进行真正的游戏握手、StartGame、区块同步和交互验收。

本阶段只做离线解析与只读诊断，不发送任何真实 `RequestNetworkSettings` / `Login`，不尝试绕过网易服务器的准入或反作弊限制。
