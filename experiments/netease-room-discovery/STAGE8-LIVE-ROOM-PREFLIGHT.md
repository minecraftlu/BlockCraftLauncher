# 第八阶段：真实第二账号 + 当前网易房间连接参数预检

## 用户最终目标

不安装网易 Windows 电脑版，使用 BlockCraft Launcher 在 Windows 上以**第二个独立账号**加入手机网易《我的世界》自定义联机房间（用户最近的房间号 `280474`）。仍保留原项目主分支，实验功能运行在独立 C# + WPF + .NET Framework 4.8 应用中。

## 这是首次真实会话和真实房间组合预检，不是再次做模拟解析

用户此前已经通过 Opentransfer `connect.exe -login -login-method email` 获取本机 Cookie，并取得有效格式的 PE 游戏令牌输出；其 `auth_mode` 标签在用户所运行的 helper 版本中缺失，所以认证模式仍为未完全确认。

Stage 8 增加**「第八阶段 · 用第二账号预检真实房间连接参数（不入房）」** 按钮。用户必须先选择受信任的 `connect.exe`、在私有目录保留 Cookie、勾选第三方账号安全风险确认，并在每次测试前再次点击 Yes 同意。

点击后程序：

1. 使用与 Stage 6 相同的固定 g79 配置，启动用户**已经选择**的本机 `connect.exe -dump-auth -c <privately stored config>`。此进程需与网易登录服务通信。BlockCraft 限长读取返回的游戏会话数据，仅限进程内临时使用，**从不向 UI、GitHub、诊断日志或剪贴板输出 UID、token、昵称或 Cookie 内容**。
2. 只接受有效格式、UID 非 0、PE 平台标记为 2、16～32 ASCII 字节的临时游戏令牌；`auth_mode` 缺失时保留 `modeCategory=missing`，不会声称 G79 已完全确认。x19/未知模式不允许推进。
3. 在本机发起一次真实 HTTPS 房间只读查询 `POST https://g79mcltransfer.minecraft.cn/room-with-name`，传入用户房间号和**第二账号 UID**；不可从 `uid=0` 查询结果直接推断个人授权。
4. 按返回的房间 `rid` 或名称精确匹配，检查真实房主 `hid`、共享大厅 `srv`、房间是否满员/有密码。然后从网易公开 `transferserver_obt_new.list` 查找当前 `srv` 对应的节点地址及 RakNet/信令端口标记。
5. 根据公开 FunAuth 的连接材料派生流程：在内存中计算游戏 token 的 MD5 (16 字节)，生成新的安全随机 16 字节 RakNet rand 和信令 seed，使用 AES-ECB/PKCS7 生成加密参数及 seed/ticket，并验证形状。**这是为兼容当前社区协议而使用的既有格式，并不表示该老旧加密方式适合新系统设计。**
6. 预检结束后立即清除可清零的中间 `byte[]` 加密材料并删除本机临时配置文件。由于 .NET `string` 不保证所有内存副本可即时清零，仍必须信任用户自行选择的登录助手、Windows 进程和本机环境。

关键源码参考：
- https://github.com/XiaoXiao-2026Max/Opentransfer/blob/main/internal/auth/auth.go（`LoginWithCookie` 和 `sessionCredentials`）
- https://github.com/Zhonger-Yuansi/OpenSource-AuthService/blob/main/auth/tan_lobby_login.go（`GetTransferRoomWithName`、`GetEncryptedToken`、`AesECBEncrypt`、票据创建逻辑）
- https://github.com/Zhonger-Yuansi/OpenSource-AuthService/blob/main/modules/g79client/utils/encryption.go（`GetEncryptedToken` 即 MD5）
- https://github.com/Zhonger-Yuansi/OpenSource-AuthService/blob/main/modules/g79client/utils/aes.go（AES-ECB/PKCS7）

## 用户看到的状态

- `LIVE_PREFLIGHT_NOT_RUN`：还没执行。
- `LIVE_ROOM_SESSION_MATERIAL_PREPARED_NOT_ADMITTED`：第二账号的 PE 会话数据、本次房间元数据和节点均满足校验，临时密码学材料在本机成功生成并释放。**不是**游戏大厅登录成功，不是网易服务器接受入房，也不是连接到手机世界。
- `LIVE_PE_SESSION_INCONCLUSIVE`：助手未提供格式兼容的可用临时会话，或模式明确不是 PE；不展示原始 token。
- `LIVE_ROOM_LOOKUP_INCONCLUSIVE`：服务端当前房间查找无可靠房主和共享节点数据，可能是房间关闭或格式变化。
- `LIVE_NODE_LOOKUP_INCONCLUSIVE`：当前公共节点列表找不到合格记录。
- `LIVE_ROOM_FULL`：元数据提示已满；`LIVE_ROOM_PASSWORD_REQUIRED`：房间需要额外密码，当前程序不采集密码。
- `LIVE_TOKEN_KEY_INCOMPATIBLE`：令牌长度不符合公开示例使用的 AES key 长度，不尝试猜测或绕过。

## 测试和隐私

Windows CI 离线测试只用虚构账号 UID、虚构游戏 token、模拟房间 ID、模拟公开节点记录和确定性随机数；验证 JSON 解析、安全标记、密钥布局、AES 输出长度和数组 Dispose 清理。**Windows CI 不会使用用户自己的 Cookie 或连接网易服务器。**

本预检涉及第三方程序读取账号 Cookie 和真实网络请求，有账号及平台使用条款风险；不应在违反网易账号规则或未经允许的情况下运行。

## 真正的联机尚需什么

即使得到上述连接材料，仍需进行真正的**RakNet 大厅登录**，取得服务端的 `TanLoginResponse` 成功码，发送已授权的 `TanEnterRoomRequest`，取得真实服务器的 `TanEnterRoomResponse` 准入回执，读取房主 `TanNotifyServerReady` 中的 NetherNet ID，然后启动授权的 WebSocket 信令、ICE/DTLS/SCTP 和完整基岩游戏数据协议。

这些功能尚未接通，因此此阶段程序不会向大厅发起入房请求，也不会承诺可以直接用 Java 客户端进入手机房间。用户不需要向聊天发送密码、Cookie、UID、token、实名资料或原始 `-dump-auth` 输出。
