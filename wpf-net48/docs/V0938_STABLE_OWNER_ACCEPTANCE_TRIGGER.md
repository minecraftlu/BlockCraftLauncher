# BlockCraft Launcher v0.9.38 Stable Patch — owner acceptance record

REAL_DEVICE_ACCEPTANCE: true
RELEASE_TAG: v0.9.38
REVIEWED_MAIN_SHA: 2040ea21b49b2a5c6c09bf87ee46e8d687c252b2

Owner reported in this conversation on 2026-11-11 that all existing launcher features operated normally ("都没问题"), and earlier that RC5 skin 3D preview displayed correctly on their Windows device. That is owner-reported acceptance, not proof of every possible third-party server, network or GPU configuration.

The originally published v0.9.37 release successfully passed Windows compilation, normal ZIP upgrade, integrity, Sigstore signatures and build provenance. However its post-release OneFile upgrade check discovered an internal cache tag v0.9.36 instead of the published version v0.9.37; the helper found no v0.9.37 cache and did not complete the final OneFile relaunch assertion.

v0.9.38 corrects that internal bootstrap version, keeps all previously accepted launcher features, adds a build-time tag consistency assertion, and has completed:
- Branch Windows CI success: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42891312637
- Main Windows CI success: https://github.com/minecraftlu/BlockCraftLauncher/actions/runs/42891788961
- Reviewed patch pull request: https://github.com/minecraftlu/BlockCraftLauncher/pull/29

Release this exact reviewed main patch only after a fresh Windows build, both package selftests, stable updater preflight from v0.9.36, SHA-256, Sigstore and GitHub provenance verification. The post-release upgrade test MUST confirm the new v0.9.38 OneFile cache and reopened WPF window. Do not overwrite the already published v0.9.37; retain it for traceability.
