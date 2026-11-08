# 网易手机版自定义房间独立联机研究记录

日期：2026-11-08。本记录仅写入用户授权分享的实验结果，不保存账号密码、Cookie、sessionid、sead 或网络会话。

## 在真实 Windows 客户端得到的结果

- 用户的网易手机版自定义联机房间已经进入游戏世界，允许电脑版玩家进入。
- BlockCraft NeteaseRoomLab C# / WPF / .NET Framework 4.8 独立 EXE 成功通过 HTTPS 读取房间资料。
- 回应状态：ROOM_METADATA_FOUND。
- 房间号和 rid：453649；name：453649；人数：1/30；version：48；platform：2；srv：9366。
- 无需安装网易 PC 游戏客户端，不使用网易账号认证。以上仅证明房间元数据查询成功。
- version=48 不可直接当成 Minecraft 游戏协议号；srv=9366 不可直接当作游戏端口。

## 第二阶段：房间节点标记映射

Opentransfer 的 internal/netease/netease.go 引用了公开大厅节点列表：https://g79.update.netease.com/transferserver_obt_new.list

新的 WPF 实验程序允许在房间真实查询成功后，对该列表做一次只读 GET，尝试把房间 srv=9366 与公共节点记录的 id 进行比较。

- NODE_METADATA_FOUND 表示能够从公开列表匹配到该节点的记录；不代表已加入房间。
- NODE_MAPPING_INCONCLUSIVE 表示无法确定节点映射；不能断言房间下线。
- 不主动向大厅节点、手机设备或游戏世界建立会话；不提交游戏登录凭据。
- 该网络测试尚需用户在真实 Windows 电脑上运行。

## 两种入房协议不可混淆

Opentransfer 有二进制大厅协议 ProtoEnterRoom=3、房主接收 ProtoNewGuest、WebRTC 和 RakNet 转服模式；它没有完整交付此实验需要的独立游客入房客户端。

Minecraft-Link、OpenSource-AuthService 和 WPLauncherHelper 提供另一套 online-lobby-room-enter HTTP 入房实现，但目前尚不能证明它适用于本实验的六位数自定义房间。

参考开源代码：
https://github.com/XiaoXiao-2026Max/Opentransfer
https://github.com/coferkim/Minecraft-Link
https://github.com/Zhonger-Yuansi/OpenSource-AuthService
https://github.com/KawaiiNekoChanProject/WPLauncherHelper
https://github.com/GanquanOnline/ViaBedrock_NetEase

## 尚未完成

还需找到六位数房间对应的合法游客入房流程、会话授权、对端信令和游戏协议连接。现有成功只证明发现房间，不是入房或在世界里见到玩家。未经验证不发入房请求、不绕过权限、不公布账号凭据。第三方代码复制和分发前需审核许可证。


## 第二阶段真实运行结果：公共节点匹配已成功

用户 Windows 截图显示 NODE_METADATA_FOUND（真实请求，不是本地自检）：

- 房间号、rid、name：453649；1/30 人；version=48；platform=2；srv=9366。
- 公开节点列表按 id=9366 匹配成功。
- 节点公开地址：42.186.165.240。
- 节点状态码：3；SignalWebPort：8900；ServerType：dockerB；列表内端口数：8。
- 仍然没有登录任何游客账号、加入大厅房间或建立 Minecraft 游戏连接。

准确解释：SignalWebPort 是公开节点记录的信令服务端口标记；不是 Minecraft 世界端口。即使节点可查，也不等于房间会话可用。

## 新找到的实际传输层参考

CubeDock 项目：https://github.com/emuyx/CubeDock 。其 README 称已经实测了网易手机版本地联机的房主侧 WebRTC/NetherNet + RakNet v8 代理以及部分资源包机制，但它的工作模式是自己创建房间、供手机玩家加入，**并不是 PC 游客进入现有手机房间**。

CubeDock 在 vendor-src/nemc-tan-lobby-solver/core/nethernet/dial.go 中提供可发起客户端方向连接的 Dialer：通过 Signaling 获取 ICE/TURN 信息，发送 CONNECTREQUEST 和 CANDIDATEADD，接收 CONNECTRESPONSE 后建立 ICE/DTLS/SCTP 与数据通道。其 signal.go 明确列出信令消息类型。这个 Dialer 是传输层组件，不提供网易游客入房授权、房主 NetworkID、游戏账号或直接可玩的客户端。

参考源文件：
- https://github.com/emuyx/CubeDock/blob/main/vendor-src/nemc-tan-lobby-solver/core/nethernet/dial.go
- https://github.com/emuyx/CubeDock/blob/main/vendor-src/nemc-tan-lobby-solver/core/nethernet/signal.go
- https://github.com/emuyx/CubeDock/blob/main/README.md

## 研究路线与客观限制

1. 已完成：房间发现与公开节点映射，均由用户 Windows 真机截图确认。
2. 尚缺：第二个独立的合法玩家会话，用于游客端加入。不要复用房主正在使用的同一账号导致房主掉线。不要让用户公开提交 Cookie、sauth、sead 或 token。
3. 尚缺：适用于六位数本地联机房间的游客入房流程，尤其是携带真实权限与返回目标 NetherNet NetworkID 的阶段。不可假定与 HTTP /online-lobby-room-enter 相同。
4. 待办：只有在取得获授权的房主会话目标及信令凭据后，再实现客户端侧 Dialer 和网络事件诊断，并检查是否获得游戏数据。
5. 最终才对接 Java/网易基岩协议转换及合法 Minecraft 客户端。严格区别房间元数据成功、会话成功、传输成功和游戏成功。

本实验仍不包括任何账号认证、网络扫描、凭据提取或无授权连接请求。
