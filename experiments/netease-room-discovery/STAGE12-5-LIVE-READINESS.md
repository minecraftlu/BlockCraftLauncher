# Stage 12.5 · 真实网易房间“游戏连接前置条件”证据检查

**本阶段的功能性质：真实服务器/NetherNet 连接状态的只读汇总；不执行 Bedrock 游戏登录、不读取或发送游戏内容、不授予任何额外权限。**

## 背景

在用户本人已经验证的 Stage 12.4 预览版中：
- 两个 Windows 子进程通过本地有界 IPC 成功完成模拟的 Bedrock Login、StartGame、PlayerSpawn；
- 用户明确许可的网易移动版房间在 Stage 11.1 探针中曾出现双通道 OPEN、30/60 秒计数为 0；
- 这些结果分别证明离线游戏协议可运行、以及 WebRTC 传输已建立，**并未证明两个系统已经在同一个实际游戏会话中握手**。

另外，独立的 Stage 7 合成准入模式报告 `ADMISSION_PROVIDER_NOT_CONFIGURED` 并不自动否定 Stage 9 真实的 TanEnterRoomResponse；应当分别看服务器实际准入、传输连接、以及游戏层身份。

## 新增诊断结果

Go `GuestTransportLab/internal/stage12readiness` 通过 *已经读取的服务端回执/网络检查布尔值* 计算固定状态，无新网络请求：

| 状态 | 目前缺少的证据 |
| --- | --- |
| `STAGE12_ADMISSION_NOT_VERIFIED` | 没有实际大厅登录和房间准入成功回执 |
| `STAGE12_HOST_NOT_READY` | 没有已确认的房主 NetherNet 就绪事件 |
| `STAGE12_SIGNALING_NOT_VERIFIED` | 没有可靠的信令成功证据 |
| `STAGE12_TRANSPORT_NOT_CONNECTED` | 没有 WebRTC 已连接证据 |
| `STAGE12_TRANSPORT_NOT_STABLE` | 没有完成所选时长的稳定性检查 |
| `STAGE12_DATA_CHANNELS_NOT_VERIFIED` | 尚不能证明可靠和不可靠通道都 OPEN |
| `STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED` | 传输前置条件全部满足，但**游戏身份链提供者、游戏协议版本仍未确认** |

重要边界：
1. `auth_mode=g79`、`g79_mode_confirmed`、PE 会话格式、房间元数据 `version` 以及 `TanEnterRoomSuccess` 都**不是** Minecraft 游戏层登录身份授权，也不是 Bedrock protocol number。
2. 即使最后一行全部满足，当前探针仍明确设置 `stage12_game_identity_verified=false`、`stage12_game_protocol_verified=false`、`stage12_game_login_attempted=false`、`minecraft_world_joined=false`。
3. WPF 不直接信任子进程上报的 Stage 12 状态：它独立从其它已经校验的服务器/网络布尔值重新计算。任何伪造的游戏认证、版本认证、开始登录声明都会被拒绝，且不输出原始私有数据。
4. 旧版探针可能不包含新字段；为保持旧 IPC 兼容，WPF 只在检测到新字段时显示 Stage 12.5 诊断，原有状态始终不冒充游戏成功。
5. 不能把新的国际版 NetherNet 合作伙伴协议直接视为网易历史版协议。Mojang 文档中的身份签名方式和 `GET /v1/join` 版本信息仅供研究设计，不代表网易移动房间提供这些接口。

## 验收

- 通过 Go 单元测试及 `go test -race`；确认所有服务端证据组合最终最多只能达到 `STAGE12_GAME_IDENTITY_AND_PROTOCOL_UNVERIFIED`，绝不能输出“进入世界”。
- 通过 .NET Framework 4.8 解析回归：只接受一致的 fixed status，拒绝游戏认证/版本认证造假，且离线 `room_version=898` 不能被解释为 Bedrock protocol。
- 通过真实编译的 Go EXE → .NET WPF IPC 自检，确保新版状态从子进程传到 UI。

## 下一步真实联机问题

**必须先确认合法游戏身份提供器与网易 PE 实际 Bedrock 协议版本。** 在此之前不应向房主发送伪造登录链、跳过反作弊或使用国际版 Xbox 令牌冒充网易账号。完成合法认证兼容后，才能在获得房主许可的房间里把已建立的 NetherNet 会话交给 Stage12 的游戏协议工作进程，并以真实 StartGame / 生成玩家 / 区块加载 / 移动交互及稳定断线清理作为最终验收条件。

参考：https://mojang.github.io/bedrock-protocol-docs/guides/nether-net-onboarding-guide/
