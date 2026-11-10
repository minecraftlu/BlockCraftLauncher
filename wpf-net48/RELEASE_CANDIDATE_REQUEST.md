# BlockCraft Launcher v0.9.36 WPF RC2 · Licensed Skin Catalog and Mod-style Downloads

- Candidate tag: `v0.9.36-wpf-rc.2`
- Channel: **GitHub prerelease / preview**, never stable
- Source branch: `feature/skin-catalog-beta-v0.9.36-rc2`
- Target Windows: 10/11 x64 · C# WPF · .NET Framework 4.8
- Distributions: standalone OneFile EXE + complete Alpha ZIP with updater
- Release request date: 2026-11-10

## Highlights

- Search/browse Mosaicary's freely licensed public skin library with category, model, sort, source, author and CC license filters; only fetch thumbnails for a limited visible page.
- Include DP-Plugins community skin discovery and separate Mojang player skin search; a public player texture does NOT imply an open license.
- Redesign result lists like the Modrinth/CurseForge download center: thumbnail + name, author, model, permission, downloads; right side preview/details; download PNG to existing library or use in skin DIY.
- Save original source / license attribution in adjacent .png.source.txt. Reject oversized / malformed PNGs, untrusted redirect and not-licensed community downloads.
- Preserve Minecraft account login, optional confirmed official skin upload, DIY tools, game/mod downloads, mod icons, saves, update rollback and direct-open behavior.

## CI gates and acceptance

- [x] GitHub Windows WPF CI successfully compiled and ran full offline regression suite: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42815074281
- [x] All skin license and Mosaicary parser offline selftests, source validation and UI layout checks passed.
- [x] Update tag is v0.9.36-wpf-rc.2 and the updater's preview channel selects newer RCs.
- [ ] Controlled prerelease workflow itself must clean-compile WPF/OneFile, validate all offline selftests, real WPF window reopen behavior, SHA-256 generation, and publish only if every gate passes.
- [ ] Manual network testing on mainland China and overseas networks: catalog pages, downloads, attribution, preview, Mojang/DP-Plugins availability, optional Microsoft skin upload.

## Strict release policy

Publish **only** GitHub prerelease v0.9.36-wpf-rc.2, with prerelease=true, latest=false. Do not change or replace v0.9.35 stable or rc1 preview tags. Existing v0.9.35 or RC1 users must intentionally select the Preview update channel to update. This build is NOT SignPath-signed because foundation approval is still pending. Do not pretend that it removes SmartScreen warnings.

Full notes: wpf-net48/docs/V0936_SKIN_CATALOG_RC2.md.
External catalogs cannot guarantee containing every freely licensed skin on the internet; 'openly visible' is not identical to an open content license.
