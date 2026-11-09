# BlockCraft Launcher v0.9.26 → v0.9.27 WPF 功能对照（2026-11-08）

基准：v0.9.26 IconFix（Python/Tk + Go）；迁移目标：C#、WPF、.NET Framework 4.8、Windows x64。

> 这里的“接入”表示源码实现并在 Windows CI 执行编译检查，不等于已完成 Windows 真机端到端测试。
> WPF 预览继续使用独立的用户配置、账号缓存和模块数据文件。**不得覆盖 v0.9.26 稳定版。**

| 模块 | 当前迁移实现 | 仍需验证或继续开发 |
|---|---|---|
| PCL2 风格首页/侧边栏、深浅主题、80–140% 缩放 | 已接入 WPF | Windows 高 DPI / 多窗口回归测试 |
| 启动器图标与 Windows x64 | 已接入 x64/PE 验证 | 实际 Windows 10/11 任务栏图标检查 |
| 离线模式、微软账号、静默续期、退出 | CmlLib / WebView2 x64；用户反馈能使用新版登录入口 | 端到端账号身份、静默续期和退出验证；令牌加密 |
| 原版 Minecraft / Fabric / Quilt / Forge / NeoForge 安装 | CmlLib 组件；支持 .mrpack 指定的加载器版本 | 各游戏版本真实安装、启动、取消与进度 |
| 本地实例、存档备份和本地资源管理 | 本地扫描、导入、启用/停用 JAR / 光影 / 资源包 | 多实例路径一致性、兼容性、备份恢复 UI |
| Modrinth 搜索、下载 | 已接入 API、兼容性和 SHA-1 验证 | 分页/分类/中文检索、模组自动依赖 |
| **Modrinth 模组更新** | WPF 管理的资源登记、检查、更新、备份和回滚 | 批量失败续传与跨版本迁移实测 |
| **Modrinth 整合包 .mrpack** | 搜索/下载/本地导入、限制解压、SHA-1/SHA-512、独立实例、固定加载器版本 | 联网安装完整整合包、客户端 override 冲突处理/恢复、兼容全量包 |
| **CurseForge** | 可选 MCIM 搜索、JAR、必要依赖、SHA-1 和防覆盖 | MCIM 访问授权、文件 URL、真实下载、持久队列与更新管理 |
| **多人联机** | 服务器收藏、TCP 连通性、直接进入服务器、UDP 局域网发现 | 游戏内直连实测、延迟/在线人数/历史、复杂 IPv6 |
| **启动器更新** | 只读查询 GitHub Releases、打开官方页 | 校验下载更新包、回滚与自动替换未实现 |
| 国内外自动线路/代理/多线程 | 保留基础路由和 WPF 代理设置 | CmlLib 安装器全面接入 Mojang/BMCLAPI 快速切换、运营商实测 |
| 资源包、光影辅助组件、离线工具与诊断 | 基础文件管理与诊断 | Iris/Sodium 组合安装、网络测速与诊断导出、账户存档工具 |
| Windows 构建 | GitHub Actions MSBuild x64，SHA-256、ZIP 打包 | Windows 真机 GUI / 实际下载、登录、启动回归 |

## 2026-11-09 · 原版游戏下载稳定性补丁

- 此轮只修改 WPF 原启动器的 `MinecraftDownloadFactory.MinecraftRouteHandler`，没有修改已取消的网易手机房间实验。
- 游戏资源通过 Mojang/BMCLAPI 线路切换时保留 Range、User-Agent 等公共下载头；**跨域镜像不会收到官方请求的 Authorization、Cookie、自定义私有头或 If-Range**，官方回退则恢复原始请求。
- 镜像缺少快照/依赖而返回 403、404、410、416，以及暂时超时、429 或服务端错误时按需回退官方；官方真正返回 404 时不误报镜像有该资源。
- 安装器的大文件 HttpClient 超时改为 15 分钟，仍可取消任务；没有声称实现文件级并行分块。
- Windows CI 的 `--download-selftest` 现在使用**完全离线的模拟 HTTP handler** 验证上述行为，且分别在普通 WPF EXE 与打包后的单 EXE 中执行。
- [Windows CI #42647089376](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42647089376) **success**，下载/主题之外的启动烟雾与微软重新认证离线测试也没有回归。
- 本轮有经过 Windows 编译和离线测试的 WPF Alpha 构建，但仍需要在真实网络环境实测多版本游戏安装、国产运营商线路和加载器兼容；**不得标记已与 v0.9.26 全功能等价**。

## 重要的安全边界
- 不读取或公开旧版的 Microsoft 刷新令牌。
- .mrpack 的文件名不能逃出新实例目录，限制大小，校验 SHA-1 与 SHA-512；发生失败不会登记实例。
- 只自动更新 WPF 安装并登记的 Modrinth 资源，不猜测手动放入的 JAR。
- CurseForge 使用可选 MCIM，必须在设置中主动启用，不绕过作者禁止下载的限制；不使用别人的 API Key。
- 自动更新暂时为只读检查和手动下载，不自动执行不可信的 EXE。

## 后续必须完成才能声称“与 v0.9.26 全功能等价”
1. 真实 Windows 双机测试所有页面、登录、5 类加载器、下载/联机/整合包；对照旧版行为逐项验收。
2. CmlLib 全部文件下载通过国内/海外自动镜像、并发优化与回退测试。
3. CurseForge 后台下载队列、更新管理、整合包、浏览分类和授权兼容性；Modrinth 模组依赖图。
4. Fabric 光影助手、实例维护、更新器安全安装/回滚、日志导出和主题缩放细节。
5. 对微软账号令牌使用 Windows DPAPI 做加密存储，并完成审计。
