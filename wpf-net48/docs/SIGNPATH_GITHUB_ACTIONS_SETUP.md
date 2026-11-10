# SignPath Foundation → GitHub Actions setup (after approval)

**Project status (2026-11-10): application submitted, awaiting approval. Do not configure a fake certificate or publish an unsigned EXE as signed.**

The optional [manual signing workflow](../../.github/workflows/wpf-net48-signpath-sign.yml) is a **post-approval proof-of-signing test**. It deliberately produces only a verified GitHub Actions artifact and does not change GitHub Releases or the launcher updater.

## SignPath Dashboard / owner steps after acceptance

1. Finish the SignPath account invitation and enforce two-factor authentication for GitHub and SignPath. **Do not share private API tokens or certificates with anybody, including in issues or chat.**
2. In SignPath, confirm the project repository URL exactly equals `https://github.com/minecraftlu/BlockCraftLauncher`.
3. Add the predefined `GitHub.com` trusted build system to your SignPath organization, **link it to the project**, and install/authorize the [SignPath GitHub App](https://docs.signpath.io/trusted-build-systems/github) for this repository. The app verifies build origin and audit log data.
4. Configure the project's default artifact configuration to sign the Windows x64 PE **OneFile EXE only**. Ensure product and version metadata restrictions match `BlockCraft Launcher` and the built version. Do not sign upstream DLLs with this project certificate.
5. Configure a release-signing policy with **origin verification**, GitHub-hosted Windows runners and `main` as the allowed source branch; require a manual release-approver decision for every request.
6. Create a SignPath API token with the **least privilege necessary to submit**, not approve, signing requests.

## GitHub repository configuration

Under **Settings → Secrets and variables → Actions**, create:

| Kind | Name | Value |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | Your SignPath signing-request submission token (never add to source code) |
| Variable | `SIGNPATH_ORGANIZATION_ID` | The organization ID from SignPath |
| Variable | `SIGNPATH_PROJECT_SLUG` | The project slug from SignPath |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | The approved release-signing policy slug |

Do not invent these values or fill them from the developer's nickname. The Foundation certificate's publisher remains **SignPath Foundation**, while the project's display author may remain **晓银**.

## Manually verify after approval

1. Review and merge the proof-of-signing PR. Do not enable it before acceptance and complete trusted-origin configuration.
2. From GitHub Actions, choose **WPF SignPath verified EXE (manual, post-approval only)** and **Run workflow** on `main`. Select the explicit approval confirmation only if the SignPath project is actually approved/configured.
3. The workflow runs static checks, builds and selftests the normal OneFile EXE, uploads the **unsigned binary as a GitHub Actions artifact**, and submits the returned artifact ID via the [official SignPath GitHub Action v3](https://docs.signpath.io/trusted-build-systems/github).
4. The responsible human approver decides whether to sign. If signing is rejected, fails, is untrusted or invalid, the workflow **fails closed** and cannot upload a falsely labeled signed artifact.
5. After signing it checks Windows Authenticode status `Valid`, checks the signer contains `SignPath Foundation`, runs a smoke test on the **signed** EXE, computes its **post-signing SHA-256**, and uploads a GitHub Actions artifact named `signpath-verified-onefile`.
6. Download the signed artifact and check again locally:

```powershell
Get-AuthenticodeSignature '.\BlockCraftLauncher-v0.9.35-WPF-OneFile-Windows-x64.exe' | Format-List
Get-FileHash '.\BlockCraftLauncher-v0.9.35-WPF-OneFile-Windows-x64.exe' -Algorithm SHA256
```

## Important release integration work still needed

The current [controlled release workflow](../../.github/workflows/wpf-net48-controlled-release.yml) is **unchanged**; it publishes unsigned EXE and ZIP builds. After the proof-of-signing test passes:

- Integrate signing **after all build/selftests and before hashes and GitHub Release publication**.
- Replace the unsigned OneFile EXE asset with the actual signed EXE and regenerate `RELEASE-SHA256SUMS.txt` and `BlockCraftLauncher-....exe.sha256` **from the signed bytes**.
- ZIP bundles currently contain a separate WPF launcher EXE, updater EXE and third-party DLLs. Either obtain explicit SignPath artifact configurations for permitted owned EXEs, sign them **before ZIP creation**, or transparently label ZIP-contained EXEs as unsigned. Do **not** use the same project certificate to sign third-party DLLs.
- Keep the updater's SHA-256-based validation compatible with the new signed asset bytes, and test Windows upgrade/rollback against the signed release.
- Reject all failed or unapproved release-signing requests; never silently downgrade to publishing unsigned files as if they were signed.
- Update the README and release notes to include the code signing policy and correct publisher details **only when signing is actually working**.

Because the Foundation approval and configuration values have not been provided, this document and the workflow are **staged integration only**, not proof of a valid certificate or a working signed release.
