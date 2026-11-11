# v0.9.41 Stable — source-locked cancellation lifecycle hotfix release trigger

OWNER_RELEASE_AUTHORIZATION: true
RELEASE_TAG: v0.9.41
REVIEWED_SOURCE_SHA: d88175c1513f9a1f833f3915cb781208e76c53f1
WINDOWS_CI_SUCCESS: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43021515506
MAIN_FIX_CI_SUCCESS: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43020678584
FIX_PR: https://github.com/minecraftlu/BlockCraftLauncher/pull/35

User's issue: BlockCraft Launcher displayed a fatal error `CancellationTokenSource 已释放`. The source has at least one demonstrable defect: server directory requests disposed their CancellationTokenSource but left the shared field set. A later page navigation could call Cancel on that disposed source and shut down WPF.

- The stable patch repairs server-search completion by clearing its CTS field before Dispose and adds safe detach-before-cancel behavior to all launcher page navigation request scopes.
- When a visible WPF window receives a narrowly identified stale CancellationTokenSource exception, the exception is logged and treated as recoverable; all unrelated exceptions remain handled normally.
- The PCL layout WPF offline regression injects already disposed CTS references across pages to check no fatal UI crash.
- Keep prior features and existing game worlds/account profiles, downloader behaviors, skin/DIY, Modrinth/CurseForge, resource icons, update replacement and custom download path unchanged.
- Actual exception stack from the user's machine has not been inspected, so do not assert this is the only source of the crash or universal problem resolution.

Release guards: this one acceptance/trigger document must be the only difference from the exact reviewed candidate at d88175c1513f9a1f833f3915cb781208e76c53f1. The new stable tag must be unused; the release must build both EXE/ZIP, run Windows CI and in-place updater selftest, pass SHA-256/Sigstore/GitHub provenance, publish signed immutable assets, and successfully upgrade pinned public v0.9.40 OneFile and ZIP to v0.9.41 in their original paths, reopening WPF main window.

Keep v0.9.40 and all earlier stable/preview tags unchanged. Sigstore verifies integrity and build provenance, not Windows Authenticode publisher identity.