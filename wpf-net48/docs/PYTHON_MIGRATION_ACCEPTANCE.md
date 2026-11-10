# v0.9.26 Python → C# / WPF 全功能迁移验收清单

基准源码：仓库根目录 `BlockCraftLauncher_完整版.py`。
目标：Windows x64、C# + WPF + .NET Framework 4.8，保留 BlockCraft 自有 PCL 风格界面。
**此表明确区分：有实现 / Windows CI 离线验证 / 真实 Windows 网络、账号与游戏实机验收。**

| Python 模块 / 用户场景 | WPF 进度 | 与“完全稳定”的差距 |
|---|---|---|
| 首页、导航、实例选择、PCL 风格深浅主题 | 已实现；两种 Windows EXE 的 UI 回归通过 | 多 DPI / Windows 10/11 实机视觉验收 |
| 游戏版本列表、原版安装 | 已实现；下载清单与镜像回退离线测试通过 | Mojang/BMCLAPI 真实全量安装，断网恢复与损坏重试 |
| Fabric / Quilt / Forge / NeoForge | CmlLib 相关安装组件已有接入 | 五种加载器 × 常见 Minecraft 版本组合的下载/启动测试 |
| 离线玩家、Microsoft 正版登录与静默恢复 | WPF 已实现；离线错误分类测试通过 | 真实 OAuth 登录/退出、WebView2 环境及账号缓存的 DPAPI 安全存储 |
| Modrinth 模组/资源包/光影搜索与下载 | 已实现；版本和 SHA-1 校验有测试 | 实际联网安装、长尾依赖和 CDN 限流 |
| Modrinth **必需依赖图递归安装** | 本轮新增：固定版本、循环/重复、版本兼容、最多 32 项/16 层、官方 CDN+SHA-1、临时下载、原子登记与本次写入回滚 | 真实社区模组依赖图、复杂混合版本、用户手动安装文件交叉冲突 |
| Modrinth 项目详细信息、语言选择、覆盖翻译词条 | 尚未完整迁移 | 详细内容页、用户字典与 Python 版完整多语种切换 |
| .mrpack 整合包下载/导入 | WPF 已实现安全解压、固定加载器版本和哈希验证 | 大型整合包的实机长时间安装、客户端 overrides 全矩阵 |
| CurseForge / MCIM 模组搜索和必要依赖 | WPF 已有可选 MCIM 工作流 | 授权/在线下载及 Python 未覆盖的整合包更新等流程 |
| 模组更新、备份与回滚 | WPF 已有登记目录、SHA-1 检查/旧文件备份 | 现实多模组批量更新、取消/失败后多进程一致性 |
| 本地导入/模组开关/实例文件夹 | WPF 已实现 | 手动文件冲突、符号链接与跨盘实例回归 |
| **只从列表移除实例（不删除存档）** | 本轮新增与 Python v0.9.26 一致的保留文件行为 | 多实例、运行中的游戏与 UI 并发操作实机验证 |
| **存档备份与恢复** | 本轮新增：ZIP 导出、恢复到新世界文件夹、重复路径/符号链接/ZIP 路径穿越/体积限制/失败回滚 | 大容量真实世界、Windows 杀毒/权限和异常断电情况下的恢复测试 |
| 多人服务器收藏与 LAN 扫描 | WPF 已实现 | 实际双机联机、特殊 IPv6 网络和 Java 服务器协议兼容 |
| 启动器自更新 | WPF 已能只读检查 GitHub Releases | 更新包签名/哈希、退出后替换与可靠回滚尚未实现 |
| 启动与运行日志 | 基础日志窗口已实现 | 敏感信息脱敏导出、诊断包与隐私审计 |
| 独立单 EXE | Windows CI 已能编译、启动自检和打包 | 下载、首次解包、SmartScreen 和无 .NET 运行环境的用户机器验收 |

## 本轮已经提交的实际改动

1. `ModrinthDependencyPlanner.cs`：**先计划整个必需依赖图再下载**，显式版本一致性、依赖循环和无限链保护、所有 JAR 文件名/CDN URL/SHA-1/游戏版本/加载器检查。
2. `ModrinthClient.DownloadModWithDependenciesAsync`：同实例串行化安装，先下载到隔离临时目录，完成后仅移动当前新增文件；出错回滚本次写入，不改动手动放入的已有 JAR。
3. `ManagedModUpdateService.RecordBatch`：一次性校验和保存新安装的依赖资源登记，不再为每个依赖生成独立不一致状态。
4. `LocalInstanceTools.RestoreSaves`：只恢复新的世界目录，ZIP 路径穿越/文件数量/大小/符号链接/覆盖保护及失败回滚。
5. `MainWindow.ManageInstance`：备份、恢复和“仅移除启动器列表”按钮，异步执行 ZIP 处理，**不会删除游戏及存档**。
6. Windows Actions 加入 `--modrinth-deps-selftest` 与 `--saves-restore-selftest`，分别在正常 WPF EXE 和单文件 EXE 运行。全程离线，不获取微软账号/网易凭据，也不会下载真实 Minecraft。
7. 原有 UI 布局、Mojang/BMCLAPI 下载回退、HTTP Range、微软错误恢复、镜像隔离与单 EXE 启动测试继续运行。

## 本轮 CI 与分发

- [成功的 Windows CI #42655016102](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42655016102)：MSBuild、下载、UI、Modrinth 图依赖、注册事务、ZIP 存档恢复、账号错误分类及单 EXE 一致通过。
- GitHub Artifact：`BlockCraftLauncher-v0.9.27-WPF-net48-Windows-Alpha`。
- 单 EXE SHA-256：`631bdb047571865ec940d5119ba23611b489408b908b97ded76e9a13860b19f2`，已从构建 ZIP 解包并按其 `.sha256` 文件独立核对。

## “可替代 Python 稳定版”发布门槛

**不能仅凭 CI success 宣称所有功能完成或完全稳定。** 发布前还需要：

- 一台真实 Windows 10 和一台 Windows 11，以 Microsoft 正版账号/离线账号完整安装并启动 vanilla / Fabric / Forge / NeoForge / Quilt。
- 国内 BMCLAPI 和官方 Mojang 各跑一次合法完整安装，含断网续传、HTTP 429、损坏 SHA、主动取消后的恢复。
- 真实 Modrinth 多层依赖模组、资源包、光影、`.mrpack` 和 CurseForge（仅授权来源）安装、更新与冲突处理。
- 真实 Java 联机服务器和局域网双机测试，且不把在线网络服务状态当作游戏成功。
- Win10/11 的 DPI 100/125/150、不同用户配置目录、文件权限/杀毒拦截和存档从备份恢复。
- 微软刷新令牌的 Windows DPAPI 或经过审计的等效加密存储，登录迁移和退出后的秘密擦除审计。
- 未迁移的多语言、项目详情、安全自更新与完整日志导出验收后，再给出功能对等与稳定版结论。

此版本依旧属于 **WPF Alpha**，默认不覆盖 v0.9.26 Python 稳定版，不自动删除 Python 设置、账号或游戏数据。
