# 模组下载

在「下载 → 模组下载」选择 Modrinth、CurseForge 官方或 GitHub 开源。已启用 MCIM 时，也可以手动切换到该来源。切换来源时保留搜索关键词和目标实例。

1. 选择来源和目标实例，按名称搜索。CurseForge 的游戏版本、加载器、分类、排序和分页由官方 API 处理。
2. 选择结果，页面会显示项目详情和与目标实例兼容的实际文件。选择「安装所选版本」后，任务中心显示进度，可取消或重试。CurseForge 的「另存为 JAR」也可在未选实例时使用。
3. CurseForge 和 Modrinth 自动解析必需前置。CurseForge 复用校验一致的已有文件，所有新文件校验通过后才写入实例的 `mods` 目录。

CurseForge 官方源使用本机 `BLOCKCRAFT_CURSEFORGE_API_KEY`，向官方 API 及官方 Edge CDN 认证。作者关闭第三方分发或接口未提供有效下载地址时，启动器会显示具体原因。安装过程在启动器内完成。

Windows Actions 的 `HMCL-style CurseForge search Windows CI (no secrets)` 检查页面切换、筛选、版本选择与模拟下载，生成 WPF ZIP 和单文件 EXE 测试产物。使用有效授权的实时 CurseForge 安装需在配置密钥的机器上验证。
