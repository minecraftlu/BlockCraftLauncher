# BlockCraft Launcher 发行者身份说明

本变更只修改 Windows File Properties / VersionInfo 的 Company、Authors、Product、Copyright，并不冒用 Microsoft 或其他软件厂商的数字证书。

公司名称（描述字段）：晓银（昵称，不等于经过 CA 核验的法律身份）。

在 Windows UAC / SmartScreen 的“已验证发行者”字段中显示真实身份，需要合法的 Authenticode 代码签名证书、私钥与签名过程。没有证书时可能继续显示“未知发布者”。Sigstore / GitHub 来源证明同样不能代替 Authenticode。

正式版 v0.9.44 的既有二进制发布文件和 SHA-256 不得被重新上传覆盖。此变更通过 Windows CI 验证后用于下一个版本。
