# BlockCraft Launcher v0.9.36 · 正式版发布准备

**状态：待合并、待最终验证与人工发布；不是已发布的正式版。** 目前 GitHub latest 稳定版仍是 v0.9.35。v0.9.36-wpf-rc.5 已通过受控 Windows CI 并作为 prerelease 发布，不能覆盖它。

## 本次正式版包含的功能

- Minecraft Java 游戏版本与实例启动、微软正版账号、国内和海外可选下载线路、模组/光影/资源包和整合包查询下载，以及本地资源与存档管理。
- 在线皮肤图库与 PNG 搜索下载、预览、授权信息保留；皮肤下载和模组同在「下载」分类，离线「皮肤 DIY」独立保留。
- 独立的「联机 → 查找服务器」页面，基于第三方公开 Java 服务器目录搜索、版本/在线缓存筛选、收藏和直接连接；原有局域网搜索及收藏不改变。
- 正式版资源名称应为 `BlockCraftLauncher-v0.9.36-WPF-net48-Windows-x64.zip` 和 `BlockCraftLauncher-v0.9.36-WPF-OneFile-Windows-x64.exe`，提供发布前生成的 SHA-256 清单。
- 内置 `CurrentTag=v0.9.36`，与 RC5 的 `v0.9.36-wpf-rc.5` 不同。更新器应将同数值版本的 **Stable** 视为比 RC5 新，并确保稳定通道不会看到任何 prerelease。

## 已有证据和未完成项

- [RC5 受控发布构建](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42824167368) Success；包含普通 WPF/OneFile 编译、静态回归、界面、皮肤、公开服务器目录的 **离线自检**、升级助手回滚与真实窗口打开测试。
- 2026-11-10 项目所有者在 ChatGPT 回报 RC5「都成功了」。这是**用户自行反馈的实际使用结果**，没有提供 OS/DPI、全部启动加载器、真人账号回连、不同网络地区和旧版升级操作的逐项测试报告；不可将其写成每个子项已独立验收。
- 该分支须通过 **独立稳定版 ZIP/OneFile Windows CI**，包括对已发布 v0.9.35 的哈希验证与旧版本升级预检。不能只复用 RC5 CI 结果。
- Release 工作流从经审核合并的 `main` **手动启动**，并要求项目所有者在 `real_device_acceptance` 上明确勾选；不可在预览版开发分支直接发布 stable。
- 已有升级脚本 `tests/check_v0936_promotion.ps1` 使用已发布 **v0.9.35 Stable** 的真实旧资产（校验官方 SHA-256）进行预检，并仅在新 v0.9.36 Release 发布后执行实际在线升级/新版窗口验证。这类发布后的测试失败应立即停止推广并开问题追踪。
- Windows SignPath Foundation 免费签名申请已提交但**尚未收到批准**。本版不能宣称已有代码签名、受信任发行者或没有 SmartScreen 警告。

## 发布前检查

1. 审查本 PR 对 `main` 的所有累计变动，确认没有重新引入取消的网易房间联机实验代码。
2. Windows 稳定版构建、ZIP+EXE 双包、离线测试、旧版升级预检全部成功，校验列表包含两个发行包，并检查 Windows x64 运行。
3. 项目所有者确认真人实机测试的范围和回归后，将本 PR 合并 `main`。
4. GitHub Actions → **WPF controlled release (candidate / stable gate)** → **Run workflow** → 分支 `main`、`release_tag=v0.9.36`、`channel=stable`、真实验收已满足时选中 `real_device_acceptance`。
5. 检查 Release 页面 `prerelease=false` 和 `latest=v0.9.36`、EXE 与 ZIP 下载有效、哈希一致，再测试真实稳定通道从 v0.9.35/RC5 的更新。RC1–RC5 预览版本应原样保留。

有第三方图库或目录访问失败时应清晰提示，不把目录缓存在线人数说成实时探测结果，也不绕过皮肤版权或网站配额。
