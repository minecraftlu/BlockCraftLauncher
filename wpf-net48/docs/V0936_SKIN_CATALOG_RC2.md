# BlockCraft Launcher v0.9.36-wpf-rc.2 · 开放授权皮肤图库测试版

> 仅预览版 / GitHub prerelease。正式版仍是 v0.9.35；旧 RC1 不会被覆盖。SignPath Foundation 签名尚未获得审核批准。

## 新功能

- 将「更多 → 皮肤 DIY」顶部的在线下载区改成类似 Modrinth / CurseForge 模组下载的图文列表：带缩略图、名称、作者、下载数、模型和许可信息；右侧选中皮肤的预览及操作按钮。
- Mosaicary 公开皮肤目录：留空浏览、搜索关键词、分类（女孩/男孩/角色/生物）、Steve/Alex 模型、最近/热门/最多下载排序、上一页/下一页。限制为可供 Minecraft Java 使用的 64×64/64×32 公开发布作品。
- DP-Plugins 社区目录继续支持在线关键词搜索，可与 Mosaicary 的第一页结果合并浏览；Mojang 玩家皮肤按玩家名/UUID 独立查询。
- 默认只显示作者明确声明 CC0、CC BY、CC BY-SA 及支持的自由许可作品。未公开授权、保留所有权或带非商业限制的结果不得被自动归为「开源」，关闭筛选后只可查看来源而不可直接下载。
- 保存到皮肤库或导出来源皮肤时创建同名 .png.source.txt 署名文件，保留许可、作者及原始来源链接。若 CC BY-SA，则再分发修改版时仍须遵守许可证要求。
- 点击「使用并编辑」进入原有 DIY 编辑器，原来的微软正版皮肤上传功能与草稿备份保持不变。UI 不会在进入页面时自动访问外部图库。

## 下载和更新

已安装 WPF v0.9.35 或 RC1 的用户，在「设置 → 更新设置」中选择「预览版」并检查更新。RC1 可以升级 RC2；稳定版更新通道不会接收 RC2。首次使用可从 GitHub Release 下载 OneFile EXE 或 Alpha ZIP。

GitHub 测试版地址（发布后有效）：https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.36-wpf-rc.2

## 风险与验证

- Github-hosted Windows CI 必须成功完成编译、图文布局、单文件打包、离线皮肤校验、更新器回滚及 SHA-256 校验。受控发布流程须独立重新编译和复测，所有关卡通过后才能创建 prerelease。
- Mosaicary 与 DP-Plugins 是独立网站，其访问速度、库存与许可信息可能变化。软件无法保证收录整个互联网的所有开源皮肤，也不保证在每个中国内地网络环境都可直接连接。
- UI 缩略图仅下载源站生成的有大小限制的 PNG；保存时仅接受有效 Minecraft 64×64 或 64×32 皮肤，用户必须遵守作者的许可证。
- Windows SmartScreen 可能提示未签名应用。SignPath Foundation 项目仍在审核，无法宣称签名或保证关闭弹窗。

详细来源、联网说明见 [ONLINE_SKIN_SEARCH.md](ONLINE_SKIN_SEARCH.md) 与 [PRIVACY.md](PRIVACY.md)。测试反馈请在 https://github.com/minecraftlu/BlockCraftLauncher/issues 提交，勿分享任何微软身份令牌。