# v0.9.39 RC2 post-release real upgrade verification

RELEASE_TAG: v0.9.39-wpf-rc.2
RC2_PUBLIC_RELEASE: https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.39-wpf-rc.2
RC2_RELEASE_CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43007519882
RC2_RELEASE_STATUS: completed / success / 40 steps

Launch a fresh Windows hosted run that downloads the actually published RC2 EXE, ZIP and SHA-256 checksums, then downloads published v0.9.39 RC1 OneFile and ZIP packages with exact pinned SHA-256 identities. Run check_v0939_rc2_promotion.ps1 Preflight and Live.

The live test must confirm both old RC1 installations download and install RC2, original OneFile EXE path remains the same, the new WPF main window opens, ZIP users' unmanaged files are intact, and successful-update staging/obsolete rollback EXEs are removed.

Do not modify the stable v0.9.38 release, manually delete user files, or treat source-only tests as proof the real upgrade succeeded.