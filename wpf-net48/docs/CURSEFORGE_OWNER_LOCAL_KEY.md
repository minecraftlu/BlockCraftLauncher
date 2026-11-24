# 在 BlockCraft 中使用 CurseForge

## 下载官方 Windows 客户端

打开「下载 → CurseForge 官方」，点击「下载 CurseForge 客户端」，选择保存位置。你可以在「下载任务」查看进度、取消或重试。启动器从 Overwolf 官方地址下载独立安装程序，使用 Windows 验证数字签名及发行者 Overwolf Ltd，成功后将安装程序保存为 EXE。需要安装时，手动运行保存的文件。这项功能无需 CurseForge API 密钥。

## 直接安装 Minecraft Java 模组

1. 打开「下载 → CurseForge 官方」，选择目标游戏实例，例如已安装 Fabric、Forge、NeoForge 或 Quilt 的版本。
2. 输入项目名称并搜索。可用版本、加载器和分类缩小结果范围。
3. 选中项目后查看兼容文件。点击「更多版本」可查找较早的发布。选中文件时，页面会列出必需前置。
4. 点击「安装所选版本」。下载任务会先处理必需依赖，再将校验通过的 JAR 放入目标实例的 `mods` 目录。仅需取得文件时可选择「另存为 JAR」。

官方服务受项目作者的分发设置约束。文件安装前会核对 Minecraft 版本、加载器、项目和文件标识，以及官方元数据提供的 SHA‑1。已有同一文件且校验一致时会复用本地副本。

## 为公开版提供官方模组服务

维护者可以部署仓库中的 CurseForge 服务，并将获授权使用的密钥存为服务端 Secret `CURSEFORGE_API_KEY`。部署后先检查 `/health`，再验证分类查询、兼容文件查询，以及一个允许第三方分发的模组的实际下载。部署和接口说明见[服务端说明](../../backend/curseforge/README.md)。

通过后，更新 `minecraftlu/BlockCraftLauncher` 仓库 `curseforge-service-config` 分支根目录的 `service.json`。该文件是公开的服务地址登记表。把 `serviceUrl` 设为实际部署的 HTTPS 源地址，例如 `https://mods.example.org`，使用默认端口，省略路径、查询参数和凭据。下面的内容表示尚未登记服务。

```json
{"serviceUrl": ""}
```

在启动器的官方模组页面点「重新检测官方服务」即可重新读取配置。登记服务前，页面会说明官方搜索和安装暂不可用，仍可下载官方客户端或浏览官网。登记表的固定地址为[公开服务地址配置](https://raw.githubusercontent.com/minecraftlu/BlockCraftLauncher/curseforge-service-config/service.json)。

自行构建时也可以通过 MSBuild 属性 `BlockCraftCurseForgeServiceUrl` 指定自己的公开 HTTPS 源地址。本机联调可设置环境变量 `BLOCKCRAFT_CURSEFORGE_SERVICE_URL` 并重新启动。服务地址可以公开，API 密钥仅保存在提供服务的主机上。

## 开发者在 Windows 本机直连

拥有官方 API 授权的开发者可以在「系统属性 → 高级 → 环境变量」中新增当前用户变量 `BLOCKCRAFT_CURSEFORGE_API_KEY`，完全关闭并重新打开启动器。此时官方模组入口优先使用本机密钥。官方 API 以及官方 Edge CDN 的授权请求可携带密钥，其他下载域名、公开服务和 MCIM 镜像均不会收到该密钥。若密钥已暴露，请先在 CurseForge 撤销旧密钥，再配置替换密钥。

要使用 MCIM，可在「下载设置」启用该镜像，然后从模组下载页选择「CurseForge · MCIM」。
