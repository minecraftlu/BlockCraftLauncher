# BlockCraft Launcher WPF 稳定版发布验收门槛

> 2026-11-10。UI 排版参考 PCL2 的交互层级，由 BlockCraft 独立实现；不复制其专有图标、图片或源码。
> **目前 v0.9.27 WPF 仍是测试预览，不得只凭 GitHub Actions success 宣称稳定版。**

## 本轮已实施的界面统一

- 顶部蓝色导航：四项主入口、Segoe MDL2 字体图标、圆角选中标签；BlockCraft 自有品牌。
- 侧栏：188 px 统一宽度、分组标题、固定 21 px 图标列、选中状态强调。
- 所有 C# 动态生成的按钮：统一符号位置、中文标题间距、点击反馈与禁用状态。
- 卡片与列表：统一间距、标题字重、说明颜色、长描述换行；原版版本、模组、光影、整合包、下载任务沿用安全的数据绑定。
- 设置：游戏/账号、外观、网络三组两栏布局；标签与输入控件对齐，附中文提示。
- 多人联机服务器与本地文件：统一可选卡片，名称/地址与状态分层显示。
- 更新中心：正式版发布与 CI 预览版区别展示，必须是兼容 WPF 的发布资产才会提示可升级。
- `--ui-layout-selftest` 实际遍历首页、版本、模组、光影、资源包、整合包、下载、多人联机、模组更新、CurseForge、启动器更新、设置、日志等入口。

## 自动化通过 ≠ 实机通过

| 质量门槛 | 已知状态 | 正式版要求 |
|---|---|---|
| Windows x64 MSBuild net48 与正常 ZIP | CI 检查 | CI Success 且清单校验 |
| 独立单 EXE 自解包 | CI 检查 | CI Success，首次启动/更新/杀软实测 |
| 所有 UI 页面无 XAML 异常 | CI 离线测试 | Windows 10/11 真机反复导航 |
| 1366×768 / 1920×1080，100%/125%/150% DPI | 尚未全面验收 | 无遮挡/错位/方块字/非预期横滚 |
| 深色、浅色与 80%～140% 缩放 | 部分 CI | 真机观察主题切换和输入焦点 |
| 微软正版登录、令牌静默恢复 | **用户在 2026-11-10 反馈微软正版交互登录成功**；CI 离线认证恢复测试通过 | 还需验证实际正版游戏启动、静默续期、退出登录；日志不泄露 Token |
| 原版/Fabric/Forge/NeoForge/Quilt 安装启动 | 部分源码与 CI | 在线完整资源安装并可进入单人世界 |
| Modrinth、CurseForge（明确授权）、.mrpack | CI 离线测试 | 真实合法下载、安装及存档保持 |
| 存档安全恢复与启动器卸载 | CI 离线测试 | 不覆盖/不删除已有世界 |
| Java 多人服务器与局域网双机 | 未端到端验收 | 真机联机、网络失败恢复 |
| 网易手机版房间互通 | 不支持/研究阶段 | **不能作为稳定版功能宣传** |
| 更新资产/签名或 SHA-256 核验与回滚 | 现有检查和包装 SHA-256 | 稳定版升级前必须能核对官方哈希、手动回退 |

## 发布流程

1. 先从 WPF 开发分支获取最新 Windows CI 构建，将完整 ZIP 或单 EXE 放在全新目录，保留旧版启动器及游戏数据。
2. 完成以上实测项目，记录测试日期、OS、DPI、Minecraft 版本及发现的问题。
3. 修改 UI 中的“预览”提示、项目文件版本、打包文件名和更新服务版本，复核新旧架构不互相覆盖。
4. 合并经过审核的 WPF 代码至 `main`，使用受控发布工作流进行最终 Windows 编译、所有离线自测和 SHA-256 生成。
5. 只有验收确认且所有测试通过时才允许创建非 prerelease GitHub Release；否则仅发布显著标为 Candidate 的测试版。
6. 正式版可供启动器从 GitHub Releases `latest` 检查，必须带 WPF x64 EXE/ZIP；Python 或网易房间实验版不可被当作升级。

此清单不包含任何用户的 Microsoft、网易账号或私密联机凭据。发布流程不会读取这些数据。

## 2026-11-10 · Windows 用户反馈记录（不涉及账号隐私）

