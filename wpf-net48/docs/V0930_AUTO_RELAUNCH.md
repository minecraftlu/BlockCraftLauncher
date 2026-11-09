# BlockCraft Launcher v0.9.30 · 更新完成自动打开新版窗口

日期：2026-11-10。仅实现用户明确确认安装之后的自动打开；不强制安装、不退出 Minecraft Java 游戏。

## 使用体验

1. 在 v0.9.29 或之后的 WPF 启动器中，设置「更新通道 → 预览版」，检测到更高版本后点击「下载并安装 · 完成后自动打开新版」。
2. 更新模块从 BlockCraft 官方 GitHub Release 下载适配的单 EXE 或 ZIP，并验证 SHA-256；解压与启动自检通过后，要求用户最终确认。
3. 用户确认后，原 WPF 主窗口关闭，独立更新助手等待原进程退出，再替换真正的单 EXE 或 ZIP 文件夹，保留备份，更新失败还原旧文件。
4. 替换并通过新版 `--smoke-test` 之后，更新助手自动启动原安装目录中的**新版本启动器**，参数为一次性随机 `--after-update <token>`。
5. 新版 WPF 真实主窗口完成 `ContentRendered` 事件后激活窗口、将窗口带到前台、将状态栏更新为「更新完成」，并写入无敏感信息的确认文件。更新助手等待确认，避免单 EXE 外层解包进程快速退出而产生误判。
6. 如果 70 秒内无法确认新版主窗口已经渲染，更新助手**不**重复打开第二个窗口，也不覆盖已运行的新文件；会提示可手动打开的新 EXE 路径并记录诊断，避免因为仅确认文件失败而强制回滚正在运行的新程序。

## 安全与兼容

- 更新助手/标记文件仅使用 `%LOCALAPPDATA%\BlockCraftLauncher\updates`，不保存微软、网易账户或 Minecraft 游戏数据。
- 更新包下载只使用官方 GitHub HTTPS Release，拒绝未知重定向；正式版与 RC 预览版严格区分，并在安装前确认。
- OneFile 的 `BLOCKCRAFT_SINGLEFILE_BOOTSTRAP` 用于识别**用户运行的外层 EXE**；窗口确认由真正渲染 UI 的内层 `BlockCraftLauncher.Wpf.exe` 执行。
- 未经真实 Windows 在线旧版到新版自动更新验证，不得标记 Stable。

## 自动化测试覆盖

- 新增 `LauncherRelaunchSignal.RunOfflineSelfTest()` 验证一次性确认令牌与真实 WPF PID 格式。
- 独立更新助手 `--selftest` 以延迟生成窗口确认标记模拟正常渲染；无确认则判定失败，同时保留单 EXE、ZIP 文件更新失败回滚的模拟测试。
- `wpf-net48/tests/check_window_relaunch.ps1` 分别启动多文件 WPF 和单文件 EXE，以 `--after-update <token> --window-relaunch-selftest` 实际创建并渲染 WPF 主窗口，验证内层真实 PID 写入确认文件后才退出。测试禁用网络与账号读取，不向用户弹出窗口确认。
- GitHub Actions 的普通 Windows CI 及受控 RC Release CI 都运行新版窗口自检、下载器/存档/登录离线自检与更新助手回滚。

## 正式版剩余验收

Windows 10/11 下真实在线从 v0.9.29 RC1 升级到 v0.9.30 RC1，验证新版窗口能自动显示、版本号正确、只有一个启动器窗口、游戏/账号/存档数据不变；另测试受限目录、杀软、网络中断和恢复。完成实测后才能进行 Stable 发布审批。
