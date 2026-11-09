# BlockCraft Launcher v0.9.30 WPF Release Candidate 2 · Direct Open

- Candidate tag: `v0.9.30-wpf-rc.2` (NEW; do not overwrite RC1)
- Channel: **prerelease/candidate**, not Stable
- Windows 10/11 x64 · WPF .NET Framework 4.8
- Single EXE and full ZIP with standalone updater
- Development branch: `feature/wpf-net48-alpha-v0.9.27`
- Request date: 2026-11-10

## Next Stable feature: 直接打开新版

- Update center gains `直接打开新版（不覆盖旧版）`.
- After normal GitHub Release check and SHA-256 verification, user confirms direct-open. The new EXE/ZIP app runs from a private update staging folder, **not replacing the original installed launcher**.
- Only after the newly opened WPF main window reaches `ContentRendered` and acknowledges its random token does the previous launcher close.
- The newly opened window explicitly labels itself as a temporary preview; desktop shortcut still starts the old installed EXE.
- Existing `下载并安装 · 完成后自动打开新版` remains the permanent update path, with old application backup, startup selftest and rollback.
- OneFile and full ZIP verify all eligible WPF assets; ZIP direct-open compares **every extracted file** against the SHA-256-verified original archive and refuses extra or modified payloads.
- Keep Microsoft login, Minecraft version downloads, Forge/Fabric/NeoForge, CurseForge artwork, game saves and Java multiplayer behavior untouched.

## Tested and gating

- [Windows CI #42688924777](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42688924777) passed 29 steps, including normal and OneFile UI/window tests, direct-preview SHA-256 checks and temporary-file security checks.
- The controlled release workflow must perform a fresh clean Windows build, file rollback test, normal and OneFile tests, release SHA-256 generation and upload only on **complete success**.
- Tests include actual WPF main window rendering for both installed-update relaunch and direct-open, not just successful CreateProcess.
- **Do not label Stable yet.** Manual Windows testing remains required for real online download, direct-open from v0.9.30 RC1 to RC2, permanent EXE/ZIP upgrade, low permissions/antivirus and game/account/save preservation.
- The existing v0.9.30 RC1 GitHub Release remains available for rollback.

Details: `wpf-net48/docs/V0930_DIRECT_OPEN.md`.
