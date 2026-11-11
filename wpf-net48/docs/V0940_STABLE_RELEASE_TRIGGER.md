# v0.9.40 Stable — exact-source owner-authorized release trigger

OWNER_RELEASE_AUTHORIZATION: true
RELEASE_TAG: v0.9.40
REVIEWED_SOURCE_SHA: 2e38163ec7fad70431456e033178195b0219f807
CI_TESTED_SOURCE_SHA: c706f8f2135d313f4a4fd5f10f28592076792c54
WINDOWS_CI_SUCCESS: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43014373165
MAIN_FIX_CI_SUCCESS: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43013230146
FIX_PR: https://github.com/minecraftlu/BlockCraftLauncher/pull/33

The launcher owner explicitly requested a new official stable release that preserves existing functions while adding automatic replacement of the old launcher EXE. This is release authorization, NOT a claim of hands-on Windows device acceptance for v0.9.40.

- Keep all current Minecraft/Modrinth/CurseForge/skin/account/profile features, including user-selectable game and download paths.
- Use the stable channel v0.9.40 and ship single EXE and complete ZIP.
- Clicking 更新并替换旧版 updates the original executable IN PLACE, preserving the original path, filename and shortcut.
- The optional automatic install-on-normal-close setting is OFF unless explicitly enabled; when enabled, the update is installed only after download verification and safe normal exit (no live game, active task or system signout).
- Retain old executable on failed check; after the new WPF window confirms successful launch, remove only updater-owned previous-EXE backups and staged packages.
- A release may be created only after exact reviewed source lock, full Windows CI, immutable tag check, stable migration preflight from pinned v0.9.38 binaries, SHA-256, Sigstore signatures and GitHub provenance.
- Finally, test from actual published v0.9.38 ZIP and OneFile using the newly published v0.9.40, checking original-path replacement and new main window.
- Never overwrite the published v0.9.38 or the v0.9.40 RC1 prerelease. Do not delete Minecraft/user files.

External provider availability and every custom drive cannot be proven by CI; avoid claiming universal compatibility. Sigstore is not a Windows Authenticode publisher signature.