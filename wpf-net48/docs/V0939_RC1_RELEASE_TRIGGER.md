# v0.9.39 RC1 · In-place EXE updater preview release gate

PREVIEW_TAG: v0.9.39-wpf-rc.1
SOURCE_COMMIT: da613c4acb60c040daaaa8bd265af8d42277942a
WINDOWS_CI_SUCCESS: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42991247216
MERGED_FEATURE_PR: https://github.com/minecraftlu/BlockCraftLauncher/pull/30

User asked that updates replace the old launcher EXE instead of leaving many EXEs occupying storage.

- The update helper replaces exactly the executable originally launched, preserving its path, filename, shortcut behavior and game data.
- Once the new WPF main window is acknowledged, remove updater-created old EXE backup, completed package stage and obsolete single-file caches.
- Keep rollback backups if the update or relaunch did not finish; best-effort cleanup must never cause post-launch rollback.
- Launch-time cleanup is limited to old GUID-named staged/runner update directories. The app does not delete unrelated EXEs in Desktop or Downloads.
- Keep v0.9.38 stable unchanged. RC1 is a separate prerelease, not yet accepted for stable promotion.
- The dedicated release pipeline must rebuild and pass Windows WPF, updater, UI, mod and network tests, SHA-256, Cosign and GitHub build provenance checks.

User acceptance testing after release should update from v0.9.39 RC1 to a future new preview and confirm same original EXE path, no leftover successful-update backup, and safe rollback on simulated error. Updating from older v0.9.38 uses that older helper for its first update; the new helper/cleanup logic takes effect on subsequent updates.