# CurseForge 公共模组服务

部署完成后，项目维护者将 Worker 的公开 HTTPS 地址填入此分支的 `candidate.json` 并提交。地址示例为 `https://my-worker.my-subdomain.workers.dev`，末尾无斜杠。此文件只填写公开网址。私密 API Key 在 Worker 的 `CURSEFORGE_API_KEY` Secret 中配置。

验证工作流会检查服务健康、CurseForge 模组分类以及一个真实模组的分发许可、文件大小和 SHA‑1。成功后工作流自动更新 `service.json`，已安装 v0.9.56 RC1 或更高兼容版本的玩家可以打开「CurseForge 官方」使用服务。检查失败时，已有公开地址保持原值。需要暂停服务时可直接将 `service.json` 恢复为 `{"serviceUrl":""}`。

[部署与验证说明](https://github.com/minecraftlu/BlockCraftLauncher/blob/v0.9.56-wpf-rc.1/backend/curseforge/README.md)
