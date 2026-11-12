# v0.9.47-wpf-rc.1 预览版

本版本仅为 CurseForge HMCL 风格搜索/版本选择测试而发布；稳定版 v0.9.46 不变。

- 使用官方 API 进行在线 Minecraft 版本、加载器和排序筛选，并支持分页。
- 模组详情提供兼容文件版本选择，必要依赖自动安装保留 SHA-1 及回滚逻辑。
- 用户私有 API Key 只从本机 `BLOCKCRAFT_CURSEFORGE_API_KEY` 读取，未经许可不编入公开安装包；如需面对所有玩家分发官方 API 服务，应申请可控后端方案。
- 免费 GitHub Release 将提供 ZIP/EXE、SHA-256、Sigstore 包和 GitHub 来源证明。此为构建来源验证而非 Windows 可信 Authenticode 签名。
- 正式版用户可在「更新设置」切换预览通道，测试后切回稳定版。
- CI 离线测试无法代替用户本人网络环境中真实的 CurseForge 下载与依赖安装验证。
