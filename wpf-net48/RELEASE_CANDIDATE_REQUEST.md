# BlockCraft Launcher v0.9.36 WPF RC1 · Online Skin Search and Download

- Candidate tag: `v0.9.36-wpf-rc.1`
- Channel: **GitHub prerelease / preview**, never stable
- Target branch: `feature/skin-search-beta-v0.9.36`
- Target Windows platform: Windows 10/11 x64, WPF, .NET Framework 4.8
- Distributions: OneFile EXE and complete Alpha ZIP with standalone updater
- Requested: 2026-11-10

## New beta functionality

- **Mojang player skin search:** search Minecraft Java usernames or UUIDs, retrieve current public skins and Steve/Alex model information.
- **Community skin search:** search third-party DP-Plugins public catalog by keyword, with attribution/available license information.
- **Preview and download:** preview selected skin, download sanitized valid Minecraft PNG into the existing BlockCraft skin library, or import into the pixel editor after confirming unsaved changes.
- Existing skin DIY, Microsoft account login, explicit optional official skin upload, game downloads, auto-update rollback, mod icons, saves and resource management remain intact.

## Build and test gates

- [x] Online skin search changes were merged into `main` via PR #13.
- [x] Versioned build and preview package names match `v0.9.36-wpf-rc.1`, including Alpha ZIP and OneFile EXE.
- [x] **Full Windows build** https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42812457691 **passed**, including MSBuild + NuGet restore, static contracts, both distributions' skin offline checks, window-render/relaunch test, download and account selftests, rollback verification, CI release assets, and checksums.
- [ ] The controlled candidate release workflow must perform its own clean Windows build, static checks, offline dual-distribution selftests, real WPF window test, updater rollback check, SHA-256 generation and publication. The workflow MUST fail closed on any failure.
- [ ] After publication, confirm actual GitHub prerelease assets and that switching an installed v0.9.35 launcher to the Preview update channel discovers this candidate.
- [ ] End-user network acceptance: Mojang player name, DP-Plugins community keyword search, PNG save/preview, China/overseas connectivity, optional Minecraft account upload (explicit authorization).

## Release policy

Only `v0.9.36-wpf-rc.1` may be published as **prerelease**, not as stable. v0.9.35 remains the stable release and must not be overwritten. Set GitHub release `prerelease=true`, `latest=false`. The current launcher is **unsigned** because SignPath Foundation approval is pending; do not imply Windows verified publisher identity. The separate stable release gate still requires approval from `main` and real Windows acceptance.

Documentation: `wpf-net48/docs/V0936_ONLINE_SKIN_PREVIEW.md`.

Any live network limitations belong in the prerelease notes, not hidden as successful tests.
