# BlockCraft Launcher · C# / WPF / .NET Framework 4.8 迁移工程

**当前版本：v0.9.27 WPF Alpha · 阶段 2（源码预览，不是可替换的正式版）**

从 Python v0.9.26 IconFix 向完全原生 C# WPF 迁移。本公开迁移目录**不包含旧版 Python 源码备份**，避免将 `auth_app.json` 等本地配置误提交；旧版实现仍保留在原项目的稳定版本/发布归档中。**本工程没有嵌入 Python/Go 运行器**。界面采用自有资产和 PCL2 风格布局，未经授权不复制 PCL2 专有素材。

本阶段在原先的首页、侧栏、主题缩放、配置读取、原版安装/启动和 Modrinth 搜索之外，补充了 **Microsoft 正版 OAuth/Xbox/Minecraft 登录与静默刷新、Fabric/Quilt/Forge/NeoForge 安装入口、Modrinth 加载器兼容性校验以及更安全的文件替换策略**。

### Windows 构建

需要 Windows 10/11、Visual Studio 2022（含 .NET 桌面开发工作负载和 .NET Framework 4.8 Developer Pack）、WebView2 Runtime（正版登录时使用）以及 NuGet 联网访问。

1. 解压源码，双击 `BlockCraftLauncher-WPF.sln`。
2. 在 VS 中还原 NuGet 包并以 `Release | Any CPU` 构建；或者运行 `build/build-windows.bat`。脚本会自动查找 Visual Studio MSBuild、检查 .NET Framework 4.8 targeting pack，使用 MSBuild 还原并编译，再在 `dist/` 生成 ZIP 和 SHA-256。
3. 运行 `src/BlockCraftLauncher.Wpf/bin/Release/net48/BlockCraftLauncher.Wpf.exe`。
4. 请在独立测试环境中先试离线账号、已有游戏实例和安装流程。**不要直接替换旧版 EXE**。

仓库根目录 `.github/workflows/wpf-net48-alpha-build.yml` 可在 GitHub 仓库中，通过 Actions → WPF net48 Alpha (Windows build) → Run workflow 使用 Windows runner 构建；成功后到该工作流的 Artifacts 下载包含 EXE、依赖 DLL 和 SHA-256 的 ZIP，但 **本次没有将该工程推送 GitHub，也未生成经过验证的 WPF EXE**。

### 功能及风险

完整状态见 [docs/MIGRATION_STATUS.md](docs/MIGRATION_STATUS.md)。特别提醒：令牌缓存由第三方微软认证组件写入预览目录，**尚未实现 DPAPI 加密**；不可分享缓存文件。Forge/NeoForge 可安装版本取决于各安装组件；非原版安装需实际联网和 Java 运行时验证。

当前尚未迁移：CurseForge 直装与依赖、整合包、多人联机、自动更新以及完整模组更新。相关页面清楚提示迁移状态，不声称已实现。

### 静态检查

`python tests/check_project.py`、`python tests/check_phase2.py`（仅用于跨平台结构检查；不能替代 Windows 编译或运行测试）。

参考库：
- [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core)
- [CmlLib.Core.Auth.Microsoft](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft)
- [CmlLib.Core.Installer.Forge](https://github.com/CmlLib/CmlLib.Core.Installer.Forge)

### 编译准备补丁（阶段 2，Windows CI）

- Windows 构建使用 Visual Studio **MSBuild**，不依赖 Linux 或 Mono 的 WPF 兼容实现。
- `build/build-windows.bat` 找不到 MSBuild 或 .NET Framework 4.8 targeting pack 时会明确报错。
- `build/package-windows.ps1` 检查生成的 EXE、计算 SHA-256，并连同依赖 DLL 一起打包，**不是只分发 EXE**。
- GitHub Actions 的 Windows runner 构建与静态检查分开执行；构建不成功不会上传伪 EXE。
- 当前 Linux 环境只执行过静态检查，**没有产生已编译的 WPF EXE**。
