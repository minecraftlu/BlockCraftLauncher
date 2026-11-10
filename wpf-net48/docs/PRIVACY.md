# Privacy and network activity — BlockCraft Launcher (Windows WPF)

**Scope:** Windows WPF distribution in [wpf-net48](../), maintained at https://github.com/minecraftlu/BlockCraftLauncher. **Last updated:** 2026-11-10.

BlockCraft Launcher is a Minecraft Java Edition launcher. It handles locally selected game instances and offers Microsoft account authentication, game and mod downloads, game version management, skin functions and launcher updates.

## Network access

The application may make requests to external services for features including:

- **Microsoft authentication:** Microsoft account sign-in, tokens and associated authorization flows.
- **Minecraft game resources:** game manifests, libraries, runtime and asset downloads from Minecraft/Mojang/Microsoft-related services and configured mirrors.
- **Mod / resource discovery:** Modrinth, CurseForge and other selected providers or mirrors, depending on enabled features.
- **Launcher releases:** GitHub Releases for update checks and release file downloads.

These services have their own privacy policies. The availability of mirror services and each provider's handling of network metadata and credentials is outside the project's direct control.

## Local data and choices

The application may store configuration, account login state or tokens and downloaded game content locally to support launcher functions. Users should use its account sign-out and settings functions where available, and remove local data manually when necessary. Deleting a portable launcher EXE does not automatically remove separately downloaded Minecraft files or user game saves.

No assertion is made here that the launcher never sends network requests without a direct button click, or that it never uses optional telemetry, until the current release source and all dependencies have been audited. Project maintainers must review this statement and document any additional data collection or transfer before applying for SignPath Foundation signing.

## Security and verification

Use only release assets from [official GitHub Releases](https://github.com/minecraftlu/BlockCraftLauncher/releases). Verify the SHA-256 checksums published with the release. As of 2026-11-10 this project has not obtained a SignPath Foundation signing certificate; do not assume a downloaded file is Authenticode-signed. For signing status, read the [Code signing policy](CODE_SIGNING_POLICY.md).

For security or privacy concerns, use the repository's [Issues page](https://github.com/minecraftlu/BlockCraftLauncher/issues) without posting passwords, tokens or private account data.
