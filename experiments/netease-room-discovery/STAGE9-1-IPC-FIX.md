# Stage 9.1：修复实机 JOIN_PROBE_OUTPUT_INVALID

## 症状及证据

用户 2026-11-08 在 Windows 运行 Stage 9，截图显示新按钮可执行，但结果为 `JOIN_PROBE_OUTPUT_INVALID：输出格式不合法，原始内容已隐藏。`；此前 Stage 8 `LIVE_ROOM_SESSION_MATERIAL_PREPARED_NOT_ADMITTED` 成功，房间 280474 只读查询/节点映射成功。不能因为 IPC 错误声称网易拒绝了登录、房间关闭或已经入房。

## 最可能的原因和可重复消除的缺陷

旧 Go CLI 输出缩进 JSON，`detail` 包含中文；原版 WPF `Process.StandardOutput` 未显式设置 UTF-8 编码，Windows 使用系统默认子进程解码，可能将 UTF-8 中文错误解码成非法 JSON 字符。旧版接收端再用 `IndexOf('{')` 和 `LastIndexOf('}')` 从任意控制台内容截取 JSON，缺乏帧同步、版本识别和原始子进程错误分类。用户实际的原始输出未保存（正确做法：不保存敏感认证相关输出），因此无法证明编码是唯一原因。

## 已修复

1. `cmd/joinprobe/main.go` 增加 `-machine-json`：输出一条带固定前缀 `BLOCKCRAFT_JOIN_PROBE_V1` 的单行 JSON。内容仅为 ASCII 状态枚举和布尔校验字段，不包含原始 `detail`、UID、昵称、token、Cookie、密钥、服务端错误文本或房间连接标识。
2. WPF `JoinProbeRunner.cs` 明确指定 `UTF8Encoding(false, true)`，使用 `ParseMachineOutput` 要求恰好一个版本化记录；多余控制台输出、重复记录、无记录、损坏 JSON、长度超限均以单独的 `JOIN_PROBE_IPC_*` 状态提示，不直接回显原文。
3. 跨进程结果校验 `login_accepted`、`room_admission_accepted`、`host_ready_seen` 的相互关系，且绝对不允许 `minecraft_world_joined=true`；防止本地故障或错误数据被宣传为已经进入真实世界。
4. Go CLI 新增 `-ipc-selftest -machine-json` 纯本机合成测试，不加载 Cookie，不连接任何服务器。
5. GitHub Windows 发布流程在**真正编译** Go EXE 后运行 C# 测试应用，执行 Go 的离线模式并用**生产 WPF 使用的解析函数**校验真实进程输出；不是仅用伪造 JSON 字符串进行测试。
6. 与 Stage 8 的 token 格式保持一致：认证助手令牌可以是 16、24 或 32 字节的 ASCII AES 合法长度；RakNet 参数仍从其 MD5 派生，而不是固定要求 token 必须为 16 字节。新增纯合成凭据测试覆盖三种长度。

## 下载与用户测试

GitHub Releases：`https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/netease-roomlab-stage9-1-preview`。

下载 `BlockCraftRoomStage9_1.exe` 或 ZIP，打开后选择**原来可信的** `connect.exe`、保持原有第二账号 Cookie 文件、手机房间在线，填自己有许可的当前房间号，勾选风险后主动点击一次「第九阶段 · 真实 RakNet 大厅登录并申请入房（一次）」。如果仍有故障，用户只需分享屏幕上出现的**状态代码**（例如 `PE_AUTH_UNAVAILABLE`、`RAKNET_TRANSPORT_FAILED`、`TAN_LOGIN_REJECTED` 或 `JOIN_PROBE_IPC_*`）。不要上传原始 helper 输出或 token。

## 边界

Stage 9.1 修复的是 WPF/Go 进程通信和本地认证令牌格式校验；并未在 CI 或助手侧使用用户私人账号实际入房。只有用户明确同意并在个人 Windows 上运行真实测试，收到服务器 `TanEnterRoomResponse` 成功码，才可认定大厅准入接受。即使如此仍不代表完成 WebRTC 或 Minecraft 游戏世界加入。