- 用户反馈：**v0.9.27 WPF UI 优化版的功能正常运行**；尚未按 OS、Minecraft 各版本、加载器、网络和存档功能逐项提供测试记录，因此不将这条整体反馈等同于全部验收。
- 用户随后进一步明确反馈：**微软正版登录成功**，将交互授权与登录路径标记为「用户实测成功」。这是使用体验反馈，不是本仓库自动重放真人 Microsoft 令牌的测试。
- 不能据此宣称 Microsoft 访问令牌静默续期、正版游戏启动、登出及多账号切换均成功；这些维持待验证状态。发行包不读取、不保存、不发布用户提供的任何凭据。

## 2026-11-10 · Release Candidate 发布

- 发行标记：`v0.9.27-wpf-rc.1`，**GitHub prerelease（RC，尚非 Stable）**。
- 由 `.github/workflows/wpf-net48-controlled-release.yml` 在专用 WPF 开发分支的 Release Candidate 请求触发，完成 Windows x64 打包、源码静态检查、普通 EXE 与单 EXE 离线回归和 SHA-256 文件生成。
- [GitHub Actions #42675105225](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42675105225)：success。
- [公开下载页](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.27-wpf-rc.1)：包含单文件 EXE、完整 ZIP、EXE 独立 SHA-256 及完整发布 SHA-256 清单。
- 额外修复：当仓库只有预览版本、没有 WPF 正式版时，更新中心不应因 `/releases/latest` 返回 404 失败；现在筛选官方 Releases 列表中的可兼容 WPF Stable，并排除网易房间实验版、旧版 Python、Alpha 包、异常 URL 和伪造版本号。
- 用户已确认 WPF 主要使用功能正常、微软正版交互登录成功；仍未逐项完成游戏版本安装/启动、正版续期、双机局域网与 DPI 全范围实机回归。**正式 Stable Release 继续受验收门槛限制。**

## 2026-11-10 · 中国大陆下载 / CurseForge 图文候选版

- v0.9.28 WPF 新增国内镜像优先回官方、下载断点来源校验、长文件超时及缓存复用、可选 MCIM 加速；CurseForge 项目封面、作者、下载量和截图详情已经由源码及 Windows 离线测试验证。
- [CI #42678383098](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42678383098) 完成 27 项编译与自检；[受控候选版发布 CI #42678756716](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42678756716) 亦成功，且已发布 [v0.9.28-wpf-rc.1](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.28-wpf-rc.1)（prerelease）。
- **不能仅凭这些结果宣称已经获得国内实测速度优势、模组在线真实安装和完整 Stable 验收。** 中国大陆 ISP 测试结果与实际图片可用性仍待收集，v0.9.27 RC1 继续保留供回退。

## 2026-11-10 · v0.9.29 RC1 实际发布结果

- [GitHub Release v0.9.29-wpf-rc.1](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.29-wpf-rc.1) 已发布，状态为 **prerelease / candidate**，并非 Stable。
- [受控 Windows 发布构建 #42683268868](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42683268868)：**Success**。编译、静态验证、独立更新助手回滚自测、普通 WPF EXE 与单 EXE 离线测试、SHA-256 清单和发布资产上传均完成。
- 包含单 EXE、完整 ZIP、单 EXE .sha256 与 RELEASE-SHA256SUMS.txt，公开下载链接不会像 Actions Artifact 那样短期过期。
- 较早的 [Windows 自动构建 #42682875674](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42682875674) 也通过 28 项检查。
- **实测边界**：真实联网检测下一新版本、一键替换真实正在使用的 EXE/ZIP、受限权限目录/杀毒软件拦截仍需在 Windows 实机测试。该版本自带更新功能，但 v0.9.28 及更早的版本必须手动安装这一版一次。

## 2026-11-10 · v0.9.30 RC1 已发布与 CI 验证

- [GitHub Release v0.9.30-wpf-rc.1](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.30-wpf-rc.1) 已上传单文件 EXE、完整 ZIP、SHA-256 清单；状态为 **prerelease（候选版）**，不是尚未验收的 Stable。
- [受控 Windows 发布任务 #42686477605](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42686477605)：**Success**。重新编译、更新助手回滚离线测试、双发行方式的窗口实际渲染确认、主要 Minecraft/登录/下载离线回归、SHA-256 生成及上传均成功。
- 此版本在更新完成后自动打开新版 WPF 窗口并带到前台，显示非阻塞的「更新完成」状态；一旦无法确认实际窗口渲染，更新助手给出原 EXE 的手动启动路径，避免重复打开。
- 从已有 [v0.9.29 RC1](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.29-wpf-rc.1) 中将更新通道切为「预览版」，现在应能够检测到 v0.9.30 RC1。它是首次真正适合在实机上验证应用内升级的目标版本；**实机验证结果尚未收到，不得将 RC1 宣称为完全通过 Stable 验收。**
