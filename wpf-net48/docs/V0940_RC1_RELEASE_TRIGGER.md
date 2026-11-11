# BlockCraft Launcher v0.9.40 RC1 · Custom Download Path Release Gate

PREVIEW_TAG: v0.9.40-wpf-rc.1
REVIEWED_SOURCE_SHA: 778f3ef139a61ffdd1e7ec2074d96757b1c90461
WINDOWS_CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43011569208
WINDOWS_CI_STATUS: completed / success / 38 steps
FEATURE_PR: https://github.com/minecraftlu/BlockCraftLauncher/pull/32

User request: 自定义下载路径功能.

- The user can browse and set a Minecraft game installation root under 启动设置. Existing game instances do not move automatically.
- A separate 下载缓存目录 under 下载设置 can be selected from local C/D/E disks, with a writable-path probe; reset-default and open-folder controls are available.
- Custom download cache paths route Modrinth mrpack archive downloads, official auto-updater EXE/ZIP staging and SHA-256 manifests into user-selected root-owned subdirectories.
- A protected updater receives the selected staged updates root, retains same-path replacement of original launcher EXE and previous-version rollback, and removes only matching GUID-named/marked staging folders on confirmed success.
- The cache root itself, other user EXEs, Minecraft instances and saves are never scanned for deletion; custom paths must not point to disk roots, Windows, user profile or Minecraft installation.
- Older settings remain compatible with existing LocalAppData storage. The published v0.9.38 stable remains unchanged.
- The signed RC1 Windows build must pass full WPF UI, offline download, in-place updater, skin, mod, SHA-256, Sigstore and GitHub provenance gates and publish exactly one GitHub prerelease with six expected assets.

Note: CI validates all changes but the user's selected external drive and the first real custom-path update still require post-publication testing. Do not claim every possible drive or network setting has already been tested.