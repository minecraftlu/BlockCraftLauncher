# v0.9.40 RC1 Post-release Live Upgrade Check

PUBLIC_RELEASE: https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.40-wpf-rc.1
SIGNED_RELEASE_CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43011868550
SIGNED_RELEASE_CI_STATUS: completed / success / 40 steps

Use the exact, immutable published RC2 ZIP/OneFile EXEs and the RC1 v0.9.40 packages and public SHA-256 manifests.
Check that the RC2 updater detects RC1 from GitHub preview channel, downloads, validates and replaces its old install at the same path, keeps unrelated ZIP files, relaunches the RC1 main window and deletes completed-update stage/legacy OneFile backup EXEs.
Note: RC2 does not yet expose configurable update staging; custom-path updates within RC1 are guarded by new offline updater selftests and still require a subsequent newer release for a full live upgrade demonstration.
Do not modify the stable v0.9.38 release or downloaded user files. This run is read-only outside isolated test directories.