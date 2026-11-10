# BlockCraft Launcher v0.9.37 Stable — owner acceptance

REAL_DEVICE_ACCEPTANCE: true
RELEASE_TAG: v0.9.37
REVIEWED_MAIN_SHA: 709f6640c6c8a4702c2460b08fa14df2daf04dcd

2026-11-11 — The project owner confirmed in this conversation that all requested launcher functions work without issues ("都没问题"). The owner separately confirmed the RC5 360-degree skin preview renders correctly on their Windows computer. These are user-reported acceptance results, not independently instrumented multi-country or cross-GPU measurements.

The release candidate was built and tested by GitHub Actions on Windows:
https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42878239742

Publish v0.9.37 stable exactly once from main using the isolated full release gate.
- Require current tag v0.9.37 and no previously existing tag.
- Rebuild Windows x64 single EXE and ZIP, exercise all offline UI, updater, Minecraft and skin tests.
- Confirm SHA-256, Sigstore and GitHub provenance; check v0.9.36 upgrade preflight.
- Keep v0.9.37 RC5 as prerelease; do not delete it, alter v0.9.36 or modify user game data.
- Do not treat CI success as evidence all third-party mod servers are permanently reachable.
