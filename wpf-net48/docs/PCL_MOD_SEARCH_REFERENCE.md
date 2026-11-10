# PCL2 截图参考：BlockCraft Launcher WPF 资源中心改进

用户提供 PCL2「下载 → Mod」的 Windows 截图，目标是在保留 BlockCraft Launcher 品牌、源码和现有下载安全流程的前提下，改善搜索筛选、资源条目密度、中文文字排版和可操作性。此方案是参考界面层级的独立实现，并非复制 PCL2 源码、专有图标或联网服务。

## 实际修改

- 顶部仍是 BlockCraft 自有蓝色导航条，左栏资源下载分组更名为**社区资源**，`游戏版本 → 原版游戏`、`资源中心 → Mod`、`光影引擎 → 光影包`；路线、页面入口、现有 CurseForge 与实例管理逻辑均不变。
- Mod、资源包和光影包页面使用**名称 / 来源、版本 / 类型**两行两栏筛选，版本框支持手动输入 Minecraft Java 版本号；模组类型支持优化、科技、冒险、装饰、支持库、世界生成等 Modrinth 官方 categories，额外可指定 Fabric/Forge/NeoForge/Quilt 加载器和搜索排序。保留“仅当前实例兼容”开关，重置按钮同时清空分页/结果。
- 来源明确显示 `Modrinth` 和 `CurseForge（独立搜索）`；切换 CurseForge 会进入已有可选 MCIM 页面，而不是错误声称“全部来源合并搜索”。MCIM 没有开启时不发送 CurseForge 网络请求。
- 资源结果由文本列表改为图标、名称、作者、两行简介、类别、加载器、支持游戏版本、下载量、更新日期和 Modrinth 来源的分层卡片。完整简介保留在悬浮提示中。真实图片仅接收经过验证的 `https://cdn.modrinth.com` 图标地址，加载失败保留占位符。
- 下载量和更新时间直接来自 Modrinth v2 搜索结果的 `downloads` 和 `date_modified` 字段，未给出时标明未知；总结果数由 `total_hits` 推动上一页/下一页启用状态。保留 Modrinth 搜索结果限制每页 20 条。
- 搜索按下回车、点击搜索均可触发；支持上一页/下一页，35 秒超时和切页时取消旧请求，避免旧页面结果覆盖新页面。
- 项目双击改为**查看详情**，不再意外直接发起下载。详情页提供完整说明、真实数据、安装到当前实例、打开官方 Modrinth 项目页。资源安装仍调用既有校验、依赖自动安装与后台任务队列。
- 暗色模式仍使用原有 DynamicResource 色彩 token；没有把浅色截图里的颜色硬编码到全局文字或卡片中。

## 验证和限制

- Windows Actions 静态检查、MSBuild 编译、正常 EXE 和单文件 EXE 的 UI 回归继续执行。
- 新增 `--modrinth-search-selftest`：完全离线构造公开 API 风格响应，验证项目图标 host 白名单、下载量格式、日期、类别、版本和总结果数，两个分发版本都执行。
- Modrinth 搜索字段、filter facets 和项目数据类型与官方 API v2 规范对应：<https://docs.modrinth.com/api/operations/searchprojects/>。
- 已有的微软正版登录、游戏版本根目录检测、原版/Fabric/Quilt/Forge/NeoForge、整合包/备份与模组依赖不会因本次 UI 改造被替换。
- 不宣称支持 PCL2 所有来源聚合、数据包独立安装、官方中文译名数据库或 PCL2 自有资源；这些功能需要独立、安全的后端实现和真实网络验收。
- 真实 Windows 机器的 1366×768 / 1920×1080、125%/150% DPI、代理网络下图标加载和 Modrinth 限流仍需用户实测。
