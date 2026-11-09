# Stage 12.1 · Windows 双进程 Bedrock 数据通道（离线）

**状态：独立进程 IPC 自测通过，尚非网易真实游戏连接。**

## 为什么需要这个桥

现有 `GuestTransportLab` 为已验证的网易大厅 RakNet / NetherNet / WebRTC 传输栈，依赖旧版 Pion transport/v3。Bedrock 协议处理器使用 `gophertunnel v1.62.0`，其依赖 transport/v4。把两套游戏库强行链接到同一 Go 进程会编译失败。因此采用两个彼此独立的 Go 可执行程序，通过一个**没有网络依赖**的第三 Go 模块传递二进制消息。

## 已实现的可复现验证

- `BridgeWire` 独立 Go module：10 字节固定消息头（`BC12`、版本、类型、4 字节大端 payload 长度），最多 1 MiB 的不透明二进制数据，拒绝不支持的版本、类型、大小或截断帧。
- `GuestTransportLab/cmd/bridgefixture`：只在 `-offline-ipc-selftest -worker <绝对路径>` 参数下工作。启动已编译的本地 `bedrockworker` 子进程（没有 shell），生成一次性随机握手 nonce，经 STDIO 发送一条模拟 Bedrock 数据帧，验证原样收到回声，发送 Stop 并要求子进程正常退出。
- `BedrockProtocolBridge/cmd/bedrockworker`：只有 `-offline-ipc-selftest` 运行模式，验证 nonce / 消息状态，调用真正的 gophertunnel 分包解码器验证无 0xFE 前缀的模拟批次消息，回显二进制帧。不产生玩家身份或世界数据。
- 当前只在 CI 中运行两个 EXE 的往返。不会接收真实网易 Cookie，也不请求 WebSocket、ICE、房间 API 或游戏服务器；输出只有固定状态码 `LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED`，没有任何原始游戏内容、密钥或用户 ID。
- 对公共 `BridgeWire` 运行 10 次单测和 Go `-race`，校验错误输入 / 1 MiB 上限 / 控制消息 / 截断数据。原 `GuestTransportLab` 与 `BedrockProtocolBridge` 的独立测试继续运行。

## CI

[Windows Actions 42499904055](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42499904055)：
- `proven-netease-transport`：通过
- `bedrock-offline`：通过
- `cross-process-offline`：通过（实际编译并启动两个 Windows EXE）

## 尚未完成，不得宣称成功进入世界

当前只有本地模拟批次数据的跨进程回声，**没有把真实 NetherNet 连接挂到工作进程**，也不包含合法网易游戏登录身份提供器、协议版本映射、世界初始化和长期运行验证。下一阶段需逐一验证经过服务器授权的会话和协议兼容性，才能考虑对真实房间启用游戏数据转发。前述 Windows CI 和本地模拟的 `StartGame` / Spawn 测试都不等于网易手机版实际入房。
