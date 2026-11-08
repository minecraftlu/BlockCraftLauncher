# BlockCraft 第四阶段：网易本地联机游客入房协议

## 关键突破：已发现完整游客侧连接参考

公开仓库 https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver 的 `protocol/service/dial.go` 具有端到端游客侧步骤：

1. 通过已授权的 `Authenticator.GetAccess(roomID)` 获得大厅与信令参数（这不是匿名接口）。
2. RakNet 连接大厅并以授权返回的随机数、玩家 ID 等登录。
3. 发送 TanEnterRoomRequest；处理房间不存在、人数已满、密码不正确等服务端回执。
4. 等待 TanNotifyServerReady，取得真正房主的 NetherNet 网络标识。
5. 使用授权信令种子与票据建立 WebSocket，随后由 NetherNet Dialer 建立 ICE/DTLS/SCTP 连接。

此开源实现提供协议结构级证据，但本实验没有确认它仍兼容用户当前网易手机版的版本/房间，也不应复用房主账号作为游客。

相关参考文件：
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/packet/tan_enter_room_request.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/packet/tan_enter_room_response.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/packet/tan_notify_server_ready.go
- https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/main/protocol/service/dial.go

该仓库检索时未见根目录 LICENSE，**不可假定其可用于直接复制、捆绑、再分发**。本实验独立编写字段格式的离线测试代码，不直接从仓库复制实现。

## 本实验实际实现

`internal/tanroom/codec.go` 为离线、安全限长的二进制结构编解码器，实现以下结构：

- TanEnterRoomRequest：ownerID、roomID、password、enterTeamID、enterToken、followTeamID、guestNetherNetID、compressed。
- TanEnterRoomResponse：结果码；成功时按 uint8 长度读取玩家 ID 列表和物品 ID 列表。
- TanNotifyServerReady：房主服务地址、RakNet GUID、RTC 房间号、房主 NetherNet ID 和压缩标记。

所有整数与两字节类型头按 big-endian 编码；StringUTF 前缀是 2 字节 UTF-8 长度。解码器拒绝截断、超长、额外尾部字节、非法 UTF-8 和无效网络 ID。

命令：

```powershell
.\BlockCraft.GuestTransportLab.exe -protocol-selftest
```

输出 `LOCAL_TANROOM_CODEC_OK` 和 `PROTOCOL_SELFTEST_PASS` 只说明**纯离线模拟数据的协议字段编解码成功**；不会登录、查询、加入任何网易房间，也不会把测试号 453649 发往网易服务。

此协议没有独立用户身份或有效授权时不会建立真实会话。`srv=9366` 是大厅节点 ID，不可直接作为 host NetherNet ID。

## 下一阶段实施顺序

1. 取得单独且合法的游客身份（须通过平台要求的验证；不得用手机房主登录会话冒充游客）。
2. 在合法账号上下文中验证与六位数本地房间对应的大厅授权信息和准入响应，并记录脱敏错误码。
3. 联合经授权的大厅会话与已通过本机自检的 WebRTC Dialer；只有真正进入手机房间后才允许显示 ONLINE 或 CONNECTED。
4. 调研游戏协议、资源包及 Java 客户端合法运行；本阶段不声称用户可以进入世界。

## 测试状态

- 房间发现：Windows 真机已通过（用户截图）。
- 公共节点映射：Windows 真机已通过（用户截图）。
- 本机 WebRTC 数据通道：Windows 真机已通过（用户截图）。
- 游客入房二进制数据包：目前仅自动化离线测试，不是服务端协议实测。
- 网易游客授权和真实加入：未完成。
