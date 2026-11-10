# RC4 独立预览版发布门禁

版本：v0.9.37-wpf-rc.4 (prerelease, not stable).

本分支已验证 Windows GitHub Actions 编译、打包、皮肤 UI / 旋转 / 透明外层离线测试、下载器与自动更新基础自检、GitHub/Sigstore 来源证明。

准备提交：62aff5592c1c73aebb01dd347584ff05f46bd919
成功 Windows CI：https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42868877291
合并修复 PR：https://github.com/minecraftlu/BlockCraftLauncher/pull/26

RC4 测试版增加：
- 修复 3D 皮肤部分透明外层遮挡本体导致的碎块/悬浮渲染。
- 扩大预览，优化光照、相机、背景。
- 自动旋转/暂停，双击恢复视角，滚轮缩放和俯仰角限制。
- 在线皮肤详情显示/隐藏外层，皮肤 DIY 保留编辑同步。
- 继承已有 v0.9.37 RC3 功能与模组图标、下载校验。

禁止覆盖 RC3 / v0.9.36 稳定版。受控发布任务会重建并校验文件哈希及 Sigstore/GitHub 来源证明后创建唯一 RC4 prerelease tag。发布不意味着不同显卡上已完成鼠标实机验收。
