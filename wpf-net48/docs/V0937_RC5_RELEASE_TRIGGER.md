# v0.9.37 RC5 · 3D Skin Compatibility Preview Release Gate

Candidate source commit: 0d79b9aeaa2b3dd8712df30b91bb92f4fa424e0a
Candidate Windows CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42873012107
CI conclusion before creating this trigger: completed / success
Fix merged to main: https://github.com/minecraftlu/BlockCraftLauncher/pull/27

Release exclusively as v0.9.37-wpf-rc.5 prerelease. Do not modify RC4 or the v0.9.36 stable release.

3D fixes:
- Render the six Minecraft base-skin body parts as actual ARGB-colored pixel geometry, avoiding a broken WPF atlas material sampler that caused the user's RC4 avatar to appear mostly transparent.
- Outer-layer texture uses cropped per-face bitmaps and only draws nonzero-alpha texels.
- Preserve Steve and Alex arms, rotation, zoom, double-click reset and auto-spin.
- CI now checks real WPF rendered pixel coverage using the offline teal skin, in addition to UV / transparency / version / security tests.
- Preserve RC4 UI, downloads, mod icons, Microsoft login and launcher auto-update behavior.

The isolated release workflow must rebuild EXE and ZIP, run Windows tests, verify hashes, GitHub provenance and Sigstore bundles before publishing. Real-world user GPU and their exact external skin still require post-release verification.
