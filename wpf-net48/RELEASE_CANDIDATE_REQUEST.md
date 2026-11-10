# BlockCraft Launcher v0.9.36 WPF RC4 · Public Skin Manual Download

- Candidate tag: `v0.9.36-wpf-rc.4`
- Channel: **GitHub prerelease / Preview ONLY**
- Source branch: `feature/public-skin-manual-download-v0.9.36-rc4`
- Target Windows: Windows 10/11 x64, C# WPF, .NET Framework 4.8
- Distributions: OneFile EXE and Alpha ZIP with updater
- Release request date: 2026-11-10

## Changes

- Download → Community Resources → Skin remains an independent mod-style browsing/download screen; More → Tools → Skin DIY remains an independent editor with optional explicit official Microsoft upload.
- Users may manually download single publicly accessible skin PNGs from the supported Mosaicary / DP-Plugins or Mojang skin APIs even when the skin does not have an open-content license. This is NOT a claim of copyright permission: UI shows an explicit rights warning and requires acknowledgement for each unknown / restricted-license download or edit.
- Free-licensed-only filter remains the default, and the all-public catalog must be chosen expressly. Unknown / ARR/NC / found skins are clearly labelled and must not masquerade as CC0 / free-license content.
- Original author, source link and license warning are preserved in .png.source.txt sidecars; no automated mass downloads, access-control bypass, quota bypass or hidden-content scraping.
- Files are delivered via HTTPS, bounded PNG, file-size/shape verification, stage-and-move without overwriting user skins, and shared download task queue with cancel/retry/progress.

## Quality gates

- [x] Windows WPF / OneFile build, all online skin offline selftests, UI navigation, skin import and download regression, updater rollback / actual main-window restart passed at https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42819863620
- [x] Earlier compile/test run of rights warning UI and source checks passed at https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42819816533
- [ ] Controlled GitHub prerelease workflow must independently pass clean WPF + OneFile build, static validation, offline security and updater regression, real WPF reopen test and post-build SHA256 before publishing.
- [ ] Manual real-network tests for Mosaicary/DP-Plugins access in mainland China and abroad, author license content, and real Microsoft account upload still required.

## Release safety

Publish prerelease=true and latest=false for v0.9.36-wpf-rc.4 only. Preserve stable v0.9.35 and the existing RC1/RC2/RC3 releases. Stable update channel must not receive this prerelease. No SignPath Foundation code signature until approval. This release does not grant rights to redistribute or commercially use third-party skins.

Docs: wpf-net48/docs/V0936_PUBLIC_SKIN_DOWNLOAD_RC4.md.
