# BlockCraft Launcher v0.9.30 WPF Release Candidate 1

- Candidate tag: `v0.9.30-wpf-rc.1`
- Channel: **prerelease/candidate**, not Stable
- Target: Windows 10/11 x64, C# / WPF / .NET Framework 4.8
- Formats: portable single EXE and complete ZIP, both with the standalone updater
- Development branch: `feature/wpf-net48-alpha-v0.9.27`
- Date: 2026-11-10

## Requested feature: automatically open new launcher window after update

- After a user confirms the final installation prompt, old WPF exits; standalone updater applies verified replacement and starts the original installed **new** launcher executable.
- New WPF actually renders its `MainWindow`, brings it to the foreground, sets a non-modal «更新完成» status and emits a one-time per-launch random confirmation file.
- Update helper waits for the true WPF child process to report a rendered window, including when the single EXE outer bootstrap exits before its child.
- When a window cannot be confirmed, no duplicate launches, no overwrite of an already running new version. Instead prompt for manual launch at the known actual application EXE path.
- Retain v0.9.29: official SHA-256 updates, stable/preview channels, opt-in auto download, independent rollback helper, preserved Minecraft worlds/authentication, and original game process unaffected.

## Automated testing completed

- [Windows CI #42685988330](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42685988330): **success**, compiles and exercises both launcher forms, normal feature regressions, isolated updater file rollback and actual `ContentRendered` window acknowledgment.
- Both the multi-file and OneFile Windows executables are launched with a one-time test token. The WPF child emits a render proof after its real window is shown; the harness verifies correct PID, timestamp and success exit code.
- Update helper tests asynchronous window-ready marker detection, missing-marker handling, and EXE and ZIP file rollback without executing fake dummy binaries.
- Controlled release workflow reruns all checks and generates independent SHA-256 digest manifests before GitHub Release.

## Do not mistake CI for full Stable acceptance

- Candidate must be visibly tagged **prerelease**. No Stable/automatic update channel promotion before real Windows end-to-end installation from v0.9.29 RC1 to v0.9.30 RC1.
- Required user verification: actual new main window appears automatically and shows the new version, old launcher closes, no duplicated window, Microsoft login remains usable, game and saved worlds remain intact, both install types where feasible.
- No unsupported NetEase mobile-room claims. Previous RCs and Python launcher must not be replaced.

Details: `wpf-net48/docs/V0930_AUTO_RELAUNCH.md`.
