# BlockCraft Launcher

BlockCraft Launcher is a Minecraft Java Edition launcher. The Windows x64 WPF release is built with C# and .NET Framework 4.8.

- [**Download v0.9.40 Stable (Windows x64)**](https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.40)
- [Latest releases](https://github.com/minecraftlu/BlockCraftLauncher/releases)
- [Windows WPF documentation](wpf-net48/README.md)
- [WPF component MIT license](wpf-net48/LICENSE)
- [Free Sigstore/Cosign signatures and release verification](wpf-net48/docs/FREE_RELEASE_SIGNING.md)
- [Authenticode code signing policy (SignPath application declined)](wpf-net48/docs/CODE_SIGNING_POLICY.md)
- [Privacy and network access statement](wpf-net48/docs/PRIVACY.md)

**Publisher / developer naming:** The launcher uses the developer nickname **晓银 (Xiaoyin)**. This nickname is not an Authenticode verified publisher identity. The SignPath Foundation application was declined due to insufficient project popularity (per the maintainer). This project instead adds no-cost **Sigstore/Cosign keyless release signatures** and **GitHub build-provenance attestations** to future releases. These verify files and provenance, **not** Windows Authenticode publisher trust. SmartScreen may still show an unknown publisher. Confirm signatures and hashes for each release; old releases have no retroactive Sigstore signatures.

The current stable version supports checked, in-place EXE updates at the original installation path, optional automatic installation after a normal launcher exit, and cleanup of launcher-created obsolete update files after a confirmed relaunch. These features do not delete Minecraft worlds, mods, installed profiles or unrelated downloaded EXEs.

The WPF component's license is located at `wpf-net48/LICENSE`. Licensing status for other components at the repository root should be reviewed independently before claiming the entire repository is covered by the same license.
