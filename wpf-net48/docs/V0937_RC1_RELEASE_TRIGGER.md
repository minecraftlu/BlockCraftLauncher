# BlockCraft Launcher v0.9.37 RC1 — release trigger

This file triggers the isolated GitHub Actions prerelease workflow on the
`release/v0937-rc1` branch. It is not a stable release.

- Target prerelease: `v0.9.37-wpf-rc.1`
- Windows x64 single EXE and Alpha ZIP
- Preview channel only; stable `v0.9.36` remains intact
- Publish only after all compiler, offline regression, checksum, Sigstore and
  GitHub provenance checks pass in the same run
- Release source: the exact commit of this branch (not the moving `main`)
- Existing RC branch CI success: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42856176795
- Re-publishing an already created tag is prohibited; do not overwrite an RC.

This candidate is for voluntary Windows and Minecraft real-device testing;
GitHub Actions cannot certify mainland-China and international network
performance, licensing of every third-party mod, or a verified Windows publisher.

Retry 1: replace nonexistent-gh-release-view guard with a nonfailing git ls-remote tag check; prior run published nothing.
