# 在线皮肤搜索与下载（Windows WPF）

本功能集成在启动器左侧 **「更多 → 皮肤 DIY」** 页面顶部，不改变现有的离线皮肤编辑、草稿恢复、PNG 导入/导出或 Minecraft 正版皮肤上传功能。

## 使用方法

1. 搜索方式选择 **玩家皮肤（Mojang）**：输入一个真实 Minecraft Java 玩家名称（如 \`jeb_\`）或 UUID。启动器先查玩家 UUID，再从 Mojang 公开会话服务提取当前皮肤 PNG 地址。若该用户仅使用默认皮肤，界面会明确提示。
2. 或选择 **社区皮肤（DP-Plugins）**：输入 2～60 字关键词，搜索第三方社区公开皮肤目录。可用的结果数量取决于社区服务的索引。页面显示原作者及许可字段（若服务提供）。
3. 选择一个结果，启动器最多下载 **4 MiB** 的原始 PNG 并在页面中预览人物模型。
4. 点击 **下载到皮肤库**：把 PNG 重新编码、去除原始元数据，存到现有 \`%APPDATA%\BlockCraftLauncher\Skins\` 本地库，不修改当前正在编辑的皮肤。
5. 点击 **使用并编辑**：如果编辑器已有未保存的修改，会先询问用户是否舍弃。载入后仍需点击现有的「保存到皮肤库」或「导出 PNG」保存。若使用微软正版账号，用户可通过原有的、需要确认的上传按钮将当前皮肤设置为自己的 Minecraft Java 皮肤。

## 数据来源和权限

- **玩家资料查询**：\`api.minecraftservices.com\`、\`api.mojang.com\`、\`sessionserver.mojang.com\`，贴图为 \`textures.minecraft.net\`。
- **社区关键词检索**：\`https://dpplugins.com/api/skins?search=\`，该站的 [公开 API 文档](https://dpplugins.com/docs/api) 将此接口标记为无需个人 API key 的只读资源。它是一个独立的第三方社区，不隶属于 BlockCraft 或 Mojang；可用性、覆盖范围、数据版权与服务地区均由对方决定。
- 搜索时不需要 Microsoft 登录令牌，且不会自动把用户自己的皮肤上传到其他服务。使用者应尊重皮肤作者提供的许可和归属信息。
- 中国内地或海外如果对应站点不可达，界面会显示错误，离线皮肤 DIY 和本地皮肤库保持可用。
- 不应把社区皮肤服务返回的地址当作可信的 HTML。应用不执行远端脚本，只接受可验证大小/尺寸的 PNG，禁用 HTTP 重定向，并拒绝私网/IP 字面量地址。
- 详见 [隐私和联网说明](PRIVACY.md)。

## 离线和 Windows 测试

- 已增加 \`OnlineSkinSearchService.RunOfflineSelfTest()\`，经现有 \`--skin-selftest\` 入口执行，**不会联网**。它检查玩家名/UUID、Mojang URL 标准化、Base64 textures 解析、Alex 模型及 PNG 像素尺寸约束。
- \`tests/check_skin_search.py\` 验证 UI、服务和隐私声明的接线，并加入 Windows build 与 release workflow 的静态检查。
- 合并前需要审查 GitHub Actions 的实际 Windows 编译、自检结果。
- 合并后建议在 Windows 电脑**实际联网**测试 \`jeb_\` 玩家搜索、社区关键词搜索、预览、下载、无网络报错、导出和正版上传确认。由于第三方服务可变，离线静态检查不等于全网可用性验证。

此 PR 仅提供下一版本的功能开发，**不会自行触发正式版本发布，也不代表目前的 v0.9.35 已经包含上述功能**。
