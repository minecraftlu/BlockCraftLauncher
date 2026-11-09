# BlockCraft Launcher · PCL2 风格自动检测与安全自动更新（v0.9.29 目标）

> 2026-11-10。本功能由 BlockCraft 独立开发，只参考 PCL2 的易用更新体验；不依赖 PCL2 源码或服务。

## 用户体验

- **自动检测（默认启用）**：启动器窗口显示后，最多每 6 小时连接一次 BlockCraft 官方 GitHub Releases，自动判断是否有更高且兼容的版本。无网络时跳过，不妨碍启动 Minecraft。
- **通道分离**：默认「稳定版」只识别正式非草稿 Release + 兼容 WPF 安装包及 SHA-256 清单，不把网易联机实验包或旧 Python 架构当作 WPF 更新；用户可在「设置 → 启动器自动更新」主动选择「预览版」，接收规范的 vX.Y.Z-wpf-rc.N 候选包。不会自动降级。
- **自动下载（默认关闭）**：用户主动开启后，发现新版会后台下载并验证。不允许强制安装：下载完成后仍须点击安装、确认退出。
- **更新提示与进度**：首页显示更新卡片，侧栏「启动器更新 · 新」标记；更新中心包含版本内容、检查按钮、下载百分比、更新按钮及官方发布页。
- **正常 EXE/ZIP 安装均支持**：单 EXE 由外层启动器把实际 EXE 路径传入 WPF，安装时更新真正的原 EXE，不只修改临时缓存；完整 ZIP 使用独立更新助手升级应用文件目录，不删除旧的其他文件或 Minecraft 存档。
- **完整性与安全**：仅使用官方 GitHub HTTPS Release，允许官方下载 CDN 域名重定向。先下载 RELEASE-SHA256SUMS.txt 再读取对应文件的哈希，完成 SHA-256 校验；ZIP 安全解压、拒绝路径穿越与符号链接、验证主要 EXE 和 DLL 的内部哈希。新版本先进行无 UI 的启动自检。
- **退出后替换、失败回滚**：用户确认后单独启动 BlockCraftLauncher.Updater.exe，等待当前 WPF 进程退出，备份旧版本并替换。更新后运行 --smoke-test；失败则自动恢复被替换的旧文件。更新不需要管理员提权，也不会终止 Minecraft Java 游戏进程。
- **账号安全**：更新请求不发送微软账号令牌，也不通过 Minecraft 镜像下载启动器 EXE；私人凭据保留在用户现有配置位置，不打包进入发布资产。开发者应保管官方 GitHub 仓库与 release 发布权限。

## 一次性的升级说明

v0.9.27 和 v0.9.28 RC 在发布时还没有自动安装助手，因而无法对旧程序远程补加按钮。用户需从 GitHub 官方页面**手动安装带有更新助手的第一个新版（目标 v0.9.29 RC）**，此后的兼容版本才能自动检查和一键更新。

## 质量验收

- Windows GitHub CI 执行旧下载、UI、存档、OAuth 离线自测、单 EXE 完整启动检查，以及升级助手 --selftest。测试会使用临时模拟文件故意触发安装失败，核对回滚后原程序字节完全相同且测试世界保持不变。
- 更新元数据的离线测试包含：Stable/RC 分流、同比 RC 号、无需更新/拒绝降级、非 GitHub 域名、未包含 SHA-256 清单或 EXE 的伪 Release、ZIP 路径逃逸和 SHA-256 不匹配。
- **待真实验收**：国内 Windows 网络 GitHub 下载连通性、自动更新首次真实 EXE/ZIP 安装、管理员权限受限目录、多实例同时启动、杀软拦截，以及意外断电时的恢复。
- 正式 Stable 发布仍需以上关键验收；候选版仍显著标为 prerelease，不可仅因 CI success 宣称 100% 稳定。

## 手动回退

- 单文件安装：更新助手使用带唯一标记的 .blockcraft-previous-*.exe 备份旧版；更新前本地旧 EXE 也可以另外留存。
- ZIP 安装：备份位于 %LOCALAPPDATA%\BlockCraftLauncher\updates\rollback-* 目录；程序自动自检失败后会恢复旧文件。不要在更新时手动清空此目录。
- 用户想保留旧安装时，最安全的方法是将新版 EXE 或 ZIP 放在新目录，Minecraft 游戏与世界文件单独保留。

## 2026-11-10 · v0.9.29 RC1 实际发布结果

- [GitHub Release v0.9.29-wpf-rc.1](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.29-wpf-rc.1) 已发布，状态为 **prerelease / candidate**，并非 Stable。
- [受控 Windows 发布构建 #42683268868](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42683268868)：**Success**。编译、静态验证、独立更新助手回滚自测、普通 WPF EXE 与单 EXE 离线测试、SHA-256 清单和发布资产上传均完成。
- 包含单 EXE、完整 ZIP、单 EXE .sha256 与 RELEASE-SHA256SUMS.txt，公开下载链接不会像 Actions Artifact 那样短期过期。
- 较早的 [Windows 自动构建 #42682875674](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42682875674) 也通过 28 项检查。
- **实测边界**：真实联网检测下一新版本、一键替换真实正在使用的 EXE/ZIP、受限权限目录/杀毒软件拦截仍需在 Windows 实机测试。该版本自带更新功能，但 v0.9.28 及更早的版本必须手动安装这一版一次。
