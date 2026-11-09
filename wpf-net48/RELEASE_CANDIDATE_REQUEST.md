# BlockCraft Launcher v0.9.29 WPF Release Candidate 1

- Candidate tag: v0.9.29-wpf-rc.1
- Channel: **prerelease / candidate**, not Stable
- Target: Windows 10/11 x64, C# / WPF / .NET Framework 4.8
- Formats: Portable OneFile EXE, complete ZIP containing the update helper
- Development branch: feature/wpf-net48-alpha-v0.9.27
- Request date: 2026-11-10

## New PCL-like automatic update workflow

- Launcher startup checks official GitHub Releases every six hours by default, without delaying Minecraft launch.
- Stable / WPF preview channels are separated; Stable is the default and never selects NetEase room-lab or legacy Python assets.
- Main screen and update center show new version announcements and release description.
- User can opt into automatic background downloads, while installation always requires confirmation to close the launcher.
- GitHub-hosted assets must match exact recognized architecture/package names and the official release's SHA-256 manifest.
- Update packages are prepared in a staging area and smoke-tested before installation; no scripts or third-party mirror EXEs are executed.
- The standalone update helper waits until the WPF process exits, updates the actual original single EXE or the extracted ZIP application directory, and smoke-tests the replacement. A failed start restores old executable or DLL files.
- The new singlefile bootstrap now forwards all WPF CI test exit codes rather than masking failures.
- No saved Minecraft worlds, launcher authentication data or existing Python launcher should be modified; live game processes remain running.

## CI and release signoff

- [Windows CI #42682875674](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42682875674): success, 28 steps with ordinary and single-EXE tests.
- New updater static, simulated rollback, SHA-256, Safe ZIP extraction and release channel tests passed in Windows CI.
- Build the two formats again in a clean Windows runner; run every offline selftest and generate official SHA-256 manifests before publishing.
- Publish **only as GitHub prerelease v0.9.29-wpf-rc.1**. Do not mark Stable without real Windows end-to-end tests of both update paths, Chinese GitHub downloads, access controls and post-update relaunch.
- Users of v0.9.28 and older must **manually install v0.9.29 once** because previous binaries did not include an updater; after that the UI can install compatible future releases.

## Remaining limits

- In-app automatic checks and one-click update machinery are verified offline and compiled; they are not yet user-tested end-to-end with a later actual v0.9.30 release.
- A GitHub Release cannot update an app built from the Python/Go architecture in-place; old and new applications are kept separate.
- NetEase mobile room integration remains experimental and unsupported.

Details: wpf-net48/docs/V0929_AUTO_UPDATE.md
