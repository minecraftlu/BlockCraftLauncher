# v0.9.45 正式版 - 文件属性发行者 晓银

基于 GitHub main 合并的公司元数据改动构建 v0.9.45 Stable，保留 v0.9.44 的更新安全性与现有功能。

- WPF、OneFile、Updater 都具有 CompanyName=晓银，ProductName=BlockCraft Launcher，FileVersion=0.9.45.0；在 Windows GitHub Actions 实际编译后核验三个 EXE 的 Win32 FileVersionInfo。
- Windows CI 包括更新 ZIP 与 EXE 交接/回滚、PCL 风格界面、皮肤、模组搜索、离线下载、存档和用户目录等回归测试。
- 发布与真实稳定版升级验证使用独立 Release 标签 v0.9.45；保证 v0.9.44 Release 资产不被覆盖；使用来源证明与 Sigstore。
- 验证旧版 v0.9.44 自动更新到新版本的 OneFile 与 ZIP，优先验证单文件避免旧版缓存被另一种方式的测试清理。
- CompanyName 不等于通过 Windows Authenticode 数字证书验证的发行者；没有证书不应声明微软已验证。Windows 初次运行仍可能显示“未知发布者”。
