# Stage 12.6 · 真实网易连接的只读游戏消息 IPC 验证

**性质：一条可选择启用的真实房间接收路径，不是 Minecraft 游戏登录或网易客户端破解。**

## 与 Stage 12.5 的区别

Stage 12.5 只汇总真实大厅/NetherNet 的证据。Stage 12.6 在真实大厅明确同意入房、收到房主就绪事件、信令和 WebRTC 握手完成以后，允许将 **可靠通道从手机方向收到的应用消息** 通过版本化 STDIO 桥接送到本地内嵌 `Stage12Worker.exe`。所有流量都限定为：

```
网易房主 → WebRTC 可靠通道 → 现有单一 ReadPacket 观察协程
             → 受限本机 STDIO IPC → 只接收工作进程
```

**没有相反方向。** 该工作进程不打开网络 socket，不生成/发送任何 Minecraft Login、PlayStatus、StartGame、移动包或认证凭据；也不将收到的游戏消息回显或记录到文件。没有获得合法游戏层身份之前，不启动原本的双向 `ipcrelay.Run`。

## 实现与保护

- 新增 `GuestTransportLab/internal/ipcrelay/ForwardInbound`。传入接口只提供 `ReadPacket()+Close()`，**在类型层面没有 `Write` 方法**。按消息边界转交，不输出内容，最多 **256 条 / 8 MiB**，单条不超过 1 MiB，随后立即清空临时包缓冲区。取消时关闭网络读端与本机写端，解除阻塞。
- `BedrockProtocolBridge/cmd/bedrockworker` 新增 `-readonly-inbound-ipc`：只在本机 IPC 的 Hello/Ready 随机 nonce 握手后消费 Data 帧，遇到 STDIN EOF 则回复固定 Stopped 控制帧。无游戏身份校验接口，**不解析/假设网易实际 Bedrock 版本**。
- 原 `joinprobe/channel_observation.go` 新增可选 sink，仍由 **唯一的** `ReadPacket` 协程执行可靠通道读取并清空原始消息。默认 Stage 11.1 观察流程原样保留。sink 失败不能被误判为手机服务器拒绝或稳定传输。
- `joinprobe` 新增 `-try-readonly-ipc -stage12-worker <审核过的完整路径>`，要求用户已确认房主许可、`-try-webrtc`、30 或 60 秒窗口、绝对的 `Stage12Worker.exe` 路径。验证全部发生在真实入房之前；无效配置绝不访问房间。
- WPF 新增独立按钮“Stage 12.6 · 真实房间只读 IPC 接收验证（一次）”，必须经用户确认后才会启动。内嵌 worker 使用私有目录中的 SHA-256 固定版本提取，输出中不包含原始数据、Cookie、房间/玩家 ID 或身份材料。
- 新增的机器字段 `stage12_readonly_ipc_verified`、`stage12_readonly_ipc_messages` 由 .NET 端校验，必须与已经校验的入房状态、双通道 OPEN 和可靠消息计数匹配。
- 离线回归新增了**真实 Windows 双进程**只读 IPC 自检（包括接近 1 MiB 的模拟数据），且通过只含读取接口的假源验证不存在向远端写数据的调用。保留先前 Stage12.4 完整模拟 Bedrock 登录/生成自检。

## 运行与解释

1. 从 Windows Stage 12.6 Artifact 下载单 EXE，打开自己的或获得房主许可的网易手机版联机房间。
2. 选中 30 或 60 秒，使用已验证的第二账号会话，按 Stage 12.6 按钮，再次确认非官方客户端测试风险。
3. 如果获得房间准入且两条数据通道 OPEN，同时只读进程完整启动/退出，将显示
   `STAGE12_READONLY_IPC_VERIFIED_GAME_NOT_JOINED`。
4. `stage12_readonly_ipc_messages=0` 也可能是正常情况：仅证明真实房间的接收路径、IPC 控制握手和清理完成，**不证明传输了 Minecraft 游戏数据包**。
5. 出现服务器未入房、房主断线、网络不稳定或本机 worker 错误时，只输出固定分类状态；不重试、不绕过验证、不存储凭据。

## 仍未解决

网易 PE 客户端所接受的游戏认证链/游戏协议版本及真实世界的数据解码和生成玩家流程仍未验证。**本阶段不会进入真实游戏世界。** 未来只有在游戏层身份提供器、房主权限及协议版本全部可合法验证后，才能启用能向网络发送 Minecraft 游戏包的双向桥接器。

这项工作也不能保证官方服务器允许非官方连接；可能触发服务条款限制和离线房间成员残留。

参考：Stage 12.4 离线双向桥接及 Stage 12.5 服务端阶段诊断。
