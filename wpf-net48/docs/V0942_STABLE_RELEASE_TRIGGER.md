# BlockCraft Launcher v0.9.42 stable release — USB disk same-volume paths

OWNER_RELEASE_AUTHORIZATION: true
RELEASE_TAG: v0.9.42
REVIEWED_SOURCE_SHA: 2e84266c3902c720c3adde8620453c4324abed89
REVIEWED_SOURCE_CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43025794752
MAIN_CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43025247097
FEATURE_PR: https://github.com/minecraftlu/BlockCraftLauncher/pull/37

The user requested a fix so the BlockCraft Launcher game root and download cache can both be stored on the same external USB hard drive.

The reviewed code has passed 38 Windows CI steps; the same-volume acceptance includes drive-root selection, distinct sibling directories on one volume, settings persistence, rejection of overlap, UNC and drive-relative paths, and compatibility with fixed or removable drive classification.

Stable release changes only the new current version number, OneFile bootstrap build tag, Windows package names, assembly version, signed workflow version, pinned upgrade test, UI and validation code already reviewed. It MUST NOT modify Minecraft game worlds, mod folders, user archives, settings or legacy releases.

One-click external disk option in 启动设置 / 下载设置: pick E:\ or E:\BlockCraftLauncher and set E:\BlockCraftLauncher\Minecraft and E:\BlockCraftLauncher\Downloads, while keeping separate directories and user-controlled Save action. Existing instances do not migrate.

Keep v0.9.41 stable and earlier releases downloadable. New version still offers original-path replacement of launcher EXE and a rollback helper. Verify public stable v0.9.41 OneFile and ZIP pinned SHA-256 digests upgrade to v0.9.42 at the original install paths and reopen the new WPF main window.

Keep SHA-256, Sigstore and GitHub build provenance verification. Sigstore is NOT Windows Authenticode; exact USB hardware unplug/replug and user-specific mobile-disk paths are not testable on the hosted runner and require user testing.