# 网易手机版房间号查询实验（只读）

目标：验证 BlockCraft Launcher 能否在**未安装网易 PC 客户端**的 Windows 环境下，解析网易手机版自定义联机房间的公开元数据。

当前用户用于自有房间测试的号码：`453649`（会随房间结束而失效）。

## 依据与边界

- 社区项目 [Opentransfer](https://github.com/XiaoXiao-2026Max/Opentransfer) 的 `internal/netease/netease.go` 包含 `LookupRoomByNumber`，向 `https://g79mcltransfer.minecraft.cn/room-with-name` POST `{"name": "房间号", "uid": 0}` 形态的 JSON（实际实现传入指定用户 UID）。
- [Minecraft-Link](https://github.com/coferkim/Minecraft-Link/blob/main/docs/api.md) 也记载了这个房间信息查询路径和部分返回字段。
- 此端点是**社区观察到的服务端路径**，不是我们已确认的官方第三方开放 API。身份要求、有效性和稳定性都可能变化。
- 本实验只查询公开性质的房间信息，不提交登录凭据、不进入房间、不修改任何游戏状态，也不进行认证绕过。

## Windows 运行

1. 保持手机房间运行，在电脑上打开仓库所在目录。
2. 审阅 `Probe-Room.ps1` 的完整代码。
3. 在 Windows PowerShell 5.1 或兼容环境中运行：

```powershell
.\experiments\netease-room-discovery\Probe-Room.ps1 -RoomId 453649
```

如系统禁止执行未签名脚本，应遵循自己的执行策略；不需要关闭系统防护或安装网易 PC 版。脚本只会向一个 HTTPS 服务发送**一次** JSON 查询，默认 UID 是测试值 0。

## 如何解释结果

| 输出 | 含义 |
|---|---|
| `ROOM_METADATA_FOUND` | 响应包含该房间的基本元数据。**不等于已经登录或加入** |
| `INCONCLUSIVE (service response code)` | 服务端拒绝或不支持该请求。不能断定房间不存在 |
| `INCONCLUSIVE (no matching room...)` | 请求有响应但未匹配。可能是身份、房间过期、可见性、接口变化 |
| `INCONCLUSIVE (no successful metadata response)` | 网络故障、HTTP 错误或身份限制，需要进一步诊断 |

输出仅包含编号、人数、版本和平台标记，不输出房主 UID、聊天、会话或敏感信息。用户可分享输出状态和错误码，**不要提供 Cookie、登录令牌、密码、sessionid、sead 或用户凭据**。

## 为什么不直接点击「加入」

要完成真正加入，仍需独立验证：正确的房间 ID 与会话定位、通过平台授权的玩家登录、入房申请与握手、房主或平台准入、游戏传输协议和可运行的游戏客户端。当前 [Minecraft-Link](https://github.com/coferkim/Minecraft-Link) 将进入房间路径标记为“已发现，未执行”，不能将其当作可用的联机实现。

## 集成方案

`BlockCraft Launcher` 的 C# / WPF 版本应将这项功能放在实验性服务层：

- `IRoomDiscovery`：根据房间号查询资料，区分“找到 / 无法判定 / 失败”。
- `IRoomSession`：后续仅接入获得授权的认证和入房功能，不收集或持久化明文账号凭据。
- `IGameBridge`：在授权会话与实际游戏目标明确之后再考虑协议转换。
- UI 状态必须准确区分“房间信息可读取”和“实际已进入游戏”。

**注意：**本仓库 main 当前主要是 Python v0.6 单文件源码，缺少此前讨论的 C# / WPF v0.9.26 工程；本实验放在独立目录和分支，不改动原启动器。
