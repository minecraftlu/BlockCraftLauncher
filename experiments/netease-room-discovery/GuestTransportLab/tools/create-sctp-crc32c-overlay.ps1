param(
    [string]$OutPath = ".blockcraft/nethernet-fork"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Go prohibits build overlays for paths within GOMODCACHE. Instead, copy
# only the pinned MIT-licensed fork into a temporary workspace and install a
# go.mod local replace, which affects THIS build only (never commit the
# modified go.mod). No credentials, network scans, or server changes.
$moduleJSON = (& go list -m -json github.com/Happy2018new/nemc-tan-lobby-solver | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Cannot resolve pinned NetherNet module." }
$module = $moduleJSON | ConvertFrom-Json
$root = $module.Dir
if ($module.Replace -and $module.Replace.Dir) { $root = $module.Replace.Dir }
if (-not $root) { throw "Pinned NetherNet module cache path not found." }

$source = [System.IO.Path]::GetFullPath((Join-Path $root "core/sctp/packet.go"))
if (-not (Test-Path -LiteralPath $source)) { throw "Pinned SCTP packet source not found." }

# Verify exactly the known, audited source layout before modifying anything.
$original = [System.IO.File]::ReadAllText($source)
$changes = @(
    @{ Old = 'crc32.MakeTable(crc32.IEEE)'; New = 'crc32.MakeTable(crc32.Castagnoli)'; Count = 1 },
    @{ Old = 'ieeeTable'; New = 'castagnoliTable'; Count = 4 },
    @{ Old = 'binary.BigEndian.Uint32(raw[8:])'; New = 'binary.LittleEndian.Uint32(raw[8:])'; Count = 1 },
    @{ Old = 'binary.BigEndian.PutUint32(raw[8:], generatePacketChecksum(raw))'; New = 'binary.LittleEndian.PutUint32(raw[8:], generatePacketChecksum(raw))'; Count = 1 }
)
foreach ($change in $changes) {
    if ([regex]::Matches($original, [regex]::Escape($change.Old)).Count -ne $change.Count) {
        throw "Unexpected pinned SCTP dependency code; safe CRC32c patch aborted."
    }
}

$copyDir = [System.IO.Path]::GetFullPath($OutPath)
if (Test-Path -LiteralPath $copyDir) {
    throw "The temporary fork directory already exists; refusing to overwrite."
}
$parent = [System.IO.Path]::GetDirectoryName($copyDir)
[System.IO.Directory]::CreateDirectory($parent) | Out-Null
Copy-Item -LiteralPath $root -Destination $copyDir -Recurse -Force
$patchedFile = [System.IO.Path]::Combine($copyDir, "core", "sctp", "packet.go")
if (-not (Test-Path -LiteralPath $patchedFile)) {
    throw "Local pinned fork copy was incomplete."
}

$patched = [System.IO.File]::ReadAllText($patchedFile)
foreach ($change in $changes) {
    $patched = $patched.Replace($change.Old, $change.New)
}
[System.IO.File]::WriteAllText($patchedFile, $patched,
    [System.Text.UTF8Encoding]::new($false))

# Source project go.mod is changed only within GitHub Actions' disposable
# checkout. Relative path resolves from GuestTransportLab/go.mod directory.
go mod edit -replace="github.com/Happy2018new/nemc-tan-lobby-solver=./.blockcraft/nethernet-fork"
if ($LASTEXITCODE -ne 0) { throw "Could not select locally patched SCTP module." }

Write-Host "NETHERNET_SCTP_CRC32C_LOCAL_FORK_READY"
Write-Host "Patched RFC4960 polynomial Castagnoli and SCTP little-endian checksum."
Write-Host "No changes to GOMODCACHE, GitHub source, or account credentials."
