# BlockCraft 的 CurseForge 服务

Windows 启动器通过此 HTTPS 服务搜索 Minecraft Java 模组并下载 JAR。服务使用为 BlockCraft 申请的 CurseForge API 密钥，用户无需配置个人密钥。

## 在浏览器中部署

[在 Cloudflare 部署此服务](https://deploy.workers.cloudflare.com/?url=https%3A%2F%2Fgithub.com%2Fminecraftlu%2FBlockCraftLauncher%2Ftree%2Fv0.9.56-wpf-rc.1%2Fbackend%2Fcurseforge)

1. 仓库维护者打开上方入口，使用自己的 Cloudflare 和 GitHub 账号完成授权。入口选取 `v0.9.56-wpf-rc.1` 发布标签的 `backend/curseforge` 子目录，Cloudflare 会把它作为独立 Worker 项目导入。保留默认部署命令。
2. 在 Cloudflare 的 Secret 输入框中填写为 BlockCraft 获批的 `CURSEFORGE_API_KEY`，完成部署。如果部署向导跳过了 Secret，在对应 Worker 的 **Settings → Variables and Secrets → Add** 中选择 **Secret**，保存并部署。 [Workers 控制台](https://dash.cloudflare.com/?to=/:account/workers-and-pages)
3. 从 Worker 的 **Settings → Domains & Routes** 复制实际生效的 HTTPS 源地址。访问该域名的 `/health`，密钥和配额绑定就绪时应返回 HTTP 200 和 `{"configured":true}`。缺少配置时返回 503。
4. 把该地址填入项目的候选地址文件并提交。GitHub Actions 会自动查询分类、检查作者分发许可、下载一个真实的小型 JAR 并核对字节数和 SHA‑1。检查通过后，工作流自动为所有玩家发布地址。[填写候选服务地址](https://github.com/minecraftlu/BlockCraftLauncher/edit/curseforge-service-config/candidate.json)

也可使用 Node.js 22 或更高版本在本目录运行 `node smoke.mjs https://实际服务域名`。脚本先查询服务健康和 Minecraft 分类，再检查 Clumps 项目的实时分发授权，最多下载 1 MiB，在内存中核验 JAR 文件头、字节数与官方 SHA-1。通过时输出项目 ID、文件 ID、大小与摘要。若项目的分发设置发生变化，可在命令末尾指定另一项允许分发的小型模组 ID。 [Clumps 的官方文件列表](https://www.curseforge.com/minecraft/mc-mods/clumps/files/all)

## 发布公共服务地址

维护者在 `curseforge-service-config` 分支的 `candidate.json` 中填写公开地址。自动检查通过后，工作流会将地址写入玩家读取的 `service.json`。服务尚未开通时，`service.json` 的 `serviceUrl` 为空字符串。维护者也可将它恢复为空字符串来暂停服务。[查看启用流程](https://github.com/minecraftlu/BlockCraftLauncher/blob/curseforge-service-config/CURSEFORGE_SERVICE.md)

```json
{"serviceUrl":"https://<已验证的服务域名>"}
```

提交后打开配置原文，确认文件可公开读取，且地址只含 HTTPS 协议与主机名。支持服务发现的启动器会读取这里的地址并自行追加 API 路径。专用构建也可通过 MSBuild 属性 `BlockCraftCurseForgeServiceUrl` 指定固定地址。 [查看公开配置](https://raw.githubusercontent.com/minecraftlu/BlockCraftLauncher/curseforge-service-config/service.json)

## 使用命令行部署

在本目录运行以下命令。离线测试需要 Node.js 22 或更高版本。部署使用已登录、可管理目标 Worker 的 Cloudflare 账号。

```sh
node --test
npx wrangler@4 deploy
npx wrangler@4 secret put CURSEFORGE_API_KEY
```

在交互式提示中输入已获 CurseForge 授权的密钥。也可以使用 Worker 控制台的加密 Secret。普通配置文件和客户端编译参数仅保存公开设置。Wrangler 配置使用 `CLIENT_RATE_LIMIT`，默认按 Cloudflare 节点和来源 IP 限流，阈值为每分钟 180 次请求。同一 Cloudflare 账号内的其他项目应使用不同的 `namespace_id`。提供 D1 的托管环境也可通过 `CURSEFORGE_QUOTA` 绑定配额数据库。

部署后按上文执行分类查询和真实 JAR 下载验证，再发布公共服务地址。

## 使用 D1 限流

将 D1 数据库绑定到 Worker 的 `CURSEFORGE_QUOTA`，并在启用服务前应用 [0001_quota.sql](migrations/0001_quota.sql)。同时配置两种限流绑定时，服务使用 `CLIENT_RATE_LIMIT`。可用一次分类查询验证数据库迁移及上游访问权限；`/health` 仅检查绑定和密钥配置。

D1 使用数据库的 UTC 时钟，每个固定分钟允许每个计数桶 180 次请求。数据库只保存 SHA-256 的前 20 位、分钟和计数，最多保留 1,048,576 个桶。哈希相同的地址共享额度。每次请求最多清理 8 个过期桶。数据库查询失败时，请求在访问 CurseForge 前结束。

## 客户端接口

| 方法 | 路径 | 用途 |
| --- | --- | --- |
| GET | `/health` | 查看配置状态 |
| GET | `/v1/categories`、`/v1/mods/search` | 模组分类、搜索、版本与加载器筛选 |
| POST | `/v1/mods` | 通过 JSON 字段 `modIds` 批量查询最多 32 个项目 |
| GET | `/v1/mods/{projectId}` | 项目详情 |
| GET | `/v1/mods/{projectId}/files`、`/v1/mods/{projectId}/files/{fileId}` | 文件分页与详情 |
| GET | `/v1/mods/{projectId}/files/{fileId}/download` | 流式下载已授权的 JAR，单文件上限 200 MiB |

查询保留官方 `data` 与分页结构。项目须为 Minecraft 的可用模组，且作者已允许第三方分发。每次下载都重新检查项目和文件归属、分发授权、文件大小和 SHA-1 元数据，再向官方获取下载地址。密钥仅用于官方 API 及其直接返回的官方 edge CDN 首次请求。服务向启动器返回文件内容，响应禁止缓存。

HTTP 401 表示官方拒绝凭据，403 表示项目分发或上游访问被拒绝，404 表示没有可用的项目或文件，429 表示需要按 `Retry-After` 等待后重试。

[CurseForge API](https://docs.curseforge.com/rest-api/) · [作者分发开关](https://support.curseforge.com/support/solutions/articles/9000207877-project-distribution-toggle) · [CDN 下载认证](https://blog.curseforge.com/introducing-api-key-authentication-for-curseforge-file-downloads/) · [Cloudflare 部署按钮](https://developers.cloudflare.com/workers/platform/deploy-buttons/) · [Workers 限流绑定](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/)
