# BlockCraft / 网易手机版房间实验室

这是实验性、独立的 C# + WPF + .NET Framework 4.8 Windows 程序，**不需要安装网易 PC 客户端**。

**当前功能仅为房间元数据诊断，不会登录账号、加入房间或启动 Minecraft。**
它使用社区研究中出现的 room-with-name HTTPS 查询端点，并不代表平台提供了公开第三方接入许可。

## 获取 EXE

1. 打开 [GitHub Actions - NetEase Room Lab](https://github.com/minecraftlu/BlockCraftLauncher/actions/workflows/netease-room-lab.yml)。
2. 选择实验分支上标记为 success 的构建。
3. 在下方 Artifacts 下载 BlockCraft-NeteaseRoomLab-unsigned-win，解压获得 BlockCraft.NeteaseRoomLab.exe。
4. 在 Windows 10/11（需要 .NET Framework 4.8）直接运行。可能出现未签名程序警告，请仅在确认代码和来源后运行。

GitHub Actions 构建成功只证明**编译与离线测试**通过，不证明房间可查到或可连接。

## 从源码编译

要求 Windows + Visual Studio 2022（或 Build Tools），安装 .NET Framework 4.8 targeting pack 和 WPF/MSBuild 构建组件。

在 VS Developer PowerShell 执行：

    msbuild .\experiments\netease-room-discovery\NeteaseRoomLab\NeteaseRoomLab.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU

运行（EXE 不打包网易 PC 客户端或游戏组件）：

    .\experiments\netease-room-discovery\NeteaseRoomLab\bin\Release\BlockCraft.NeteaseRoomLab.exe

离线解析器测试：

    msbuild .\experiments\netease-room-discovery\NeteaseRoomLab.Tests\NeteaseRoomLab.Tests.csproj /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU
    .\experiments\netease-room-discovery\NeteaseRoomLab.Tests\bin\Release\BlockCraft.NeteaseRoomLab.Tests.exe

## 用手机房间进行一次诊断

- 手机进入网易《我的世界》手机版的实际世界，保持自定义房间有效，允许电脑版进入。
- Windows EXE 输入当前房间号（示例：453649）。
- 查询 UID 默认 0，仅用于测试请求格式；它**不是匿名登录**，不保证有查询权限，也不建议填写任何第三方的 UID。
- 点击「查询房间」，仅发出一次 HTTPS JSON POST（有 15 秒超时，无重试）。
- ROOM_METADATA_FOUND 表示找到匹配的房间元数据。**不是加入成功。**
- INCONCLUSIVE 表示查询结果不能判断房间是否存在；可能需要授权会话。
- 点击「本地解析自检」是测试模拟数据，**不会访问网络**，绝不可当作真实房间存在的证据。
- 可复制状态报告，报告不包括账户凭据或其他原始响应。

不要向公开仓库提交 Cookie、sead、sessionid、账号密码、访问令牌或私有会话数据。

## 协议研究（截至 2026-11-08）

| 层 | 已有线索 | 仍缺什么 |
| --- | --- | --- |
| 房间发现 | Opentransfer LookupRoomByNumber，Minecraft-Link room-with-name | 当前房间真实查询结果、身份与可见性规则 |
| 账号认证 | Opentransfer 的独立登录实现 | 兼容当前手机账号的、获授权的 PC 端会话 |
| 房间控制 | Minecraft-Link 标明 POST /online-lobby-room-enter/，未经入房实测 | 服务端认可的请求字段、会话状态及结果验证 |
| 大厅二进制协议 | Opentransfer ProtoEnterRoom = 3 | 游客侧完整编码布局、响应解码和错误处理 |
| 信令传输 | Opentransfer internal/signaling 和 internal/rtc | 从已授权的入房回执取得正确的对端/会话标识 |
| 游戏通信 | ViaBedrock_NetEase 协议转换（开发版） | 进入现有网易自定义房间后端到端验证 |

HTTP 房间 API 与二进制大厅传输不应被假定为同一套进入机制；需要以测试证据厘清二者关系。Mojang 的 NetherNet/WebRTC 文档只作为标准基岩版参考，不代表网易服务器采用完全一致的信令。

参考：
- https://github.com/XiaoXiao-2026Max/Opentransfer
- https://github.com/coferkim/Minecraft-Link
- https://github.com/GanquanOnline/ViaBedrock_NetEase
- https://github.com/Mojang/bedrock-protocol-docs/blob/main/additional_docs/NetherNetOnboardingGuide.md

本实验没有复制以上项目的协议实现源码；若后续引入代码，需要逐个审核许可证与再分发义务。
