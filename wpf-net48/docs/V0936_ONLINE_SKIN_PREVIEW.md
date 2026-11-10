# BlockCraft Launcher v0.9.36-wpf-rc.1 · 在线皮肤搜索测试版

> **测试版 / Prerelease**。不替换 v0.9.35 正式版，不应把尚未实机验收的网络功能描述为所有地区都完全可用。当前还没有 SignPath Foundation 证书，预览版 EXE 不宣称已签名。

## 更新与安装

- **已有 WPF 启动器 v0.9.35**：在「设置 → 更新设置」切换到「预览版」，保存后前往「启动器更新」检测。稳定版通道不会自动拉取此候选版本。
- **第一次安装**：在 GitHub [v0.9.36-wpf-rc.1 预览版页面](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.36-wpf-rc.1) 下载单文件 EXE，或下载 Alpha ZIP 后完整解压并运行 `BlockCraftLauncher.Wpf.exe`。切勿与现有稳定版的文件混合解压。
- ZIP 和 EXE 会附带 `RELEASE-SHA256SUMS.txt`。用户可在 PowerShell 中检查 SHA-256。
- ZIP 内有 WPF 启动器、独立更新助手和 CmlLib 等依赖，不能只从 ZIP 中单独拷走 `BlockCraftLauncher.Wpf.exe`。

## 新增皮肤能力

1. 进入「更多 → 皮肤 DIY」，顶部选择「玩家皮肤（Mojang）」输入玩家名（例如 `jeb_`）或 UUID；也可以选「社区皮肤（DP-Plugins）」输入关键词。
2. 选择搜索结果后会展示 PNG 人物正面预览，并标注可获得的作者/许可信息。
3. 点击「下载到皮肤库」直接保存到本地，或者点击「使用并编辑」导入现有皮肤 DIY（有未保存改动时先确认）。
4. 原有 64×64/64×32 PNG 导入、图层编辑、画笔、撤销/重做、保存、导出及微软正版皮肤上传继续保留，上传仍需真人确认和正版登录。

## 发布验收与已知边界

- GitHub Windows CI：静态检查、WPF + 单文件 EXE 编译、离线皮肤校验、UI 布局测试、下载回归、自动更新器回滚检查；只有通过全部受控发布关卡才创建 GitHub Prerelease。
- Mojang 玩家资料查询和 DP-Plugins 第三方目录可能限流、地区访问异常或暂时不可用。失败时不应影响已有离线功能；**仅完成离线 CI 不代表实际联网调用完全通过**。
- 从已有正式版切换到预览版是主动选择；后续返回「稳定版」时需等待正式版版本号高于已安装的 RC，旧版通常不会直接降级。
- 皮肤图片来源与许可归第三方或作者所有，下载、使用前需遵守对应权限。
- 反馈请使用 [GitHub Issues](https://github.com/minecraftlu/BlockCraftLauncher/issues)，不要上传微软登录令牌等私人信息。

开发者昵称为 **晓银**；证书仍待 SignPath Foundation 审核，不能将该昵称伪装成 Windows 已验证发布者。
