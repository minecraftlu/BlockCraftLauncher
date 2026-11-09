# BlockCraft Launcher v0.9.27 WPF Release Candidate 1

- Candidate tag: `v0.9.27-wpf-rc.1`
- Channel: **prerelease / candidate**, not Stable
- Target: Windows 10/11 x64, WPF, .NET Framework 4.8, optional single EXE
- Branch: `feature/wpf-net48-alpha-v0.9.27`
- Request date: 2026-11-10

## Justification

- User reported that the unified PCL-inspired UI and its used functions work correctly on their Windows machine.
- User separately confirmed a successful Microsoft account interactive login.
- Previous Windows CI build [#42673355087](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42673355087) succeeded on WPF compile, UI navigation, account offline tests, download, safe save restore and both launcher packaging modes.
- Candidate contains an update checker fix for repositories without a published Stable WPF release; offline tests distinguish legacy Python releases, NetEase room-lab previews, malicious URLs and incompatible WPF Alpha assets.

## Safety and remaining acceptance

- This request only authorizes a **prerelease**; it does not attest to all loader variants, token refresh, gameplay, game-world integrity, multiple display DPIs or two-machine LAN tests.
- Both new EXE and ZIP are built in a clean GitHub Windows runner; GitHub release upload happens only after all static and offline tests pass and a SHA-256 manifest is produced.
- Existing Python launcher, Microsoft account data, Minecraft game files and saved worlds must remain unchanged.
- Do not advertise NetEase mobile room compatibility as ready.

The release workflow watches this file for candidate creation. Future candidate revisions must use a fresh version tag rather than overwriting or silently replacing published assets.
