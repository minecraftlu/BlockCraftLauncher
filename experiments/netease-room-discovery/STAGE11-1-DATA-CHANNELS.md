# Stage 11.1 · 两条 NetherNet 数据通道只读监测与 30/60 秒观察

## 用户已确认的基线

用户的 Windows Stage10.5 实机截图显示 `WEBRTC_TRANSPORT_STABLE_8S_GAME_NOT_JOINED`，在其本人创建或获明确许可的网易手机版房间中，第二网易账号完成合法大厅登录/入房授权，NetherNet 信令及 WebRTC ICE/DTLS/SCTP 建立后，8 秒内未观察到断开。

**Stage11.1 不修改** Stage10.4 已实机验证的 SCTP CRC32c Castagnoli/LE 协议修补、不改变原来的合法账号验证/房间准入与 Pion 协商，也不向网易手机世界发送任何未验证的游戏包。

## 真正增加的功能

- WPF 中的「第十一阶段 11.1 · 两条数据通道只读观测（一次）」增加 8、30、60 秒选择框，默认 30 秒。8 秒沿用已经实测成功的 Stage10.5 被动存活检测；30/60 秒则进行明确的数据通道测试。运行时房间号、观察秒数及重复入房按钮会锁定，窗口关闭或按「停止连接测试并清理会话」会请求关闭探针。
- NetherNet fork 当前已在 Windows Actions 的临时本地副本内打入锁定、可审计的 CRC32c 线协议修复。Stage11.1 在**同一构建临时副本**加入小型 `blockcraft_channel_observer.go`，对已存在的 `Conn.reliable` 和 `Conn.unreliable` 调用 `ReadyState()`；仅返回是否为 `DataChannelStateOpen`。它不改变 DCEP 握手、DTLS 或游戏消息，也不让应用进入 Minecraft 世界。
- NetherNet 本来已将可靠通道 `OnMessage` 用于 Minecraft 数据包重组，现通过已有只读 `ReadPacket()` 读取完整的远端可靠消息，**只计条数和大小**，随后清除临时缓冲。之前未被使用的不可靠通道增加一个只接收 `len(msg.Data)` 的回调，不读取、显示、保存或重传内容。
- Go 持有 30 或 60 秒可取消观察期，计算两个通道的 `OPEN` 状态、收到的可靠／不可靠消息条数以及字节数，上限分别为每通道 100 万条和 10 亿字节。归档/日志/剪贴板不会包含游戏数据、SDP、ICE、Cookie、账号 UID、session token 或 IP。没有用户提供的游戏协议实现时，零条消息完全可能是正常现象。
- 观察期内远端关闭时返回 `WEBRTC_CHANNELS_EARLY_CLOSED_GAME_NOT_JOINED`；未完成观察窗口返回 `WEBRTC_CHANNELS_OBSERVATION_INTERRUPTED`；到期但有通道未达到 `OPEN` 返回 `WEBRTC_CHANNELS_NOT_OPEN_GAME_NOT_JOINED`。只有两条通道均 `OPEN` 且观察期完整结束才返回 `WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED`、`WEBRTC_CHANNELS_OPEN_30S_DATA_OBSERVED_GAME_NOT_JOINED`，或者对应的 60 秒状态。
- C# 的 Go 子进程 IPC 仅增加通道布尔值以及受限整数计数。对任何“两个通道 OPEN”的结论，WPF 必须同时验证网易大厅/入房/房主就绪/信令/WebRTC 成功、正确的 30 或 60 秒观察标记、消息计数与“有数据/无数据”的状态一致，并且 `minecraft_world_joined=false`。这样即使子进程给出恶意或损坏的 JSON，也不能伪称世界进入成功。
- 为 30/60 秒观察自动延长**有上界**的内部信令/WebRTC 与进程生命周期预算，保留关闭窗口、按取消键或上层超时之后的资源释放。不是无限保持房间在线或自动多次重试。

## 测试范围和限制

- `channel_observation_test.go` 使用本地模拟连接检查可靠消息计数、接收缓冲擦除、不可靠消息长度计数、任一通道未打开、远端提前断开、用户取消。另通过运行时接口断言确认编译后的 NetherNet 依赖确实包含新增只读接口。
- 保留 Stage10.4 CRC32c 错误版本的负控测试、修正版本的实际 INIT 校验测试，以及与标准 Pion SCTP 服务端通过本地内存连接完成握手的离线兼容测试；禁止加入绕过网易服务端授权的代码。
- WPF C# 离线测试覆盖 30/60 秒双通道合法结果、非负且有界的消息计数、伪造入房许可、假在线标志、伪造游戏世界加载及非法时长，CI 编译后还运行 Go→C# UTF-8 机器回执的真实子进程测试。
- CI **不使用真实的网易账号和 Cookie，不向手机房间连接，不会主动给房主发送 Minecraft 测试游戏包**。真实 30 秒/60 秒观测是否成功，必须用户本人在其有权限的手机房间中运行后反馈结果。
- 即使出现 `WEBRTC_CHANNELS_OPEN_60S_DATA_OBSERVED_GAME_NOT_JOINED`，也只能证明两个 DataChannel 的状态和接收消息情况，**绝对不代表 Minecraft 玩家已登录、资源包/区块已经加载或世界进入成功**。

## 使用

Stage11.1 发布目标：`https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/netease-roomlab-stage11-1-preview`，单 EXE 为 `BlockCraftRoomStage11_1.exe`。先保持 Stage10.5 成功构建不被覆盖；新 Stage11.1 作为独立实验版提供。

用户打开新版，选择此前已审查的本地 `connect.exe`，使用已保存的第二账号 Cookie，选择自己开设/已获许可的当前在线房间，选择 30 秒（推荐）或 60 秒并按按钮一次。只需要反馈**固定状态名称与安全消息数**，不要上传 Cookie、SDP、信令地址、ICE/TURN 凭据或原始数据包。
