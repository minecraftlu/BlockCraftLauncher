# CurseForge HMCL-style resource browsing (development test)

This is an interaction upgrade of BlockCraft Launcher, not a copy of HMCL assets or trademarks.

- The CurseForge navigation page keeps its BlockCraft blue/white layout, search cards and project artwork.
- Search filters for Minecraft version, mod loader (Fabric / Forge / NeoForge / Quilt), sort and next/previous pages go directly to CurseForge's search endpoint; mod loader filter requires a game version. Category is a **current-page only** filter.
- The project details page includes an optional action to retrieve compatible file versions for the selected Minecraft instance. Users may choose an exact file or auto-install the newest compatible version. Required dependencies, version validation, SHA-1 verification, restricted download hosts and rollback protections remain enabled.
- The source of results remains the user-owned official CurseForge API when the local Windows user variable `BLOCKCRAFT_CURSEFORGE_API_KEY` is present, otherwise the manually enabled MCIM optional mirror. A key is NEVER baked into the EXE, workflow files, logs or source.
- The private API key shown in a screenshot should be rotated and never published. Public builds need separate authorization before distributing key-backed CurseForge service.

## Build and verification

Windows workflow `wpf-hmcl-curseforge-test.yml` builds the full WPF and OneFile binaries, checks static constraints, runs offline download/CurseForge API-route and UI layout selftests, then attaches a GitHub Actions test artifact (no real key). Live installation with the user's own credentials must be verified on the owner's machine before considering a stable release.
