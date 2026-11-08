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
