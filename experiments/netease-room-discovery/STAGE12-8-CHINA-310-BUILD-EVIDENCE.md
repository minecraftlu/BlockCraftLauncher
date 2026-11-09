# Stage 12.8：网易 3.10 客户端版本证据与 Bedrock 897/898 候选

**结论：已实现不联网的版本证据对照，不等于确认当前手机房间的游戏协议，更不等于网易游戏身份认证/世界连接。**

## 截图证据的真实范围

用户提交的是一张带 Windows 标题栏的网易《我的世界》“设置 → 其他”截图，其中分别显示：

- 客户端版本号：`3.10.7.301588`
- 补丁版本号：`3.10.9.301882`

这些是**网易客户端构建编号**。截图不能证明手机房主也运行同一版本，更不能证明网络应用层的 Bedrock protocol number。大厅元数据里的 `room_version`、房间号、G79 平台会话也都不是游戏协议号。所有登录/身份状态继续是 `false`。

## 公共资料来源与冲突（2026-11-09）

1. [China Edition — Minecraft Wiki](https://minecraft.wiki/w/China_Edition) 将公开的中国版 **3.10 系列**与 Bedrock **1.21.130** 关联；不保证 3.10.7 各平台使用相同的底层游戏协议。
2. [AllayMC/Protocol README](https://github.com/AllayMC/Protocol/blob/main/README.md) 将标准 `Bedrock_v897` 列为 1.21.130–1.21.132；该表**并无** `Bedrock_v897_NetEase` 或 `Bedrock_v898_NetEase` 的明确实现条目。
3. [Minecraft Wiki — Bedrock protocol numbers](https://minecraft.wiki/w/Minecraft_Wiki:Projects/wiki.vg_merge/Bedrock_Protocol_version_numbers) 将 **1.21.132 正式版**列为 898，同时列出 1.21.130 预览版的 897；公开资料存在“预览/正式版及编解码版本表”的差异。
4. [AllayMC changelog](https://docs.allaymc.org/changelog/) 的 **0.15.0 Unreleased** 说明网易 v860（1.21.124）的支持正在开发；这**不是** 897/898 的公开网易实现，也不能视为已经发布可用。

**因此仅保存两个标准协议候选：897、898。** 它们不是可直接发给网易的游戏登录协议。Stage 12.7 输入 819 查到公开网易编解码实现，不代表 819 是当前版本；同样不能把标准 897/898 直接当网易版。

## 新增离线实现

- `BedrockProtocolBridge/internal/neteasecompat/build_family.go`：接受明确输入的 `3.10.x.六位构建号` 和补丁构建号，返回固定的 `STAGE12_8_CHINA_310_REFERENCE_CANDIDATES_UNVERIFIED_GAME_NOT_JOINED`。其他系列、错误输入或大厅版本号返回 UNKNOWN。任何结果的 `ActualPhoneVersionVerified`、`ActualNetEaseProtocolVerified`、`GameIdentityVerified`、`LoginAttempted`、`WorldJoined` 均为 **false**。
- `bedrockworker -offline-china-build-reference <游戏版本> <补丁版本>`：无账号读取、无网络套接字、没有 Bedrock 登录；只把固定状态码写到 stdout，不回显输入。
- WPF 增加 **“Stage 12.8 · 网易 3.10 客户端版本证据对照（离线）”**，默认展示已经由用户提交的 Windows 截图版本。输入可改为自己拥有的其他客户端构建，但功能不识别房主手机真实协议。返回 UI 状态 `STAGE12_8_CHINA_310_CANDIDATES_897_898_UNVERIFIED_GAME_NOT_JOINED`，并解释公开来源的冲突。
- 原有 Stage 11 入房只读探测、Stage 12.3–12.7 离线测试和 Stage 12.6 真房间的“只从手机向本机接收”路径均保留。Stage 12.8 绝不启用真实双向游戏数据发送。
- 单元测试包括：正确识别截图构建、拒绝账号/命令注入和不同系列、任何大厅版本不能提高信任等级、未发布 860 不作为已发布网易编解码证明、.NET 子进程伪造输出不能升级成真实成功。
- Windows Actions 构建一个独立 Stage 12.8 WPF 单 EXE（不覆盖原 Stage 12.7）。

## 离真正进入手机网易世界仍缺什么？

**最有价值的下一条证据是房主手机自身独立显示的 Bedrock 协议号，或经过所有者授权获取的、已脱敏的初始 `RequestNetworkSettings` 协议字段。** 两种证据都必须标记来源及是否是手机房主本人；不能靠当前 Windows 版本号或 30 秒 0 条消息推断。

即使真实协议号被确认，仍需要对应的**网易专用**游戏编解码与服务器认可的合法游戏身份签名链；未经这些证据和房主许可，不会向网易真实房间发送 `Login`，不会伪造或重放他人的 token，也不会绕过反作弊或接入权限。

**最终验收仍然是手机房主能看到真实玩家进入、双方能移动/交互/稳定退出，而不是离线 IPC 通过。**
