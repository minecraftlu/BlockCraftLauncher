# BlockCraft Launcher v0.9.36-wpf-rc.5 — Minecraft Java 服务器查找

仅用于 Windows WPF 测试版（Preview / GitHub prerelease），不影响正式版 v0.9.35。

## 使用位置

顶部选择 **联机**，左侧有三个独立入口：**服务器收藏 / 查找服务器 / 局域网**。

在「查找服务器」中可以按服务器名、地址或介绍关键词搜索，留空则浏览公开目录。还可筛选 Java 版本、来源、目录缓存的在线状态，按综合排行、玩家人数或收录时间排序。支持翻页和选择结果卡片查看人数、地址、版本、标签和提供目录。

选中服务器后：**加入收藏**（保留在原来的本地 JSON 收藏中）、**复制服务器地址**、**查看来源网站**，或按 **直接连接** 使用现有 Minecraft Java 版本启动。若版本可能不兼容或目录未确认在线，会先要求确认。与现有收藏列表、局域网广播发现和微软登录流程保持独立。

## 公共数据源

- [MC Monitor 公共 API](https://www.mc-monitor.app/developers)：GET /api/servers，名称/IP 关键词、版本、热门/新收录及分页。
- [minecraftserve.rs 公共 API](https://minecraftserve.rs/api-docs)：GET /api/servers，Java 版本/关键词、排行与分页。
- [minecraftserver.id / MSID API](https://minecraftserver.id/en/developers)：GET /api/v1/public/servers，Java 服务器、人数、状态、排行与分页（免费且需要在产品中展示来源署名）。

这些目录是第三方公开列表，**不是 Mojang/Microsoft 官方服务器目录**。BlockCraft 不保证它们涵盖所有服务器、持续在线或允许离线账号加入。列表中服务器也并不自动被推荐或认可。仅展示 Java 客户端适用的服务器，不等同于网易手机版/网易房间联机。

只有用户点击「搜索服务器」才联网请求源站。可切换来源以处理中国内地或海外网络环境下的访问失败；同时选择「全部来源」时会合并去重多个目录，个别源站失败时不影响其他源返回成功结果。请求不携带 Microsoft token、Minecraft 账户密码或本地收藏文件，也不自动扫描非公开网络。

## 安全与资源使用

- 每个搜索条件的结果在当前启动器内至少缓存 60 秒；遵守 API 限流，若遇到 HTTP 429 应稍后再试，不能用 IP 轮换或过量请求绕过限流。
- 所有目录仅通过 HTTPS 请求，关闭重定向和 Cookie；限制 JSON 响应大小，拒绝无效或私有/回环服务器地址及非 Java 目录条目。
- 只有用户点击 **直接连接** 才启动 Minecraft 连接指定服务器；这时由游戏本身决定认证、兼容性和连接结果。
- 当前页展示的是**目录缓存状态**，不是即时 Minecraft Server List Ping。可使用原有收藏页的「检测连接」（仅 TCP 可达性，不代表游戏协议登录成功）。
- 单页最多展示来源返回的 60 条结果；查询及最多 200 页受保护，避免无节制批量采集。

## 自动回归

由 Windows GitHub Actions 运行：WPF 与 OneFile EXE 编译、`tests/check_server_discovery.py` 静态验证、`--server-search-selftest` 离线 API 数据解析/安全地址验证、`--ui-layout-selftest` 服务器目录页面与窄窗口实际 WPF 布局、自测皮肤与下载管理、游戏启动回归、更新器回滚和完整打包。

真实公网搜索、加入私人服务器、白名单、网易联机互通和各地区网络条件还需要用户在对应环境测试，CI 离线测试不代表通过这些实机验收。

发布后更新方式：在「设置 → 更新设置」切换至 **预览版**，再检查更新到 **v0.9.36-wpf-rc.5**。正式版 v0.9.35 不改变。SignPath Foundation 数字签名仍在审批中，RC5 不宣称已获得证书。