# Windows WPF 构建状态 · v0.9.27 Alpha

## 当前结果

- 已创建并静态检查 .NET Framework 4.8 / WPF 工程。
- 已检查本工程固定的 CmlLib.Core、微软认证与 Forge 安装组件的 NuGet 目标框架兼容性，以及主要 API 用法。
- 已添加 Visual Studio MSBuild 检测、.NET Framework 4.8 Targeting Pack 检测、EXE 文件头与 SHA-256 校验、依赖 DLL 打包步骤。
- **当前 Linux 环境没有 Windows Visual Studio、.NET Framework 4.8 WPF/MSBuild 编译器，因此未执行真实 C# 编译，未生成经过验证的 WPF EXE。**
- 真实编译、账号认证、游戏安装与 Windows 运行均为待验证。不要以此工程替换 v0.9.26 稳定版。

## Windows 本机编译

安装 Visual Studio 2022，选择“.NET 桌面开发”工作负载和“.NET Framework 4.8 Developer Pack”。解压本工程后在根目录执行：

```bat
build\build-windows.bat
```

如编译成功，在 `dist/` 下获得 `BlockCraftLauncher-v0.9.27-WPF-net48-Alpha-Windows-x64.zip`。ZIP 包含 EXE、依赖 DLL、SHA-256 和 Alpha 使用说明。

## GitHub Actions 云端编译

将本工程的根目录内容提交到 GitHub 仓库（注意不要覆盖现有 Python 启动器源码），打开 **Actions → WPF net48 Alpha (Windows build) → Run workflow**。成功后，在本次运行的 Artifacts 下载 `BlockCraftLauncher-v0.9.27-WPF-net48-Windows-Alpha`。

若 CI 报错，请提供失败日志中的前一个 `error CS...` 或 `error MSB...`，这样可以根据真实编译错误修复，而不是猜测成功。

## 已执行的静态检查

```
python tests/check_project.py
python tests/check_phase2.py
python tests/check_windows_packaging.py
```

这些检查**不能**代替 Windows 实际编译。
