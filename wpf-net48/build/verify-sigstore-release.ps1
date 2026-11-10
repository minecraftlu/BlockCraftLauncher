# Verifies the exact GitHub Release EXE/ZIP with SHA-256, Sigstore Cosign and
# GitHub's SLSA build-provenance attestation. All checks are mandatory.
# Requires cosign v3+, GitHub CLI with "gh attestation", and network access.
# Usage: ./build/verify-sigstore-release.ps1 -ReleaseTag v0.9.36 -DistPath ./dist
param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$ReleaseTag,
    [string]$DistPath = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'
$repo = 'minecraftlu/BlockCraftLauncher'
$signer = 'github.com/minecraftlu/BlockCraftLauncher/.github/workflows/wpf-net48-controlled-release.yml'
$identity = 'https://github.com/minecraftlu/BlockCraftLauncher/.github/workflows/wpf-net48-controlled-release.yml@refs/heads/main'
$oidcIssuer = 'https://token.actions.githubusercontent.com'
$dist = [System.IO.Path]::GetFullPath($DistPath)

$exe = "BlockCraftLauncher-$ReleaseTag-WPF-OneFile-Windows-x64.exe"
$zip = "BlockCraftLauncher-$ReleaseTag-WPF-net48-Windows-x64.zip"
$expected = @($exe, $zip)
$manifest = Join-Path $dist 'RELEASE-SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
    throw "Release checksum manifest is missing: $manifest"
}

$recorded = @{}
foreach ($line in @(Get-Content -LiteralPath $manifest)) {
    if ($line -cnotmatch '^([a-f0-9]{64})  ([^\/\\]+)$') {
        throw "Invalid manifest entry: $line"
    }
    $digest = $matches[1]
    $name = $matches[2]
    if ($expected -cnotcontains $name -or $recorded.ContainsKey($name)) {
        throw "Unexpected or duplicate release manifest entry: $name"
    }
    $recorded[$name] = $digest
}
if ($recorded.Count -ne $expected.Count) {
    throw 'Release manifest does not list the exact expected EXE and ZIP'
}

# Check the release manifest before using any third-party verification service.
foreach ($name in $expected) {
    $path = Join-Path $dist $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing asset: $path" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $recorded[$name]) { throw "SHA-256 mismatch: $name" }
    Write-Host "PASS SHA-256: $name"
}

if (-not (Get-Command cosign -ErrorAction SilentlyContinue)) {
    throw 'Cosign CLI is required: https://docs.sigstore.dev/cosign/system_config/installation/'
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI is required: https://cli.github.com/'
}
foreach ($name in $expected) {
    $path = Join-Path $dist $name
    $bundle = "$path.sigstore.json"
    if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
        throw "Sigstore bundle is missing: $bundle"
    }
    & cosign verify-blob $path --bundle $bundle --certificate-identity $identity --certificate-oidc-issuer $oidcIssuer
    if ($LASTEXITCODE -ne 0) { throw "Sigstore signature verification failed: $name" }
    Write-Host "PASS Sigstore/Cosign: $name"

    # The attestation must come from this repository's reviewed stable
    # release workflow on main, not merely from some arbitrary GitHub job.
    & gh attestation verify $path --repo $repo --signer-workflow $signer --source-ref 'refs/heads/main'
    if ($LASTEXITCODE -ne 0) { throw "GitHub build provenance verification failed: $name" }
    Write-Host "PASS GitHub build provenance: $name"
}
Write-Host "PASS: $ReleaseTag verified (hashes + Cosign + GitHub attestation)"
