# Stage 10.5 · 保留 SCTP CRC32c 实机成功基线的连接稳定性优化

## 已经确认的用户实机结果

用户在 Windows Stage 10.4 使用本人第二网易邮箱账号和获准进入的网易手机版联机房间时，截图显示 **`WEBRTC_TRANSPORT_CONNECTED_GAME_NOT_JOINED`**。它表示网易大厅登录、服务器准入、WebSocket 信令、NetherNet ICE/DTLS/SCTP 与数据传输连接的测试程序成功返回，**不表示 Minecraft 游戏协议握手、资源包或世界已加载**。本阶段刻意不更改已验证的 SCTP CRC32c Castagnoli + LittleEndian 本地构建修正、Pion/NetherNet 协商角色及合法认证流程。

## 可靠性改动

- **被动稳定性窗口：** `holdTransport` 将已建立的真实连接再维持 8 秒，用只读、内容不记录的网络接收检测短时间内的主动断开。收到早期断开报告 `WEBRTC_EARLY_DISCONNECT_GAME_NOT_JOINED`；观察期正常完成才报告 `WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED`。若连接预算或用户取消导致窗口提前终止，不报告稳定。8 秒仅说明短时未观察到关闭，**不等于长期网络稳定或 Minecraft 世界加入**。
- **WPF 主动停止：** 新按钮「停止连接测试并清理会话」将当前 `CancellationTokenSource` 与 WPF 内置 Go 探针的 stdin-EOF 取消协议连接，Go 接收到 EOF 后取消 context、尝试关闭 RakNet/NetherNet 信令与 WebRTC 资源。窗口关闭时也主动取消；不必使用 Windows 任务管理器来停止正常测试。
- **有界退出：** 主机优先等待 4 秒让子进程自行结束；不能结束时才尝试 Kill 并再等待最多 2 秒。程序的退出或超时**不代表服务端立即清除手机号房间玩家列表中的离线条目**，所以只承诺释放本地连接。
- **防止重复操作：** 真实大厅或 WebRTC 探针运行时，禁用发起另一个真实测试、重复邮箱认证、切换房间号、只读房间查询及节点映射；结束后恢复界面可用状态。
- **隐私：** 账号 Cookie、login token、房主地址、SDP、ICE/TURN 认证材料及 WebRTC 载荷不进入日志或界面。编译后的 Go 子进程只通过白名单状态和布尔字段回应 WPF；保留只使用本人或获房主明确许可的房间的弹窗确认。
- **状态严谨性：** C# 校验只有实际大厅准入 + 主机就绪 + 信令建立 + WebRTC 建立 + 8 秒观察标记同时成立才能显示稳定状态。提前断开、取消与超时绝不可误认为成功。

## 离线测试

1. Go `transport_stability_test.go` 使用内存 `net.Pipe` 检验 8 秒窗口机制、提前远端断开、用户取消及已经取消时的处理（以毫秒级窗口模拟，绝不访问真实账号）。
2. C# `TestMachineTransportParsing` 验证不能伪造服务器准入、WebRTC 稳定及 Minecraft 游戏世界加入。
3. 发布流程 Windows CI 实际运行 Go `BlockCraft.JoinProbe.exe -ipc-cancel-selftest -machine-json -cancel-on-stdin-close`，通过真实子进程 stdin EOF 发送取消，再由 .NET Framework WPF 的正式解析函数验证 `LOCAL_PROBE_CANCELLED` 安全回执与正常进程退出；不会访问任何网易服务器或读取 Cookie。
4. CI 仍保留 Stage 10.4 核心 SCTP CRC32c 的**负控测试与正向互通测试**，确保新的稳定版不能重新引入旧版 INIT ACK 失败。

## Windows 单 EXE

GitHub 预览发布页 `https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/netease-roomlab-stage10-5-preview`。文件为 `BlockCraftRoomStage10_5.exe`、同名 ZIP、`SHA256SUMS.txt`。仍需用户在合法房主许可场景下进行实机测试，才能确定 8 秒稳定窗口在具体的网易手机版上是否通过。

**本阶段不是 Minecraft 游戏客户端**；尚未实现资源包协商、世界加载、玩家交互或游戏渲染。不会绕过网易账号、实名或服务器授权。
