# BlockCraft Launcher v0.9.31 · Unified Search UI & Failure-Only Update Dialogs (RC1)

- Candidate tag: `v0.9.31-wpf-rc.1`
- Channel: **GitHub prerelease / candidate**, not Stable
- Target: Windows 10/11 x64, WPF, C# .NET Framework 4.8
- Distribution: OneFile EXE and complete ZIP, both with standalone update helper
- Development branch: `feature/wpf-net48-alpha-v0.9.27`
- Request date: 2026-11-10

## Included features

- Remove normal "确认安装 BlockCraft Launcher 更新 / 是 / 否" modal from the **new** launcher when the user explicitly clicks `下载并安装`. Verified update continues to close the old WPF, applies files and automatically opens the new window; true update failures still show errors and rollback prompts.
- Do not force-install or close Minecraft when automatically detecting or pre-downloading updates. Keep explicit separate temporary `直接打开新版` preview action.
- Every main page now shares the same Chinese navigation hierarchy, page heading, dark/light adaptive search colors, cards, labels and control sizing.
- Minecraft game version download: search name, categorized release/snapshot filtering, quick chips and version cards.
- Modrinth mod / shader / resource packs: name, source, game version, loader, category, quick-sort, paginated icon-result cards.
- CurseForge: opt-in MCIM, real mod cover images/screenshots, current-page version and category filters, downloads sorting and attribution; no unapproved file URLs.
- Modrinth modpacks: keyword, game version and sort filters, page navigation, proper result covers; original independent install behavior unchanged.
- Download tasks: search name and kind, state filters, result count, item progress/speed and selected task details.
- Preserve saved game files, Microsoft accounts, versions, installed mods, and network/multiplayer behavior.

## Windows CI & release gates

- [Windows WPF build #42693187901](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42693187901): **Success**, 29 successful build and regression steps (normal ZIP and OneFile).
- Static test enforces that normal update install contains **no `MessageBox.Show`** yet preserves `StartExternalInstaller`, application exit and `ShowError` failure path.
- `--ui-layout-selftest` verifies all navigation and download search controls, offline CurseForge filtering, layouts and bindings; existing download/auth/save and update rollback offline checks still run.
- Build a new EXE and ZIP in clean Windows runner; run release checks, independent updater selftest, WPF/OneFile tests and SHA-256 manifest before publishing.
- Publish only **v0.9.31-wpf-rc.1 prerelease**. Do not merge to main, mark Stable, or overwrite v0.9.30 RC2 without real end-to-end Windows testing.
- IMPORTANT: v0.9.30 RC2 binary still includes its own old confirmation dialog. Installing v0.9.31 using RC2's updater will show the old prompt once. The new no-dialog behavior applies when v0.9.31 is subsequently used to install a **future** compatible release. A direct manual install of RC1 avoids the old prompt immediately.

## Pending real-device acceptance

Chinese Windows network download/Modrinth/CurseForge search and images, SHA-256, actual upgrade to future version without confirmation, game compatibility, light/dark DPI, error rollback under permissions/antivirus; only then qualify future Stable.

Full specification: `wpf-net48/docs/V0931_UNIFIED_UI_AND_SILENT_UPDATES.md`.
