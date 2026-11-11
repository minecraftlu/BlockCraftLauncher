# v0.9.40 stable candidate — preserve launcher functions and replace prior EXE

Goal authorized by launcher owner on 2026-11-11: 将正式版保持现有功能稳定情况下加入自动替换旧exe。

## Scope
- Preserve previous Minecraft Java launch, Microsoft authentication, downloads and repair, installed mods/icons, skin preview/DIY, profile/world handling, domestic/international network routing, and ZIP/OneFile distribution.
- Promote already-merged and Windows-tested v0.9.39 RC2 + v0.9.40 RC1 auto-updater logic: clicking 更新并替换旧版 uses external updater to replace the exact originally launched EXE; it retains original filename/location and shortcuts.
- Optional auto-install-on-normal-exit is OFF by default. When explicitly enabled it requires automatic checking, download, verified staged package, idle window, no game/download running and no system signout. After normal user exit it launches helper, checks SHA-256 and smoke-test, replaces in place and reopens the new WPF window.
- Keep backup until successful GUI relaunch confirmation. Failure retains or restores old EXE; successful update prunes launcher-created old backup and staged package only. Never scan Downloads, Desktop, Minecraft worlds/mods or manually saved EXEs.
- Custom local download cache is supported; source is validated, verified and marker-bound. The separate download folder does not change where the launcher EXE is installed.
- Preserve GitHub Releases stable/preview channel distinction and never overwrite existing tags. Windows Authenticode publisher signing remains a separate pending approval, not misrepresented by Sigstore.

## Stability requirements
1. Windows net48 CI static checks, both ZIP and single EXE compile, updater rollback, UI/skin/mod/download/saves/network offline regression, Sigstore and GitHub source attestation must all pass.
2. Old published v0.9.38 stable binaries are pinned by SHA-256: EXE 5d7e38b8e6eba68f2f5303bc062875fcee1c7fbd57778aa18d2fd055dc368afc, ZIP ebb14ef4eebf99835def74d46347ccae20438b0656c555c5d6cea3e7a9bf66a1.
3. Pre-release compatibility gate exercises old v0.9.38 UpdateService against candidate stable v0.9.40 EXE/ZIP and confirms strict hash/version compatibility.
4. Published stable v0.9.40 ZIP and OneFile must actually upgrade those pinned old v0.9.38 binaries, replace at the same path and display the new WPF main window.
5. Stable release workflow only runs from its isolated branch, checks the exact reviewed source/one-file approval trigger, refuses an existing tag, attests and verifies the exact assets before publication.

## Honesty
CI and a Windows hosted runner validate the release, but do not prove every user's GPU, third-party network or custom disk. The user authorized a stable release, not a claim to have personally tested v0.9.40 on their machine. Sigstore bundles and GitHub provenance are not Windows Authenticode certificates.

Keep v0.9.38 and earlier releases unchanged. The next stable tag is v0.9.40; release branch source is the only candidate.