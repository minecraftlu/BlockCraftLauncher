# BlockCraft Launcher v0.9.36 RC5 · Java Server Discovery

- Candidate tag: `v0.9.36-wpf-rc.5`
- Channel: **GitHub prerelease / preview only**, never stable
- Branch: `feature/server-discovery-preview-v0.9.36-rc5`
- Platform: Windows 10/11 x64, WPF .NET Framework 4.8
- Distribution: one-file EXE and full Alpha ZIP including standalone updater
- Requested on: 2026-11-10

## New features

- Under Multiplayer, provide three distinct sections: server favorites, **Find Servers**, and LAN worlds.
- User-initiated public Minecraft Java server search across MC Monitor, minecraftserve.rs, and MSID using publicly documented, unauthenticated read-only APIs.
- Text/address/version filters, cached online-only status, player-count sort, pagination, and multi-source deduplication with graceful provider failures.
- Safe result cards show server name, description, address, cached online status, version, player counts and source. One-click favorite to existing local collection, copy address, open source website, and user-authorized direct Java join using the selected installed profile.
- Directory list does not use Minecraft/MS tokens, automatically connect to public IP addresses, scan the Internet, or claim to be a NetEase mobile room discovery service.
- Preserve original Minecraft and mod downloads, RC4 public skins, independently categorized skin DIY, existing local favorites, LAN discovery, Microsoft login, and updater rollback.

## Validated gates

- [x] Windows source build and normal / single-file packaging: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42823812526
- [x] Static server discovery assertions, no-network `--server-search-selftest` on WPF and OneFile, API payload parser / private address protections.
- [x] Real WPF layout and narrow-window navigation, skin and mod selftests, GUI startup and package smoke, updater rollback.
- [ ] Controlled release workflow must independently compile and run its entire offline test suite on the final candidate commit, then build release asset hashes and publish a GitHub prerelease. Fail closed on any failed gate.
- [ ] Real-network verification of external directory connectivity (including mainland China and overseas), actual server login, account requirements and incompatible-game-version prompts remains to be done on users' Windows computers.

## Publishing policy

Only publish GitHub release tag `v0.9.36-wpf-rc.5` with prerelease=true, latest=false. Keep stable `v0.9.35` and previous RC1/RC2/RC3/RC4 untouched. Stable update users must not be automatically moved to a preview channel.

This Windows launcher is still unsigned pending SignPath Foundation approval. This preview does not promise all public Minecraft servers are indexed, or that Minecraft Java can directly join NetEase mobile-only rooms.

Detailed docs: wpf-net48/docs/SERVER_DISCOVERY_RC5.md.
