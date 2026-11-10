# BlockCraft Launcher v0.9.36 WPF RC3 · Separate Skin Download Center

- Candidate tag: `v0.9.36-wpf-rc.3`
- Channel: **GitHub prerelease / preview only**
- Branch: `feature/skin-download-category-v0.9.36-rc3`
- Target: Windows 10/11 x64 · WPF .NET Framework 4.8
- Assets: standalone OneFile EXE and Alpha ZIP containing standalone updater
- Request date: 2026-11-10

## Changes

- Skin search, paginated catalogs, downloads and author/license detail cards now live under **Download → Community Resources → Skin** beside mods, resource packs, shaders and modpacks.
- **More → Tools → Skin DIY** remains an independent screen for painting, saved-skin library, importing/exporting PNG and explicit Microsoft Java skin upload.
- Both screens share the same editor/document instance and cross-link, so unsaved edits survive navigation.
- Saving an online skin now queues a job in the same **Download Tasks** manager as mods and games, with progress, cancellation, manual retry, bounded PNG, atomic non-overwriting save and source/attribution sidecar.
- The supported catalogs are Mosaicary published/open-license skins (paged), DP-Plugins public skins (up to 200 in the provider listing), opt-in public found skins as preview only when no reuse license exists, and direct Mojang player lookup. Never claim to have downloaded every skin on the internet or bypass rights or source quotas.
- Preserve original resource downloads, existing game profiles, the skin editor, Microsoft sign-in, updater rollback and all other stable behavior.

## Build gates

- [x] Verified Windows WPF and OneFile build, all offline UI navigation and skin-safety checks, task management, full regression tests, standalone updater rollback and relaunch: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42818357176
- [ ] The controlled prerelease workflow must independently build, run both EXE selftest suites, verify actual WPF window reopen, generate SHA-256, and publish **only after success**.
- [ ] Actual Mosaicary and DP-Plugins connectivity, source licenses, user downloads and real Minecraft account upload must be tested by beta users on their own computers.

## Release policy

Publish `v0.9.36-wpf-rc.3` as GitHub prerelease=true and latest=false. Keep v0.9.35 stable and prior RC1/RC2 releases unchanged. Existing preview users may update RC1/RC2 to RC3 via Settings > Update Settings > Preview. Stable users must explicitly opt in to preview updates.

Current Windows executables are **not SignPath-signed** (Foundation approval pending), and Windows SmartScreen may still warn.

Read the feature and license notes: wpf-net48/docs/V0936_SKIN_NAVIGATION_RC3.md.
