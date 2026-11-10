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

### 代码签名与安全说明

- [免费发布签名与验证](docs/FREE_RELEASE_SIGNING.md) — 新正式版提供 Sigstore/Cosign 签名包、GitHub 来源证明及 SHA-256 校验；**不是** Windows Authenticode，不能消除未知发布者提示。
- [代码签名政策](docs/CODE_SIGNING_POLICY.md) — SignPath Foundation 申请因项目人气不足而被拒绝（维护者反馈），并无受信任的 Windows 发行者证书。
- [Privacy and network activity / 隐私与联网说明](docs/PRIVACY.md) — 说明微软登录、游戏下载、模组管理及启动器更新相关联网服务。
- 开发者显示昵称为 **晓银**，不是 Windows 证书中的法定签名主体。Sigstore 来源校验不会让 Windows 将此昵称显示为已验证发行者。
- 本项目的 WPF 源码使用当前目录的 [MIT License](LICENSE)；不代表仓库里其他文件已自动适用相同许可。


### 在线皮肤搜索与下载（下一版本功能）

「更多 → 皮肤 DIY」页面提供两种在线皮肤查找方式：**Mojang 玩家名/UUID** 和 **第三方社区关键词搜索**。可预览人物、下载标准 PNG 到本地皮肤库，或在确认后直接导入编辑器。联网失败不影响离线编辑；社区内容应遵守作者许可。

数据来源、功能边界和验证方法参见 [在线皮肤搜索说明](docs/ONLINE_SKIN_SEARCH.md)。正式版 v0.9.35 暂不包含该功能；代码在完成 Windows 编译和联网测试后才可发布新版本。

### v0.9.36 RC1 皮肤搜索测试版

已准备 [在线皮肤搜索与下载测试版 v0.9.36-wpf-rc.1](docs/V0936_ONLINE_SKIN_PREVIEW.md)。在现有 WPF 启动器「设置 → 更新设置」选择「预览版」后，才能检测该候选版本。正式版 v0.9.35 仍保持不变。

预览版会新增 Mojang 玩家皮肤搜索、第三方社区关键词搜索、图片预览、下载到本地皮肤库及使用并编辑；外部服务的实际连通性需要用户联网测试。

### v0.9.36 RC2 · 开放授权皮肤图库预览版

下载中心新增 [Mosaicary 自由许可图库](https://mosaicary.com/developers) 和 DP-Plugins 社区结果，界面采用类似模组下载的缩略图卡片、作者/许可证、筛选和分页，以及右侧预览与下载操作。详见 [RC2 说明](docs/V0936_SKIN_CATALOG_RC2.md)。

公开可下载的皮肤不一定属于开源/自由许可；默认只显示明确授权的图库条目，下载与修改前请遵守作者声明。

v0.9.35 仍为正式版。预览通道内已安装 RC1 的用户可以更新到 RC2（正式发布 RC2 后）；请不要在普通稳定版发布流程中使用本分支。
### v0.9.36 RC3：皮肤下载与 DIY 分类分离

**下载 → 社区资源 → 皮肤** 与模组下载并列，包含 Mosaicary / DP-Plugins / 玩家皮肤搜索、筛选、缩略图、分页、下载任务进度、取消及重试。

**更多 → 工具 → 皮肤 DIY** 独立提供本地画笔、PNG 导入导出、皮肤库、恢复草稿和正版上传。两页可跳转且不丢失编辑草稿。

详见 [RC3 预览版与版权说明](docs/V0936_SKIN_NAVIGATION_RC3.md)。正式版 v0.9.35 不受影响。
### v0.9.36 RC4：公开皮肤单张下载（测试版）

在「下载 → 社区资源 → 皮肤」选择「全部公开皮肤（授权未知需确认）」即可浏览已公开但未声明自由许可的来源皮肤；对于这类皮肤，「下载到皮肤库」和「使用并编辑」会弹出许可证确认说明，确认后才执行单张下载。文件自动附带作者、来源和许可证备注。

公开 PNG 可以访问并不代表获得转载、修改、商用或再分发许可。不会绕过图库限流或提取非公开作品。详见 [RC4 说明](docs/V0936_PUBLIC_SKIN_DOWNLOAD_RC4.md)。稳定版 v0.9.35 未改变。
### v0.9.36 RC5 · Minecraft Java 服务器查找测试版

顶部「联机」新增独立的「查找服务器」页面，与原有服务器收藏和局域网入口并列。支持从 MC Monitor、minecraftserve.rs 和 minecraftserver.id/MSID 等公开目录查找 Java 服务器，按名称或 IP、版本、在线状态搜索，并可按玩家数排序及翻页；还可一键复制地址、收藏或使用已安装的 Minecraft Java 实例加入。

目录状态属于缓存，服务器是否能加入、正版账号要求和白名单由目标服务器决定。公开目录搜索为可选联网功能，不发送微软账号令牌；没有自动网络扫描。详细说明见 [RC5 服务器查找说明](docs/SERVER_DISCOVERY_RC5.md)。

此功能只随 GitHub 预览版 RC5 发布。稳定版 v0.9.35 保持不变。
### v0.9.36 中国内地和海外下载/搜索优化（正式版候选）

新增 [区域线路与公开资源搜索优化说明](docs/V0936_REGIONAL_ACCELERATION.md)：
Modrinth 模组/光影/资源包/整合包和可选 MCIM CurseForge 搜索采用
45 秒内存缓存；皮肤图库缓存和缩略图并发限制；已允许的备用线路
首次 HTTP 连接超时更快回退。保留哈希校验与自动更新回滚。
这不保证第三方平台始终可用；稳定版发行前须完成实际 Windows 网络验收。

### v0.9.36 正式版发布准备（未发布）

RC5 已通过 [Windows 受控 CI](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42824167368)，用户已反馈功能在自己的电脑上成功。现正为 v0.9.36 稳定版检查正式 ZIP、单 EXE、SHA-256、从 v0.9.35 直接升级的兼容性及真实 WPF 窗口重启。

**目前最新公开稳定版仍是 v0.9.35；v0.9.36 正式版还没有发行。** 详细验收和手动发布门禁请参见 [v0.9.36 发布准备清单](docs/V0936_STABLE_RELEASE_READINESS.md)。原 RC1–RC5 预览版全部保留，不得直接重标记为正式版；SignPath Foundation 申请已因人气过低被拒绝；新免费的 Sigstore 签名方案不会改变 Windows 未知发布者提示。
