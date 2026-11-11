# v0.9.39 Preview Candidate: Replace Old EXE and Reclaim Update Storage

## How upgrading works
- The main action 更新并替换旧版 verifies an official release and launches the external updater.
- The updater waits for the existing launcher to exit, replaces the EXACT original EXE with File.Replace, preserves the original file path, filename and shortcuts, then reopens the verified new version.
- Once the new main window has rendered, the helper removes successful-update backups and staged downloads and prunes obsolete OneFile extraction caches.
- ZIP installs update in their existing directories. User-created files, Minecraft games, mods, worlds, accounts and settings remain unchanged.
- If the update fails, the previous executable is restored. If relaunch has not been confirmed, rollback backups are kept.
- On later starts, known GUID-named staged- and runner- update folders older than 48 hours are cleaned, after checking they are not running.
- Existing OneFile launches may also remove their own matching legacy rollback EXEs older than 48 hours.

## Scope and safety
- No arbitrary EXEs in Downloads, Desktop or game directories are deleted: manually saved old versions need user choice.
- Only launcher-generated backup names and LocalAppData update work folders are touched. No automatic elevation is attempted.
- The original EXE filename does not change, even if it includes an older version string, so shortcuts keep working.
- 临时试用（不替换） remains a temporary noninstalling option, not a permanent upgrade.
- The stable v0.9.38 release is not overwritten. Test in a new preview first.

## Required validation
- Windows updater --selftest: exact owned backup whitelist, failed-update rollback, directory updater rollback and window-relaunch marker.
- WPF --download-selftest: only launcher-owned stage/runner directories qualify for cleanup.
- Windows net48 x64 compilation, normal ZIP and single EXE smoke/UI/download tests, GitHub provenance and Sigstore checks.
- After publishing an isolated preview, actual upgrade should confirm original EXE location is reused and no confirmed-success backup remains.