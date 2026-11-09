# Stage 12.9 · 真实网易大厅入房回执证据诊断

本阶段只修复**可验证的入房失败原因**显示，不修改大厅身份、房间权限、G79 会话、网易 Bedrock 协议号、游戏层登录包或任何服务器反作弊机制。实际手机 Minecraft 世界尚未加入。

## 为什么开发这一步

用户在同一启动器中观察到：
- 有些已获房主同意的网易手机版房间曾返回 `WEBRTC_CHANNELS_OPEN_30S_NO_DATA_GAME_NOT_JOINED`；
- 在另一个房间 `486608`、查询人数曾为 `4/5` 时，启动器显示 `ENTER_ROOM_REJECTED`；
- **人数快照不是服务器入房准入回执**，不能据此得出「实际房间有空位，因此必然是账户/协议错误」；
- 原 Go `joinprobe` 的 `TanEnterRoomResponse.ErrorCode` 已经在服务端消息中，但机器回执只携带笼统的 `status`，WPF 没有显示数字服务器错误码。

## 官方回执之外不推测错误原因

本项目当前绑定的 Go 依赖 `github.com/Happy2018new/nemc-tan-lobby-solver`，由锁定替代仓库 `Zhonger-Yuansi/nemc-tan-lobby-solver` 提供。其 `protocol/packet/tan_enter_room_response.go` 定义如下：

| 服务器返回的有符号 int8 码 | 公开协议常量 | 本次显示 |
| --- | --- | --- |
| 0 | `TanEnterRoomSuccess` | 服务器房间准入接受；游戏世界仍未进入 |
| 1 | `TanEnterRoomNotFound` | 房间不存在或已经关闭 |
| 4 | `TanEnterRoomFullOfPeople` | 服务器报告人数已满 |
| 14 | `TanEnterRoomWrongPasscode` | 密码不匹配 |
| 其他（-128 至 127） | 没有已验证的公开含义 | 显示原始数字和「具体原因未知」，不猜测黑名单、账号、权限或版本 |

**未收到 `TanEnterRoomResponse` 时不显示「服务器拒绝」**，而返回 `ENTER_ROOM_RESPONSE_MISSING`。收到其他类型回执则是 `ENTER_ROOM_RESPONSE_UNEXPECTED`。大厅 `TanLoginResponse` 的数字登录拒绝码与房间入房码单独处理；不能把大厅账号错误当成房间密码错误。

## 新代码与防泄密

- Go `GuestTransportLab/internal/admissiondiag` 只根据服务器实际回执中可验证的数值与 `replyObserved` 分类，不根据房间号、人数、公开节点、Cookie 或 G79 授权来推导。
- `joinprobe.Report` 在本机内存中保留大厅响应是否收到及 `int8` 码、房间响应是否收到及 `int8` 码。输出给 WPF 的版本化机器 JSON **只包含这些有限数值、固定分类以及原有阶段布尔值**；没有 UID、Cookie、Token、密码、加密参数、原始网络消息，也不打印服务器数据包。
- Go CLI 统一生成 `admission_diagnosis`，包括取消、超时和无网络的 IPC 自测；不会将空状态误识别为服务器成功。
- WPF `JoinProbeRunner.ParseMachineOutput` 验证所有扩展字段要么完整存在、要么完全不存在（兼容旧 Go 探针）；强制 8 位有符号范围；重算固定分类并检查大厅登录与房间准入阶段。出现伪造成功、消息状态冲突、错误数据类型或不可信分类时拒绝显示。
- 新增 Stage 12.9 说明在真实入房按钮下方；**仍需用户允许和明确点按**原「第九阶段」按钮才能发送现有的唯一一次大厅入房请求。**没有额外自动重试/重放/绕过功能**。

## CI

- Go `admissiondiag/diagnosis_test.go` 覆盖全部 -128..127 范围，只对 0/1/4/14 赋予已经定义的含义。
- Go `cmd/joinprobe/main_test.go` 检查固定机器 JSON 中的真实数字回执、缺失回执、未知负数回执与敏感内容不泄露。
- .NET Framework 4.8 `NeteaseRoomLab.Tests` 验证成功、拒绝、未知、服务器无回执、缺字段、伪造状态、-128..127 上下限、匿名数据不泄露。
- Windows CI 重新构建内嵌 Go 探针 + WPF 单 EXE，验证真实可执行探针离线 IPC 与三嵌入资源；Stage 12.3–12.8 的离线协议/真实只读探针功能保持不变。

## 合法的下一步实测

1. 确保用自己拥有或房主明确允许的网易手机版房间和第二个账户，且房间仍在线。
2. 升级到 Stage 12.9 Windows 单文件预览版；**只执行一次原有第九阶段「真实大厅登录并申请入房」**。
3. 若收到 `ENTER_ROOM_REJECTED`，把启动器固定状态区的 `STAGE12_9_SERVER_ROOM_REPLY` 和数值码截图提供给维护者。不要提供 Cookie、令牌、密码、UID、网络原始回执或隐私信息。
4. 若提示 `ENTER_ROOM_RESPONSE_MISSING`，应核查网络超时、加密格式及房间是否已关闭，**不要当作服务器明确拒绝**。
5. 若合法手机第二账号可加入而 BlockCraft 收到明确拒绝码，才可把研究重点进一步聚焦到客户端协议兼容和会话模式；这也不代表可以跳过认证或强行修改服务器返回结果。

仍缺真正获得网易认可的游戏层身份、协议版本确认与手机游戏世界 StartGame；Stage 12.9 **不能进入真实 Minecraft 世界**。

来源：[锁定协议包的 `TanEnterRoomResponse` 定义](https://github.com/Zhonger-Yuansi/nemc-tan-lobby-solver/blob/4dc3f1cdc574/protocol/packet/tan_enter_room_response.go)。
