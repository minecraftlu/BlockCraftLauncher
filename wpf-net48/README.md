# BlockCraft Launcher · Windows WPF

**v0.9.35 正式版 · Windows x64 · .NET Framework 4.8**

从 [v0.9.35 发布页](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.35) 下载单文件 EXE，或完整解压 ZIP 后运行 `BlockCraftLauncher.Wpf.exe`。

从 RC1 升级时，在「设置 → 更新设置」选择「稳定版」，保存后打开「启动器更新」检查新版。从 Python 版迁移时，在独立目录启动 WPF 版，再选择已有游戏实例。

### Windows 构建

需要 Windows 10/11、Visual Studio 2022（含 .NET 桌面开发工作负载和 .NET Framework 4.8 Developer Pack）、WebView2 Runtime（正版登录时使用）以及 NuGet 联网访问。

1. 解压源码，双击 `BlockCraftLauncher-WPF.sln`。
2. 在 VS 中还原 NuGet 包并以 `Release | Any CPU` 构建，或运行 `build/build-windows.bat`。脚本使用 Visual Studio MSBuild 还原并编译，在 `dist/` 生成 ZIP、单文件 EXE 及校验值。
3. 运行 `src/BlockCraftLauncher.Wpf/bin/Release/net48/BlockCraftLauncher.Wpf.exe`。

GitHub Actions 中的 **WPF net48 (Windows build)** 工作流会在 Windows 上编译、执行自检并保存安装包。

### 静态检查

`python tests/check_project.py`、`python tests/check_phase2.py`（仅用于跨平台结构检查；不能替代 Windows 编译或运行测试）。

依赖项目

- [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core)
- [CmlLib.Core.Auth.Microsoft](https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft)
- [CmlLib.Core.Installer.Forge](https://github.com/CmlLib/CmlLib.Core.Installer.Forge)
