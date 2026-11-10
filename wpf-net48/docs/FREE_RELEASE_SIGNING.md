# 免费开源签名和发布来源验证

## 提供什么、不能提供什么

BlockCraft Launcher 采用 **Sigstore Cosign keyless 签名 + GitHub Artifact Attestations（SLSA）+ SHA-256**。
公开 GitHub 仓库无需采购 CA 证书、硬件密钥或上传私人签名密钥。

正式发布工作流以 GitHub Actions OIDC 短期身份签名 EXE 和 ZIP 文件。
每个文件另附一个 .sigstore.json 签名包；GitHub 另行存储构建来源证明。
SHA-256、Sigstore 签名、GitHub 构建来源三项都必须验证通过，否则正式版不得发布。
常规 Windows 构建工作流也会登记 GitHub 构建来源证明。

**这不是 Windows Authenticode 证书。** Windows UAC / SmartScreen
仍可能提示“未知发布者”；开发昵称“晓银”不会变为 Windows 认证发行者。

**自动更新安全边界：** 现有启动器自动更新依然使用 SHA-256 和回滚保护，
但尚未在应用内部嵌入 Sigstore / GitHub 证明的加密验证机制。
因此 **不能宣称自动更新已强制验证 Sigstore 发布者身份**。
本阶段提供发布前强制验证和最终下载文件的用户端验证。
后续内部强制验证需要可信根锚定、失联容错及单独安全审查。

## Windows 用户如何验证新版本

以下以 v0.9.36 为例，仅适用于采用新流程发布成功的正式版。
旧版 v0.9.35 和先前的 RC 预览版不具有这些签名文件。

1. 从 https://github.com/minecraftlu/BlockCraftLauncher/releases
下载并放入同一个目录：
  - BlockCraftLauncher-v0.9.36-WPF-OneFile-Windows-x64.exe
  - BlockCraftLauncher-v0.9.36-WPF-OneFile-Windows-x64.exe.sigstore.json
  - BlockCraftLauncher-v0.9.36-WPF-net48-Windows-x64.zip
  - BlockCraftLauncher-v0.9.36-WPF-net48-Windows-x64.zip.sigstore.json
  - RELEASE-SHA256SUMS.txt
2. 安装官方开源 Cosign CLI（https://docs.sigstore.dev/cosign/system_config/installation/）
和 GitHub CLI（https://cli.github.com/）。验证时需要网络来取得可信根和 GitHub 来源证明。
3. 获取本仓库的 build/verify-sigstore-release.ps1，运行：

~~~powershell
.\build\verify-sigstore-release.ps1 -ReleaseTag v0.9.36 -DistPath 'C:\Users\你的用户名\Downloads\BlockCraftRelease'
~~~

脚本严格检查 manifest 中两个文件的 SHA-256，确认签名包属于原始二进制文件，
并确认 OIDC 签名来自本仓库受控正式发布工作流 main 分支；
最后再使用 GitHub CLI 校验 SLSA 构建证明来源。

手工验证单文件 EXE 的示例：

~~~powershell
cosign verify-blob .\BlockCraftLauncher-v0.9.36-WPF-OneFile-Windows-x64.exe --bundle .\BlockCraftLauncher-v0.9.36-WPF-OneFile-Windows-x64.exe.sigstore.json --certificate-identity 'https://github.com/minecraftlu/BlockCraftLauncher/.github/workflows/wpf-net48-controlled-release.yml@refs/heads/main' --certificate-oidc-issuer 'https://token.actions.githubusercontent.com'

gh attestation verify .\BlockCraftLauncher-v0.9.36-WPF-OneFile-Windows-x64.exe --repo minecraftlu/BlockCraftLauncher --signer-workflow github.com/minecraftlu/BlockCraftLauncher/.github/workflows/wpf-net48-controlled-release.yml --source-ref refs/heads/main
~~~

不要用 .* 这样的通配身份规则代替对仓库、工作流及分支的精确匹配。

## 发布工作流和兼容性

.github/workflows/wpf-net48-controlled-release.yml 仍保留 owner
手动触发、v0.9.36、仅 main 稳定版、真实设备验收等原有门禁。
构建、自测、SHA-256 清单准备结束之后，按以下顺序执行：

1. Cosign 无密钥签名原始 EXE 与 ZIP，并产出可下载的 sigstore.json 文件。
2. 使用 actions/attest 对这些相同文件生成 GitHub SLSA 来源证明。
3. 使用 verify-sigstore-release.ps1 强制检查哈希、Cosign 和来源证明。
4. 检查成功才将原有 EXE、ZIP、哈希清单和新增签名文件发布到 Releases。

签名流程不修改 EXE 和 ZIP 的字节或名称，不改变现有自动更新通道与发布版本号。

官方参考：
- https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations
- https://docs.sigstore.dev/quickstart/quickstart-ci/
- https://cli.github.com/manual/gh_attestation_verify
