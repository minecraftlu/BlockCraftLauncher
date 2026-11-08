# 第七阶段 · 网易手机本地联机准入资料结构与服务器授权边界

## Windows 真机现状

- 已由用户验证：网易邮箱第二账号通过非官方 Opentransfer 助手生成 Cookie；独立账号本地登录流程退出码为 0。
- 已由用户验证：Stage 6.2 的 `mode_category=missing`，搭配 `uid_valid=yes` / `platform_pe=yes` / `token_present=yes`。这支持 `PE_SESSION_REPORTED_MODE_UNCONFIRMED`，**没有证明真实 G79 模式、房间准入或游戏连接**。
- 最近用户希望连接的手机房间：`280474`，其当前是否在线需实时重新查询。

## 关键源码核对：GetAccess 与真正服务器许可不是同一件事

参考：
- https://github.com/Zhonger-Yuansi/OpenSource-AuthService/blob/main/auth/tan_lobby_login.go
- https://github.com/Zhonger-Yuansi/OpenSource-AuthService/blob/main/cmd/funauth/internal/handlers/phoenix_tan_lobby_login.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/service/dial.go

独立阅读 MIT 开源 FunAuth 的 `auth.TanLobbyLogin` 可以确认：此流程用 `g79client` 查询房间元数据与公开大厅节点映射，结合**当前游戏会话**和新生成的随机数，在本机组装 RakNet 登录参数、加解密密钥、信令 Seed/Ticket，并返回所谓 `TanLobbyLoginResponse`。

**关键纠偏**：这个 `TanLobbyLoginResponse{success:true}` 只说明本地/认证中间层得到了可尝试连接的参数，**不是网易大厅对用户进入房间的接受回执**。该代码本身不会发送 `TanEnterRoomRequest`；后续 `dial.go` 才会用对应参数实际连接大厅，在得到 `TanLoginResponse` 和 `TanEnterRoomResponse` 成功后，才能认为大厅接受入房。WebRTC 建立和 Minecraft 世界加入还在更后面。

## 本次真实实现

1. 新增 `NeteaseRoomLab/RoomAdmissionSchema.cs`：纯 C# .NET Framework 4.8 的**网络隔离** TanLobby 入房参数结构解析，检查 `success`、玩家 UID/房主 ID、RakNet 与信令地址、6 类 base64 二进制字段的存在与安全长度。
2. 错误提示仅包含硬编码字段名，绝不回显、输出或复制服务器返回的 token、密钥、随机数、账号或原始错误文字。
3. `IRoomAdmissionProvider` 是保留给未来合法、用户确认的提供者的接口；当前**未实现任何网络授权适配器**，不会调用任意第三方认证后端。
4. WPF UI 新增「第七阶段 · 准入回执格式自检（离线）」：只用 `.example.invalid` 测试地址和固定合成字节，输出 `LOCAL_ADMISSION_SCHEMA_OK_UNVERIFIED` 和 `ADMISSION_PROVIDER_NOT_CONFIGURED`。
5. Windows 离线测试验证：合法合成回执解析、缺少必要字段、损坏 base64、非法主机地址、模拟拒绝、超长 JSON、错误房间号以及敏感字段不泄露。

以上功能**不需要用户重新登录、不读取 Cookie 文件、不向手机房间发起连接**。WPF Stage 7 可从 GitHub Releases 预览版获取。

## 许可证

FunAuth 项目声明 MIT 许可证，但其内含 `modules/g79client/LICENSE` 是 **GNU AGPL-3.0**。第三方 Opentransfer 也是不同的许可证；目前没有将这些模块合并或捆绑到 BlockCraft EXE。后续如需正式发布独立联机客户端，必须审查上游许可证兼容性及完整源代码义务。

## 后续真正的可玩联机仍缺

1. 对用户自己独立网易账号进行合法游戏登录并获得可用会话，在本机内部安全处理凭据；
2. 结合房间号 280474 的有效元数据与用户会话，构建当前网易版本接受的 RakNet 大厅登录和信令参数；
3. 与正确大厅节点建立会话，**通过服务器真实返回**的 `TanLoginResponse` 和 `TanEnterRoomResponse` 成功码（不是本地 `success:true` 字段）；
4. 得到手机房主 `TanNotifyServerReady` 的 NetherNet ID，再建立授权的信令 WebSocket / ICE / DTLS / SCTP 数据通道；
5. 游戏握手、资源包、账号与 Java/基岩协议兼容，才能真正显示“已进入世界”。

不根据房间号猜测凭据，不请求用户在聊天上传 Cookie/token，不绕过网易登录、实名或准入检查。
