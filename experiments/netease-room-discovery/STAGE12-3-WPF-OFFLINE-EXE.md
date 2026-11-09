# Stage 12.3 · Windows WPF 单 EXE 本地游戏桥接自检

> **性质：功能已集成，但只验证本机游戏协议桥接。仍不能进入网易手机世界。**

## 本次集成

- 在 `NeteaseRoomLab/MainWindow.xaml` 增加“运行 Stage 12.3 本机桥接自检（离线）”按钮及独立的状态文本，与现有 Stage 11.2 的真实房间/只读 WebRTC 观测区分。
- `Stage12OfflineBridge.cs` 通过 WPF 嵌入资源创建隔离的临时工作目录，启动两个只支持离线自测命令的 Go 程序。经 35 秒上限、取消、状态固定值验证后清理临时文件。不会输出原始子进程回执、网络数据、Cookie、游戏 token 或账户 UID。
- 将 `BlockCraft.JoinProbe.exe`、`BlockCraft.Stage12.BedrockWorker.exe` 和 `BlockCraft.Stage12.IPCFixture.exe` 三个程序打包到同一个 WPF EXE。新版 EXE 运行时自解包测试组件，因此不需要额外下载多个 EXE；但旧的 `connect.exe` 仍是先前真实房间测试的独立可选前提。
- `NeteaseRoomLab.Tests` 新增独立的 Stage12 固定状态测试，确认不把离线 IPC 或子进程任意输出解释为游戏进入成功。
- `.github/workflows/stage12-roomlab-preview.yml` 在 Windows 上重新构建三个 Go 程序、通过 IPC 实际自测、构建 .NET Framework 4.8 WPF、检查确实包含上述三个资源，然后上传独立的未签名 Stage12.3 预览版及 SHA-256 校验和。

## 功能与限制

**新增的是可以双击的 Windows 单 EXE，以及一个离线双进程协议自检按钮。**

下列状态不代表进入世界：
- `STAGE12_OFFLINE_BRIDGE_OK_GAME_NOT_JOINED`：只说明内置两个 Go 工具可以完成本机 IPC 模拟数据收发与正常退出。
- `WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED`：只说明旧版只读网络探针的 WebRTC 数据通道 OPEN；没有 Minecraft 应用层连接。
- `HOST_NETHERNET_READY_GAME_NOT_CONNECTED`：大厅/房主的传输就绪，游戏世界连接尚未建立。

Stage 12.2 已存在的 `ipcrelay.Run` 尚未接入真实联网探针。网易版对游戏级身份链和协议版本的要求尚未验证；不向服务器伪造认证、反作弊判定或入房回执。

此预览版不覆盖此前 Stage11.2 ZIP/EXE 和 GitHub Releases。测试只可在用户拥有或获准进入的房间进行；没有合法身份提供器时不进行游戏层登录。

## Windows CI

工作流：[Stage12.3 WPF offline preview](https://github.com/minecraftlu/BlockCraftLauncher/actions/workflows/stage12-roomlab-preview.yml)。
