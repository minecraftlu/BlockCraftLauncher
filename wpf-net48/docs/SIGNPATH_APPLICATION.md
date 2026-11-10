# SignPath Foundation application draft — BlockCraft Launcher WPF

**Applicant:** Maintainer of the public GitHub repository [minecraftlu/BlockCraftLauncher](https://github.com/minecraftlu/BlockCraftLauncher). The display nickname **晓银 (Xiaoyin)** is *not* an asserted verified legal identity.

**Project:** BlockCraft Launcher (Windows WPF / .NET Framework 4.8).

**Source repository:** https://github.com/minecraftlu/BlockCraftLauncher

**Current public release:** https://github.com/minecraftlu/BlockCraftLauncher/releases/tag/v0.9.35

**Component license:** MIT, see https://github.com/minecraftlu/BlockCraftLauncher/blob/main/wpf-net48/LICENSE (link works after merging if the file remains there).

**Code signing policy:** https://github.com/minecraftlu/BlockCraftLauncher/blob/main/wpf-net48/docs/CODE_SIGNING_POLICY.md (after merging).

**Privacy and network statement:** https://github.com/minecraftlu/BlockCraftLauncher/blob/main/wpf-net48/docs/PRIVACY.md (after merging; maintainer review required).

## English application description

> BlockCraft Launcher is a free and open-source Minecraft Java Edition launcher for Windows. Its current WPF edition is written in C# and targets .NET Framework 4.8. The application helps users manage Minecraft installations, access Microsoft account sign-in, and download game versions and mods.
>
> I maintain the public source code in the GitHub repository above. The Windows WPF application is distributed under the MIT license located in the WPF source directory. GitHub Actions builds and tests release artifacts on GitHub-hosted Windows runners, and releases them through the repository's official GitHub Releases page.
>
> I am requesting consideration for SignPath Foundation's free open-source signing program so users can verify that future Windows executable releases were built from this repository and have not been altered. I understand that the certificate would identify **SignPath Foundation** as the legal signing publisher, not my nickname. I am willing to enforce release provenance checks, protect signing credentials, and manually approve each release signing request under the Foundation's policy.
>
> The application is not yet approved, and the currently published releases are not claimed to be signed.

## Maintainer-only checklist

- [ ] Review all statements above against the current project and dependencies.
- [ ] Resolve licensing of non-WPF repository files, if required by the Foundation.
- [ ] Confirm two-factor authentication is enabled on GitHub.
- [ ] Confirm the privacy statement accurately represents outbound network activity, local token storage and installed components.
- [ ] Submit through https://signpath.org/apply using the maintainer's GitHub identity. **Do not submit passwords, tokens, identity documents, or private keys in an issue or PR.**
- [ ] Await approval; the Foundation may reject projects without sufficient independent, verifiable reputation.
- [ ] If accepted, use the [official GitHub integration guide](https://docs.signpath.io/trusted-build-systems/github) and integrate signing **after compilation/tests but before checksums and publication**.
- [ ] Rebuild and verify that only signed release assets and their post-signing hashes are published; do not publish an unsigned fallback as a signed release.
