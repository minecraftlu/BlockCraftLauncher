# Stage 9 · 真实网易 TanLobby RakNet 大厅登录和入房回执探针

## 用户目标与适用范围

此阶段是 BlockCraft Launcher 的独立实验组件，供持有第二网易邮箱账号且拥有手机房间或经房主明确许可的用户测试六位数网易《我的世界》手机版自定义房间。最近用户房间号 `280474` 仅作为输入默认值；实际联网测试前手机必须重新确认该房间仍在线。

**此工具不绕过网易账号验证、实名、反作弊或服务端入房检查，也不是 Minecraft 游戏客户端。** 使用非官方客户端、社区协议和第三方账号组件可能涉及网易服务条款与账号限制风险。

## 真正的新功能

`GuestTransportLab/internal/joinprobe/join.go` 是完整的**真实网络大厅入房尝试**，而不是 Stage 3/7 的模拟响应：

1. 在本机确认用户已选择独立 `connect.exe` 并且第二账号的 Cookie 文件存在。只向子程序传递本机配置路径，使用 `connect.exe -dump-auth -c ...` 获得短暂的 PE 会话，不向控制台/日志/外部平台输出 Cookie、昵称、UID 或游戏 token。
2. 对第二账号会话做只读房间元数据查询（房间号加第二账号 UID），查真实房主 HID、房间 RID、共享大厅 srv，并核对房间非满员且无额外密码。
3. 从网易公开大厅节点列表匹配 srv 到 RakNet UDP 端点。只对该匹配的单个地址建立连接；没有目标扫描或范围探测。
4. 在内存中生成随机 nonce，以登录令牌的 MD5 和旧协议要求的 AES-ECB/PKCS7 准备 RakNet 登录数据。旧版协议如此使用密码学原语不意味着推荐用于新设计。
5. 使用 MIT 许可的 RakNet 与 TanLobby 协议包，发起一次真正的 RakNet Dial 和 `TanLoginRequest`。**必须收到服务端 `TanLoginResponse.ErrorCode == 0` 才继续。**
6. 登录被服务端接受后启用旧协议要求的 ChaCha8 会话加解密，向用户指定房间发送一次 `TanEnterRoomRequest`。按服务端真实 `TanEnterRoomResponse` 区分同意、关闭、满员、密码错误和未知拒绝。拒绝时不尝试连接游戏。
7. 若返回房间成功码，将状态设为 `SERVER_ROOM_ADMISSION_ACCEPTED`；如果随后收到 `TanNotifyServerReady` 才设为 `HOST_NETHERNET_READY_GAME_NOT_CONNECTED`。测试结束关闭 RakNet 连接，未进入 Minecraft 世界。没有 WebRTC 协商、资源包或 Java 游戏客户端。

## 单 EXE 设计

Windows 构建先用 Go 编译 `BlockCraft.JoinProbe.exe`，再通过 .NET Framework WPF 项目条件式 EmbeddedResource 将其嵌入 `BlockCraftRoomStage9.exe`。WPF 只会在用户主动点击新按钮并再次确认其确实有房主许可后，把内置 Go 程序提取到用户专用 `%LOCALAPPDATA%/BlockCraftLauncher/NeteaseAuthExperiment/helpers/` 子目录；提取文件名包含原始资源 SHA-256 前缀，已存在时检查完整哈希。使用后本机文件可以删除。

在 WPF Stage 9：

- 选择此前独立审查的 Opentransfer `connect.exe`。
- 保留此前在当前 Windows 账号下获取的第二网易账号 Cookie 文件。
- 填写**当前正在手机上开放**且有权限测试的房间号。
- 勾选风险许可后点击「第九阶段 · 真实 RakNet 大厅登录并申请入房（一次）」。
- 程序在点击 Yes 二次确认后才真实联网。它会短暂进入和退出房间（若服务器允许），不会持续在线。

## 隐私与状态判定

`JoinProbeRunner.cs` 只从网络探针的 JSON 结果中读取**固定状态枚举**，使用本地固定说明文字。Go 子程序收到的玩家 UID、原始错误、昵称、会话令牌、加密随机数和密钥均不会写入 UI、剪贴板、GitHub 或日志。子程序 stderr 被丢弃，stdout 以 8 KB 上限处理，不把不在允许列表中的文本显示给用户。

关键状态：

- `PE_AUTH_UNAVAILABLE`：第二账号当前未获得可用 PE 游戏会话。
- `ROOM_LOOKUP_FAILED` / `ROOM_NOT_JOINABLE`：房间信息不完整、关闭或不符合当前预检要求。
- `LOBBY_NODE_MAPPING_FAILED`：共享大厅节点映射失败。
- `RAKNET_TRANSPORT_FAILED`：RakNet UDP 连接没有完成。
- `TAN_LOGIN_RESPONSE_MISSING` / `TAN_LOGIN_REJECTED`：实际大厅登录未获明确成功。
- `ENTER_ROOM_RESPONSE_MISSING` / `ENTER_ROOM_REJECTED`：登录可成功但房间未接受。
- `SERVER_ROOM_ADMISSION_ACCEPTED`：收到真实服务器 TanEnterRoomResponse 成功码。**仅说明大厅准入，不是 Minecraft 世界加入。**
- `HOST_NETHERNET_READY_GAME_NOT_CONNECTED`：服务器已允许入房且房主连接标识已到达，但仍未进行 WebRTC 或游戏登录。

## 验证标准

GitHub Windows CI 使用**纯模拟令牌与房间数据**测试解析、旧协议加密参数派生、账户许可检查、客户端构建和 WPF 结果白名单。CI 不会使用用户 Cookie，不会给网易服务器发送任何包。

**必须由用户在自己的电脑上运行 Stage 9 后，才能知道上述 RakNet/入房阶段是否被网易实际接受。** 若仍失败，请只分享 WPF 安全状态名称，例如 `TAN_LOGIN_REJECTED`，不要上传原始会话 token 或 Cookie。

## 开源依赖和上游出处

此探针调用了社区现有的 MIT 许可子模块，而不是复制不明许可证代码：

- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/tree/main/core/raknet （RakNet MIT）
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/tree/main/protocol （TanLobby 包 MIT）
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/service/dial.go （数据包顺序参考）
- https://github.com/Zhonger-Yuansi/OpenSource-AuthService/blob/main/auth/tan_lobby_login.go （会话与节点参数格式研究参考）

依赖固定到了 `Zhonger-Yuansi/nemc-tan-lobby-solver` 的公开 commit `4dc3f1cdc57455398edb0bbd96b4e54832c6dad9`；原模块名 `Happy2018new` 已无法直接访问，因此 go.mod 通过 replace 指向公开仓库。二进制发布前仍应检查并遵守传递依赖及网易相关软件使用条款。

## 后续才可能进入游戏世界

如果真实回执达到 `HOST_NETHERNET_READY_GAME_NOT_CONNECTED`，下一阶段才需要继续实现已授权的 WebSocket 信令、房主 NetherNet WebRTC 连接、PE 游戏协议握手和 Java/Bedrock 互通。Stage 9 的实机验收先确认每一层是否真正能连接，绝不保证现有网易服务器兼容旧协议。
