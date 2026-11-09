# Stage 12.2 · 有界双向 IPC 与连接生命周期

**结论：Windows 离线双向传输及稳定性检查通过；并没有进入真实网易手机版世界。**

## 技术实现

- `BridgeWire/stream.go` 实现消息型 `PacketStream`。每方向逐帧、同步写入，避免多个生产者混写；payload 最大 1 MiB。只在 Hello/Ready 之后使用，且将明确的 `Stop` 与非预期 EOF 分开，不把断线当成正常退出。
- `GuestTransportLab/internal/ipcrelay/relay.go` 提供可重用的双向转发核心，将已拥有的 `ReadPacket/Write/Close` 传输接口与两个本地可关闭的流桥接起来。在取消、子进程异常退出、网络中断或异常短写入时关闭所有拥有的端点，并等待转发 goroutine 退出。没有新增拨号、网易身份、游戏数据日志或 WPF 正式入口。
- `BedrockProtocolBridge/cmd/bedrockworker` 的**离线**二进制分帧验证已改为持续读写的 PacketStream。身份认证与 Minecraft 游戏协议仍只在分离的本地模拟测试中验证。
- `GuestTransportLab/cmd/bridgefixture` 利用真实 Windows 子进程，通过 STDIO 连续发送不同大小的模拟 Bedrock 包（包括接近 1 MiB 上限的帧），验证逐包返回，并要求明确的 Stop/Stopped 正常结束。仍然不能接受真实网易房间参数。
- `internal/ipcrelay/relay_test.go` 使用内存消息传输测试真实的双向并发转发、取消前竞态清理、意外 EOF、超长数据、主动 Stop/Stopped 和短写入错误。不会发起公网流量。

## 验证

[Windows CI #42501750436](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42501750436) 三任务均通过：
1. `bedrock-offline` — 本地 Bedrock 登录与生成流程的重复测试、race/vet/build。
2. `proven-netease-transport` — 旧 NetherNet/SCTP/RakNet 链路不回归。
3. `cross-process-offline` — BridgeWire 多次测试、竞态检测；新 ipcrelay 的 race 测试；两个真实 Windows EXE 的 10 轮进程启动、双向模拟数据及正常退出。

## 下一步与当前边界

1. **未实现**真实网易游戏身份授权的可核验提供者、网易专用协议版本判别、服务器游戏入房回执及游戏世界资源流。国际版 Microsoft/XBL 身份不等于网易 PE 游戏入房身份。
2. **未接线**：`ipcrelay.Run` 虽可接受已建立的消息型连接，但**没有被 Stage 9/11 真人探针调用**；使用者的 WPF UI 仍只能显示传输层状态，不能宣称实际进入世界。
3. **未实测**真实游戏资源包的大帧分布；1 MiB 上限是安全的实验限值，不代表生产环境的兼容上限。
4. 实际启用前须使用合法账号及允许的房间，通过服务端认可的游戏身份和完整协议验证。禁止伪造授权、重放别人的令牌或绕过反作弊。

官方 NetherNet 协议与身份断言参考：
https://mojang.github.io/bedrock-protocol-docs/guides/nether-net-onboarding-guide/
