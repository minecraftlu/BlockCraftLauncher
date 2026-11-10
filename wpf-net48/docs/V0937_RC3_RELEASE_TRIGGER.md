# v0.9.37 RC3 preview release trigger · 360° skin preview

This isolated release branch is based on the verified RC2 preview and includes the
360-degree interactive WPF Minecraft skin viewer from merged PR #25.

## Gating
- Source preparation commit: 4972802a5c4926f025714d38a2f670e8d45556eb
- Branch Windows CI: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42865055229
- Result before triggering: completed, success
- Release tag is exclusively v0.9.37-wpf-rc.3 (pre-release, never stable)
- The dedicated release workflow must rebuild and pass static checks,
  Minecraft UI/skin selftests, updater compatibility, SHA-256 validation,
  Sigstore and GitHub provenance checks before publishing.

## User-visible changes
- Mouse drag 360-degree skin preview in DIY and online skin details
- Mouse wheel zoom and reset-view control
- Proper Java 64x64 UV mapping for classic Steve and slim Alex arms
- Toggle existing outer skin layers and live edits
- All previous v0.9.37 RC2 mod icon and download safeguards retained

Windows real-device mouse interaction and different GPU renderers still
require preview-user acceptance before a future stable release.

This file is intentionally the only path that triggers the isolated RC3
prerelease publish job; do not re-run the trigger after the tag exists.
