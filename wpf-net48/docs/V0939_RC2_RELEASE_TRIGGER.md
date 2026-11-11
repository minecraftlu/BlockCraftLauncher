# v0.9.39 RC2 · Automatic In-Place Launcher EXE Replacement Preview

CANDIDATE_SHA: 8e81dae4ca0024ab23da78f95c3ceb9a576c2476
WINDOWS_CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/43007137982
CI_STATUS: completed / success / 38 steps
FEATURE_PR: https://github.com/minecraftlu/BlockCraftLauncher/pull/31

User asked to keep improving automatic version replacement of the old launcher EXE and avoid accumulating multiple old EXE files.

- Adds opt-in auto-install-on-normal-exit in Update Settings, with auto-check and auto-download prerequisites. Existing users default off.
- The already signed and checked new package is installed only when the user chooses to exit, not while playing, downloading, preview-opening or shutting down Windows.
- External update helper replaces the original EXE at its exact path, verifies candidate, relaunches new WPF window, and removes launcher-owned stale backups and staging only after success; keeps rollback protection on failure.
- Preserves all Minecraft world/game data and manually downloaded launcher EXEs on all drives.
- RC2 promotion test downloads pinned, signed RC1 EXE and ZIP and verifies both update to RC2 from the same old installation path, relaunch a new window, remove the completed staged package, and leave no obsolete OneFile backup.
- Publish as prerelease tag v0.9.39-wpf-rc.2 only, keeping v0.9.38 stable and RC1 intact.
- Source code and Windows tests passed before triggering this isolated release workflow. Do not bypass SHA-256, Sigstore or GitHub build provenance.

Release only after full Windows packaged-build gate; real Windows/GPU/account performance in individual environments should still be tested.