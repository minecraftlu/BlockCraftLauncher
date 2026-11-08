# Stage 6 — 网页认证后的 g79 游戏授权验证（未入房）

## 为什么需要这一阶段

用户使用自己的第二个网易邮箱账号，在 Windows PowerShell 中运行 Opentransfer `connect.exe -login -login-method email`，输出 `Cookie 已保存` 且 `AUTH_HELPER_EXIT_CODE=0`。这表示**第三方登录助手成功获取本地 Cookie**，但**不证明网易移动端游戏的 g79 登录成功**，更不是实际加入手机房间。

此前测试确认：房间查找、节点匹配、本机 WebRTC 模拟数据通道、TanLobby 入房二进制协议的离线编解码均可工作。当前用户最近使用的房间号是 `280474`，其是否在线仍需实时查询。

## 新增 g79 授权自检

在 WPF Stage 6 实验程序中，选择已审查的 `connect.exe`，并在原有邮箱登录成功且本机私有 Cookie 文件存在时，点击 **「验证第二账号 g79 游戏授权（只检查，不入房）」**。

每次启动都要求用户显式确认第三方认证程序的账号风险。WPF 只在本机启动 `connect.exe -dump-auth -c <仅包含 Cookie 文件路径的 JSON 配置>`；配置 `auth_mode=g79`，不执行代理/开房流程。

接入依据：Opentransfer `cmd/connect/main.go` 的 `-dump-auth` 分支调用 `auth.LoginWithCookie(ctx, cookie, cfg.AuthMode)` 并打印 `uid`、`login_token`、`auth_mode`、`platform`。其 `internal/auth/auth.go` 在 g79 模式调用 `account.LoginG79`。这是**第三方项目的代码路径，不是官方发布的 SDK 或开放接口**。

上游源代码参考：
- https://github.com/XiaoXiao-2026Max/Opentransfer/blob/main/cmd/connect/main.go
- https://github.com/XiaoXiao-2026Max/Opentransfer/blob/main/internal/auth/auth.go
- https://github.com/XiaoXiao-2026Max/Opentransfer/blob/main/server.example.json

## 敏感信息处理与使用限制

- `-dump-auth` **会输出真实 login_token**；因此不要直接让用户在 PowerShell 中执行 `-dump-auth` 或把其输出截图发给任何人。
- 新版 WPF 使用重定向管道，**仅在内存中短暂读取**此输出、判断状态，不显示、不复制、不写日志、不保存 token；StdErr 直接丢弃。由于 .NET `string` 不保证内存清零，仍需要信任运行中的程序与第三方组件。
- G79 配置文件只有 auth_mode、cookie_file 路径、非对外使用的 transfer 配置及 log_level，没有 Cookie 内容，也没有账号密码；验证结束后删除。
- 接口设置 90 秒超时和输出长度上限，超时后结束第三方进程。不会尝试房间加入，不会建立游戏数据通道。
- 不将 `auth_mode=auto`、Cookie 文件存在、第三方进程正常退出自动推断为 g79 验证通过，必须检查输出中匹配 `auth_mode=g79`、`platform=2`、有效 uid 与 token 字段。
- 如果返回 `G79_HELPER_LOGIN_ACCEPTED`，含义是第三方组件声称已获得网易移动端游戏登录会话，并不代表网易官方认可第三方接入，**更不代表已进入 280474**。
- 如果返回 `G79_HELPER_LOGIN_FAILED`，不能把它直接解读为密码错误；可能涉及身份验证、实名认证、风控或网络。

## 隐私要求

不要发送 Cookie 文件、token、sessionid、邮箱全称、密码、短信验证码或身份证资料。不用房主账号登录电脑端，避免踢出手机房主；应始终使用已准备的第二个账号。

## 实测与未完成

1. 用户先前的邮箱 Cookie 已在本机获取成功（用户 Windows 截图）。
2. Stage 6 的 G79 验证代码已加入，Windows CI 测试验证本地配置生成、结果识别、敏感 token 不出现在 UI 状态。
3. **尚未在用户本机运行真实 G79 验证。**
4. **尚未实现游客房间准入授权、真实 RakNet TanEnterRoomRequest、WebRTC 到房主的连接与 Minecraft 世界加载。**

真实房间入场需要授权获取的大厅握手字段与票据；`nethernet`、`raknet` 和入房编解码可作为后续模块，不能用 `srv` 节点 IP 和公开端口替代授权票据。


## Stage 6.1：实机 G79_CHECK_INCONCLUSIVE 后的安全诊断改进

用户 Windows 截图显示：Stage 6 本机调用认证助手后状态为 `G79_CHECK_INCONCLUSIVE`，说明第三方程序正常退出，但输出未满足所有预期字段。此时**不能断言 g79 游戏授权成功，也不能认为邮箱 Cookie 无效**。

Stage 6.1 不尝试获取原始 `-dump-auth` 文本，因为它包含真实 `login_token`，直接提供 PowerShell 命令或屏幕截图会泄漏凭据。

更改 `GuestG79Verifier.ClassifyResult` 以容忍字段名称的大小写差异、额外空格与 BOM，并只返回不可逆的**布尔字段状态**：

- `uid_valid=yes/no`（不显示真实 UID）
- `auth_mode_g79=yes/no`
- `platform_pe=yes/no`
- `token_present=yes/no`（不显示 token，不显示长度或前后缀）

只要四项条件未同时满足，就维持 `G79_CHECK_INCONCLUSIVE`，不以退出码 0 自动判断成功。Windows CI 增加了模拟字段不全、输出格式差异以及无 token 泄漏测试。

用户安装 Stage 6.1 后只需要使用原账号 Cookie，再点击一次「验证第二账号 g79 游戏授权（只检查，不入房）」，只提供这四个布尔状态即可；无需重新登录、复制原始输出或上传 Cookie。未完成真正的六位数网易手机联机房间授权/入房与游戏连接。


## Stage 6.2：平台标记已满足，但 g79 模式标记未匹配

Windows 用户截图的**真实本机状态**为：

- `uid_valid=yes`
- `auth_mode_g79=no`
- `platform_pe=yes`
- `token_present=yes`

含义：第三方认证助手返回 UID、PE 平台编号与非空登录凭据字段，但不满足本地对 `auth_mode=g79` 的严格判断，因此**不得视为真实 G79 认证通过，更没有进入网易手机房间**。

从已公开的 Opentransfer 源码看，`connect.exe -dump-auth` 打印 `auth_mode`；`LoginWithCookie(..., "g79")` 会调用 `LoginG79`，并将对应模式放入返回字段。按这一实现，本地生成的配置 `auth_mode=g79` 应使输出与验证器一致。但用户所运行的实际二进制及其输出细节未经独立观测，所以本阶段不能判定原因；可能是具体程序版本、输出格式、缺失字段或模式不匹配。

Stage 6.2 在原四项布尔标记之外，新增**固定值分类** `mode_category`：仅为 `g79`、`x19`、`auto`、`other` 或 `missing`，永不回显第三方原始字段值。即使遇到未知文字或敏感内容，也只显示 `other`。仅当严格 `g79` 条件全部满足才显示 `G79_HELPER_LOGIN_ACCEPTED`；否则保留 `INCONCLUSIVE`。

不要求用户执行会把真实 `login_token` 打印在终端的 `-dump-auth`；无需发 Cookie、用户 ID、邮箱或令牌。新增离线测试覆盖缺失字段、x19 模式、未知模式及 token 不泄漏。
