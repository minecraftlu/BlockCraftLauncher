# Code signing policy — BlockCraft Launcher (Windows WPF)

> **Status (2026-11-10): NOT SIGNED. SignPath Foundation approval has not been obtained.** This document describes the proposed process only. No release is claimed to carry a SignPath signature until a signed release is built and verified.

**Free code signing provided by [SignPath.io](https://signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).** This attribution is a description of the intended program, not a claim of current participation.

## Project, maintainer and scope

- Public source repository: https://github.com/minecraftlu/BlockCraftLauncher
- Supported WPF source directory: [wpf-net48](../)
- WPF license: [MIT](../LICENSE) (other repository components and their licenses must be checked separately before the application is submitted).
- Project: BlockCraft Launcher, an open-source Minecraft Java Edition launcher for Windows x64 (.NET Framework 4.8).
- Developer display name in the application: **晓银 (Xiaoyin)**, a nickname only. It is not the verified legal identity or the Authenticode certificate subject.
- GitHub maintainer: [minecraftlu](https://github.com/minecraftlu).
- Author/committer: minecraftlu (subject to actual repository access permissions).
- Reviewer: minecraftlu, who reviews outside contributions before merging.
- Release approver: minecraftlu. Every SignPath release signing request requires manual approval.

## Proposed signed artifacts and trust chain

Only binaries built **from the project's own source** in this repository may be submitted for signing:

- Portable one-file executable: `BlockCraftLauncher-v<version>-WPF-OneFile-Windows-x64.exe`.
- The launcher WPF executable in the ZIP distribution, if covered by the eventual approved artifact configuration.
- The updater executable, only if separately approved and built as part of the reviewed workflow.

Third-party Minecraft binaries, mod loaders, downloaded mods, resources, skins, game files, and upstream DLLs **are not to be signed as if they were this project's own software**.

Release workflow: [wpf-net48-controlled-release.yml](../../.github/workflows/wpf-net48-controlled-release.yml). It currently builds and tests on GitHub-hosted Windows runners, creates checksums, and publishes stable or candidate release assets. **It does not yet submit signing requests**, and changes to the workflow are required only after SignPath approves the project and provides configuration.

The intended release-signing sequence is:

1. Review the source and verify that the release tag matches the updater's version.
2. Build the project in GitHub Actions on a GitHub-hosted Windows runner, with the existing checks intact.
3. Upload the unsigned artifact as a GitHub Actions artifact and submit its GitHub artifact ID through the official SignPath GitHub Action with origin verification.
4. Manually approve each signing request as the designated approver, verify the certificate subject and signature, and **fail the signed release if signing fails**.
5. Publish **only the verified, signed** release assets, calculate SHA-256 checksums **after signing**, and confirm the signed EXE is the exact file linked in GitHub Releases.

When enabled, Windows Authenticode should show **SignPath Foundation** as the verified publisher, not the nickname 晓银. A valid signature does not guarantee Microsoft SmartScreen will suppress every warning.

## Privacy and security disclosures

See the [Privacy and network activity statement](PRIVACY.md). The launcher can communicate with external providers when the user signs in, downloads game files/mods and checks for launcher updates. This policy should be checked against the current source code before application submission.

Maintainers must use multi-factor authentication for GitHub and SignPath access, limit signing credentials to GitHub Actions secrets, avoid embedding credentials in the repository, follow SignPath's origin-verification requirements, and announce system changes. The portable build can be removed by deleting its extracted files; separate installed Minecraft data or user files may remain until removed by the user.

## Verification for end users

After signed releases are actually available, Windows users may check a downloaded EXE using PowerShell:

```powershell
Get-AuthenticodeSignature .\BlockCraftLauncher-v0.9.35-WPF-OneFile-Windows-x64.exe | Format-List
Get-FileHash .\BlockCraftLauncher-v0.9.35-WPF-OneFile-Windows-x64.exe -Algorithm SHA256
```

The version in the example is for illustrating command syntax; **v0.9.35 is not claimed to be signed**. A successful future signed release must report `Status: Valid`, signer `SignPath Foundation`, and match the SHA-256 value published for that exact release asset.

## Prerequisites before application or activation

- [x] WPF component contains a MIT license in `wpf-net48/LICENSE`.
- [x] A Windows stable EXE release and a GitHub-hosted release workflow exist.
- [ ] Confirm license and redistribution permissions for all distributed components and document any excluded components.
- [ ] Verify the privacy statement and external dependencies against current source and binary output.
- [ ] Confirm GitHub multi-factor authentication and secure contributor/reviewer permissions.
- [ ] Submit the application personally as the project's GitHub maintainer at https://signpath.org/apply.
- [ ] Obtain SignPath Foundation acceptance; acceptance is discretionary and depends on public project reputation.
- [ ] Configure the SignPath GitHub app, SignPath organization/project/signing-policy details and the API token in repository secrets.
- [ ] Integrate official signing action into the release workflow, preserve its tests, verify signed artifacts, and update release notes.
