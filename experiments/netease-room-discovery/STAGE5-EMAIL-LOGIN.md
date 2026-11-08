# BlockCraft 第五阶段：独立网易邮箱登录辅助（实验）

目前已确认用户拥有第二个网易账号，登录方式为网易邮箱。目标是在 **不安装网易电脑版** 的前提下，由 Windows 电脑作为独立游客尝试加入手机自定义联机房间。

## 已有真实测试证据

- `ROOM_METADATA_FOUND`：此前实机查到房间 453649，1/30 人。
- `NODE_METADATA_FOUND`：此前实机匹配节点 9366。
- `LOCAL_WEBRTC_DATA_CHANNEL_OK`：Windows 本机模拟房主与游客完成 ICE/DTLS/SCTP 数据通道通信。
- `LOCAL_TANROOM_CODEC_OK`：入房协议的**模拟消息**能够进行编解码；未向网易服务器发出入房请求。

## 本次新增功能

Windows WPF 实验工具可由用户**手动选择**已经独立核对来源的 `connect.exe`，显示它的 SHA-256 指纹。只有在勾选第三方程序风险确认后才允许启动。

启动参数固定为：

```text
-login -login-method email -cookie-out <本机私有输出文件> -auth-data-dir <本机私有设备记录目录>
```

BlockCraft **不会**要求用户在 WPF 界面填写邮箱、密码、短信验证码或实名信息，不接管第三方助手的输入输出，也不会读取、复制或上报 Cookie 内容。邮件地址、密码及验证交互由单独打开的 `connect.exe` 控制台负责。

这是**用户自选第三方认证组件**：它可能读取并处理账户凭据，不属于网易官方账号登录接口。不能假定该程序安全、得到网易官方授权或兼容当前账号；选择并运行前必须自行检查其源码、发布来源、哈希和许可。**本实验不捆绑、不自动下载第三方二进制程序。**

参考（非官方实现）：
- https://github.com/XiaoXiao-2026Max/Opentransfer
- https://github.com/XiaoXiao-2026Max/Opentransfer/blob/main/docs/login.md
- https://github.com/XiaoXiao-2026Max/Opentransfer/blob/main/cmd/connect/main.go

公开实现支持 `-login -login-method email`，在控制台提示用户邮箱、密码及必要的账户验证。它使用 PolyForm Noncommercial 1.0.0 许可，BlockCraft 这里只调用用户主动选择的独立可执行程序，没有复制或打包其源码与二进制。

## 真实 Windows 操作

1. 从 GitHub Actions 获取成功构建的 `BlockCraft.NeteaseRoomLab.exe`。该 EXE 为 C# / WPF / .NET Framework 4.8，文件本身不包含网易 PC 客户端。
2. 自行检查是否信任 Opentransfer 的发布件或自己从源码构建，不要运行来源不明的 `connect.exe`。
3. 在 WPF 实验室点击「选择 connect.exe」，查看 SHA-256 指纹，确认来源。
4. 勾选风险确认，然后点击「启动网易邮箱登录（本机）」。只在本地新弹出的控制台完成第二个账号的登录。不要把任何账号信息发到聊天或 GitHub。
5. 程序会根据第三方进程退出码和本地 Cookie 文件是否出现输出 `AUTH_HELPER_EXITED_WITH_ERROR`、`AUTH_HELPER_EXITED_NO_CREDENTIAL` 或 `LOCAL_CREDENTIAL_FILE_PRESENT_UNVERIFIED`。**仅文件存在不代表账号有效、通过实名认证或获得入房资格。**
6. 「删除本地 Cookie 文件」只删除当前 BlockCraft 实验目录的凭据输出文件，不清除第三方助手自行保存的设备记录。

输出文件仅位于当前 Windows 用户的 `%LOCALAPPDATA%/BlockCraftLauncher/NeteaseAuthExperiment` 目录（本实验尝试设置用户和 SYSTEM 专用 ACL）。这不消除第三方工具对凭据的读取风险。

## 实际入房尚缺

完整游客连接流程有公开参考（https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/service/dial.go），但是需要已授权的 `GetAccess(roomID)` 会话返回大厅登录和信令票据。

这里不使用模拟数据伪装实际授权，也不获取或上传用户的私有 Cookie。当前 WPF 实验室**不会向网易服务器发送游客入房请求**。需要平台允许且经过合法身份验证的独立会话，才能进一步实现真实入房和 Minecraft 游戏连接。

**账号风险提示：**网易相关服务协议可能限制或禁止未经认可的第三方登录、反向工程或客户端修改。使用非官方账号助手可能违反服务条款，存在账号限制或封禁风险。本项目不会绕过平台实名、反作弊或登录权限。

## 验收要求

- CI 编译 WPF 和模拟登录助手参数检查成功；没有进行真实登录网络测试。
- 程序界面不能将“文件已保存”显示为“账号认证成功”。
- 只有获得平台接受的真实入房回执后才能显示 `ROOM_JOINED`；只有收到来自房主游戏协议的有效数据后才能显示 `GAME_CONNECTED`。
