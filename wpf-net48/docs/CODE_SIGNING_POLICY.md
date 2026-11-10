# Code signing policy — BlockCraft Launcher (Windows WPF)

> **Status (2026-11-10): NO WINDOWS AUTHENTICODE CERTIFICATE.** The maintainer reports that SignPath Foundation declined the application due to insufficient public popularity. No release is claimed to carry a SignPath Authenticode signature.

**Alternative at no cost:** [Sigstore/Cosign + GitHub provenance + SHA-256](FREE_RELEASE_SIGNING.md). This verifies file origin without a Windows-trusted Authenticode publisher. The SignPath discussion below is a historical proposal, not an active signing service or approval claim.

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

Release workflow: [wpf-net48-controlled-release.yml](../../.github/workflows/wpf-net48-controlled-release.yml). It builds and tests on GitHub-hosted Windows runners and creates SHA-256 checksums. The new flow must generate and verify **Sigstore keyless bundles** and **GitHub build provenance** before publishing. It does **not** submit SignPath signing requests. SignPath re-application would require fresh approval and a separately reviewed workflow.

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
- [x] Submit the application personally as the project's GitHub maintainer at https://signpath.org/apply (maintainer reports it was reviewed and declined).
- [ ] Obtain SignPath Foundation acceptance; current application was rejected because public project popularity was considered too low.
- [ ] Configure the SignPath GitHub app, SignPath organization/project/signing-policy details and the API token in repository secrets.
- [ ] Integrate official signing action into the release workflow, preserve its tests, verify signed artifacts, and update release notes.
