# WPF Alpha 阶段 2 更新记录

- 新增 `MicrosoftAccountService.cs`：接入 Microsoft OAuth / Xbox / Minecraft Java 登录、静默刷新、退出账号，使用独立预览版缓存目录，旧版账号凭证不作导入。
- 新增 `ModLoaderInstaller.cs`：原版、Fabric、Quilt、Forge、NeoForge 下载与安装入口；在写入新实例前检查版本元数据。
- 更新 `MinecraftBackend.cs`：支持传入真实正版 `MSession` 的启动路径，绝不自动将正版启动降级为离线模式。
- 更新 `MainWindow.xaml.cs`：新增账号入口与安装取消按钮，防重复安装，安装成功才写入 WPF 配置。
- 更新 `ModrinthClient.cs`：检查模组加载器与 Minecraft 版本兼容性，强制 SHA-1 文件校验。
- 更新 `NetworkDownloader.cs`：校验后原子替换旧文件，改进 429/5xx 退避等待。
- 更新 NuGet 依赖与 Windows CI 静态检查。

## 验证

- Linux 容器：`python tests/check_project.py`、`python tests/check_phase2.py` 通过。
- **未验证**：MSBuild 实际编译、WPF UI、Microsoft 登录、游戏启动、加载器实际下载安装、中国大陆与海外网络表现。

## 必须避免的误解

- Windows EXE **未生成**。不能将此源码替换 v0.9.26 IconFix 用户正在运行的稳定版。
- Forge/NeoForge 在部分新/旧版本可能不兼容，安装库自身可能在单个阶段不支持即时取消。
- WPF 预览认证缓存 **尚未实现 DPAPI 加密**，不能视为已完成安全审计。
- CurseForge、整合包、多人联机与自动更新尚未迁移。
