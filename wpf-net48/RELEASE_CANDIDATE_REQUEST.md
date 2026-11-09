# BlockCraft Launcher v0.9.28 WPF Release Candidate 1

- Candidate tag: v0.9.28-wpf-rc.1
- Channel: **prerelease / candidate**, not Stable
- Target: Windows 10/11 x64, WPF, .NET Framework 4.8, single EXE / ZIP
- Development branch: feature/wpf-net48-alpha-v0.9.27 (the existing WPF PR)
- Request date: 2026-11-10

## Changes to validate before next Stable

- China-priority downloads for supported Mojang/BMCLAPI resources, original official fallback, and single-flight automatic mirror probes.
- Long-file download timeout extended to 20 minutes, metadata streaming capped at 12 MiB with 18-second cancellation, verified SHA-1 cache reuse and route-specific partial-file markers to avoid corrupted cross-CDN resumes.
- Optional MCIM-backed Modrinth acceleration with official fallback and HTTPS domain allowlist. No OAuth or account traffic is mirrored.
- CurseForge project list with real authors' logos, preview screenshots, download counts and version hints. All artwork URLs are restricted to approved CDN domains; missing images fall back to blank placeholders.
- Full-screen navigation still uses BlockCraft's original WPF PCL-inspired layout; original login, download, mods, saves and multiplayer workstreams remain available.
- CI #42678383098 confirmed all 27 Windows build and offline regression steps including normal and one-file download tests.

## Candidate vs. Stable quality gate

- The user's earlier successful Microsoft interactive login and use of the previous WPF UI do NOT establish real Chinese network throughput or real CurseForge/Modrinth download correctness.
- Before Stable, run domestic ISP download benchmarking, download interruption/resume, valid Minecraft installs, Forge/Fabric/NeoForge compatibility and screenshots/creator links on a real Windows machine.
- This file requests ONLY a distinct GitHub **prerelease** with SHA-256 manifests, built from a clean Windows runner after every automated test passes.
- Do not change or overwrite v0.9.27-wpf-rc.1, the legacy Python release, Minecraft files, or accounts. NetEase room interoperability remains unsupported.

Validation notes: wpf-net48/docs/V0928_CHINA_DOWNLOAD_AND_CURSEFORGE.md
