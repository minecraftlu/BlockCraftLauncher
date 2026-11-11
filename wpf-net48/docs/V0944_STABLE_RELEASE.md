# BlockCraft Launcher v0.9.44 正式版验收说明

基础代码：v0.9.44 RC1（`v0.9.44-wpf-rc.1`，Windows GitHub Actions 构建完成，EXE 与 ZIP 已正式发布为预览版）。

正式发布：`v0.9.44`，独立的 Windows OneFile EXE 和 ZIP。遵循完整静态检查、.NET Framework 4.8 MSBuild、UI 窗口/布局、下载路径、Minecraft/下载/皮肤/模组/存档离线自测、更新回滚与 GitHub/Sigstore 来源证明。CI 创建正式版后还运行从 v0.9.42 Stable 检测并升级到 v0.9.44 Stable 的两种形式的真实公共 Release 端到端检测。

核心行为：下载及 SHA-256 校验结束后先退出旧版窗口，再由独立更新助手替换原 EXE 或 ZIP 文件并启动新版；有正在进行的游戏、下载或未保存皮肤草稿时延后。更新失败校验旧 EXE 哈希后回退；用户设置的自动更新关闭选项继续生效。

现实限制：GitHub CI 测试不覆盖全部真实 Windows 10/11 电脑、100%-140% DPI 环境、不同运营商网络、微软账户交互或第三方模组的实际可下载性。PCL2 只作为 UI 布局和交互参考，使用自有资产。没有 Authenticode 证书，用户可能看到 Windows 安全提示。文档不得当作“所有功能永久无故障”的证明。

官方页面：https://github.com/minecraftlu/BlockCraftLauncher/releases
