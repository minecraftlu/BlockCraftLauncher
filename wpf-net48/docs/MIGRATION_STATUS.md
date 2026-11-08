# BlockCraft Launcher WPF · 迁移状态（阶段 2）

**基线：** Python v0.9.26 IconFix；**目标：** C# + WPF + .NET Framework 4.8；**工程标识：** v0.9.27-WPF-alpha2。

本项目仍为 **Alpha 源码工程**，不等于已经发布了全功能 WPF 客户端；未在真实 Windows 环境编译、登录、下载、启动过。不要移除旧版启动器。

| 原版功能 | 当前实现程度 | 验证和限制 |
|---|---|---|
| WPF 主窗口、PCL2 风格、侧栏 | 已实现基础布局 | XML 与源码静态检查；未做 Windows 截图验收 |
| 深色/蓝白主题、全局缩放 80–140% | 已实现基础机制 | WPF LayoutTransform；未做 Windows 高 DPI 验证 |
| 读取旧配置与实例 | 已实现兼容读取 | WPF 预览写入独立 `settings.wpf-preview.json` |
| Minecraft 原版安装与离线启动 | 已接入 CmlLib.Core | Windows 首次构建/运行未验证 |
| **微软正版登录与静默续期** | **阶段 2 新增接入** | CmlLib.Core.Auth.Microsoft OAuth + Xbox + Minecraft；依赖 Windows WebView2；实际登录待测 |
| **微软退出登录** | **阶段 2 新增接入** | 调用 SignoutWithBrowser；缓存独立于旧版，未测 WebView2 |
| **Fabric / Quilt 安装** | **阶段 2 新增接入** | CmlLib.Core 自带安装器；版本兼容性及实际安装未测 |
| **Forge / NeoForge 安装** | **阶段 2 新增接入** | CmlLib.Core.Installer.Forge 2.0.0；新/旧版本兼容性未测 |
| 原版 + 加载器安装状态 | 已编写 | 安装完成后检查版本 JSON；取消操作不能中断第三方库当前阶段的下载 |
| Modrinth 模组、光影下载 | 基础下载 + 阶段 2 校验改进 | 校验 MC 与 mod loader 匹配、SHA-1；尚未处理所有依赖 |
| Modrinth / CurseForge 全部依赖和搜索 | 部分 / 未迁移 | CurseForge 直装、依赖图、授权、下载队列尚未移植 |
| `.mrpack` 整合包 | 未迁移 | 安全解压与覆盖策略尚未实现 |
| 自动模组更新、多人联机、更新器 | 未迁移 | 保留明确迁移提示，不提供空的“成功”回执 |
| 中国大陆、海外网络优化 | 仅基础超时/重试/代理 | CmlLib 下载后端尚未统一国内镜像路由；两地真实网络验证未做 |
| Windows EXE | **未交付** | 需在 Windows + Visual Studio 2022 + net48 Developer Pack 构建 |

## 新增依赖

- `CmlLib.Core` 4.1.0 (netstandard2.0)
- `CmlLib.Core.Auth.Microsoft` 3.4.0 (netstandard2.0)
- `CmlLib.Core.Installer.Forge` 2.0.0 (netstandard2.0，包含新版 NeoForge 集成)
- WebView2 Runtime：Microsoft 交互式登录依赖；不由 ZIP 自动提供。

## 账号和存储安全

- **不导入原 Python 启动器的登录令牌、密码或刷新令牌。** 仅导入玩家名称等非密钥字段。
- WPF 预览自己的账户缓存路径：`%APPDATA%\BlockCraftLauncher\wpf-preview-auth\accounts.json`。CmlLib 提供的 JSON 账户管理器可能包含令牌，请勿上传、分享该文件或把它放入日志、ZIP、Git 仓库；使用 Windows 个人账户与系统文件权限保护此目录。
- 若要完全退出，请使用 WPF 的“退出账号”，而不仅仅是关闭窗口。升级或卸载 Alpha 时可以删除预览账户缓存，旧 Python 客户端不会受影响。
- 本阶段尚未实现 Windows DPAPI 对本地 OAuth 缓存加密；这应在正式版之前作为阻塞项修复。
- WPF 与旧启动器使用相同 Minecraft 游戏目录：**不要同时进行同一实例的安装、更新或游戏启动**。

## 下一阶段工作（发布正式版前必需）

1. Windows CI/Visual Studio restore 和 build，修正真实 NuGet API 编译问题。
2. Windows 双机实际登录/静默续期/登出（WebView2）、原版与各加载器完整安装与启动。
3. 迁移 CurseForge、Modrinth 依赖与 `.mrpack`，下载失败诊断及哈希校验。
4. 扩充中国大陆/海外分运营商、分 CDN 连通性测试和回退策略。
5. 迁移多人联机、模组更新、诊断导出与自动更新；完成 UI 测试和安全审计。
