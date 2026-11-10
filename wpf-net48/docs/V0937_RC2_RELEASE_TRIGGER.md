# BlockCraft Launcher v0.9.37 RC2 release trigger

Publish an isolated signed GitHub prerelease (not stable) only if the
dedicated Windows workflow completes all offline and live public-metadata
checks. RC2 improves icons of locally installed Forge/Fabric/NeoForge/Quilt
mod JARs, supports GitHub author's avatar fallback for repo search cards,
adds resource detail cover/fallback, and retries transient GitHub Releases
download failures while retaining checksum and loader verification.

- Target: `v0.9.37-wpf-rc.2`
- Candidate branch: `release/v0937-rc2`
- Previous published prerelease `v0.9.37-wpf-rc.1` remains available.
- Existing published stable `v0.9.36` remains untouched.
- Proven green feature branch: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42860013771
- Build the EXE and Alpha ZIP using this exact source revision.
- Verify SHA-256, Sigstore Cosign and GitHub build provenance, publish as
  `--prerelease --latest=false` only after checks pass.
- Never skip project license, compatibility or file-integrity checks.
- Real Chinese-mainland and overseas ISP download speeds, actual mod
  compatibility and user Windows environments require end-to-end testing.

This trigger is intentionally separate from the mainline stable workflow.
