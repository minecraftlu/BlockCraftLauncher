# BlockCraft Launcher v0.9.32 WPF · Minecraft Skin DIY Studio (RC1)

- Candidate tag: `v0.9.32-wpf-rc.1`
- Channel: **prerelease / candidate**, not Stable
- Windows 10/11 x64 · WPF .NET Framework 4.8
- Distributions: OneFile EXE and complete ZIP with standalone updater
- Branch: `feature/wpf-net48-alpha-v0.9.27`
- Request date: 2026-11-10

## User-requested feature: full custom Minecraft Java skin DIY

- New sidebar **皮肤 DIY** page under management, plus visible homepage shortcut.
- Real 64×64 pixel UV canvas with selectable head, torso, left/right arms and legs; independent base and outer-layer masks (hat, sleeves, jacket, trousers); 4–12× zoom and optional grid.
- Tools: brush, eraser, eyedropper/right-click picker, flood fill; 1–5 pixel brush; preset colors, custom HEX, RGB sliders and alpha opacity; Ctrl+Z/Y undo/redo.
- Classic Steve (4px) / slim Alex (3px) model choice, with front/back 2D composite preview and optional outer layer visibility. This is NOT a 3D viewer; don't claim one.
- Local PNG import (64×64 or legacy 64×32 with mirrored left limb conversion); export sanitized 64×64 PNG; safe local library at `%APPDATA%\BlockCraftLauncher\Skins`. Uncommitted skin changes persist on tab changes; autosaved hidden recovery draft can restore lost edits.
- Optional, explicit upload to the user's existing authenticated Minecraft Java Microsoft account: show clear account and model confirmation; post to `https://api.minecraftservices.com/minecraft/profile/skins` with short-lived game bearer token, only official HTTPS, no redirects and no automatic retry. Offline play and local skin exports do **not** imply public skin synchronization.
- Preserve v0.9.31's unified PCL-inspired Chinese UI, download searches, CurseForge thumbnails, network routes, update success/no-popup, Microsoft login and game saves.

## Tests and release quality gate

- [Latest complete Windows WPF CI #42699257061](https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42699257061): **success, 30 steps**. Normal ZIP and OneFile builds, skin editor UI navigation, standalone `--skin-selftest` on both EXEs, existing downloader/auth/save/update rollback and real WPF window render checks.
- Skin offline selftest covers UV selection, second layer, brush/fill, undo/redo, Steve/Alex preview, standard PNG read/write, legacy 64×32 conversion, malformed PNG and path traversal rejection.
- Official upload is **offline request-structure tested only**. Test inspects exact POST endpoint, model form field, PNG bytes and rejects no token or invalid PNG. It NEVER uses a real Microsoft account or contacts the upload API.
- Controlled release CI must independently run clean Windows build, full static regression suite, OneFile and normal EXE `--skin-selftest`, WPF UI tests, updater file rollback and SHA-256 manifests, and upload GitHub assets only on all-green.
- Publish strictly as **`v0.9.32-wpf-rc.1` prerelease**; don't merge into Stable/main or overwrite older tags until real Windows/official skin upload acceptance has been performed.
- Known limitation: painting is an accurately zoomed flat UV map with front/back live preview, not a rotatable 3D editor. Java online skin appearance requires successful Mojang profile upload. Avoid promising offline/server-wide sync.

## Manual follow-up

- User should test actual brush, layers, PNG import/export, template recovery, Alex model/skin appearance, Windows DPI/scaling and library save.
- When using a real Microsoft Java account, upload is **opt-in** and must be tested with explicit user authorization; if it fails, verify official skin management using the standard Java launcher.
- Full documentation: `wpf-net48/docs/V0932_SKIN_DIY_STUDIO.md`.
