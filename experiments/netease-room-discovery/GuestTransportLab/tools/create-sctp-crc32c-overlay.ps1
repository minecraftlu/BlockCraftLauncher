param(
    [string]$OutPath = ".blockcraft/sctp-crc32c-overlay.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Apply one surgically scoped, reproducible Go compiler overlay to the
# pinned MIT licensed NetherNet fork. Do NOT modify the Go module cache
# or vendor third-party credentials.
$moduleJSON = (& go list -m -json github.com/Happy2018new/nemc-tan-lobby-solver | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Could not resolve pinned NetherNet fork." }
$module = $moduleJSON | ConvertFrom-Json
$root = $module.Dir
if ($module.Replace -and $module.Replace.Dir) { $root = $module.Replace.Dir }
if (-not $root) { throw "NetherNet fork source path was not found." }

$originalPath = [System.IO.Path]::GetFullPath(
    (Join-Path $root "core/sctp/packet.go"))
if (-not (Test-Path -LiteralPath $originalPath)) {
    throw "Pinned SCTP packet.go is not present."
}

$source = [System.IO.File]::ReadAllText($originalPath)
$changes = @(
    @{ Old = 'crc32.MakeTable(crc32.IEEE)'; New = 'crc32.MakeTable(crc32.Castagnoli)'; Count = 1 },
    @{ Old = 'ieeeTable'; New = 'castagnoliTable'; Count = 4 },
    @{ Old = 'binary.BigEndian.Uint32(raw[8:])'; New = 'binary.LittleEndian.Uint32(raw[8:])'; Count = 1 },
    @{ Old = 'binary.BigEndian.PutUint32(raw[8:], generatePacketChecksum(raw))'; New = 'binary.LittleEndian.PutUint32(raw[8:], generatePacketChecksum(raw))'; Count = 1 }
)

foreach ($change in $changes) {
    $matches = [regex]::Matches(
        $source, [regex]::Escape($change.Old)).Count
    if ($matches -ne $change.Count) {
        throw "Unexpected pinned fork packet.go layout: CRC32c patch cannot be applied safely."
    }
    $source = $source.Replace($change.Old, $change.New)
}

# Build-only overlay, no GitHub source or credentials are transmitted.
$fullOutput = [System.IO.Path]::GetFullPath($OutPath)
$outDir = [System.IO.Path]::GetDirectoryName($fullOutput)
[System.IO.Directory]::CreateDirectory($outDir) | Out-Null
$patchedPath = [System.IO.Path]::Combine($outDir, "nethernet-sctp-packet-crc32c.go")
[System.IO.File]::WriteAllText($patchedPath, $source,
    [System.Text.UTF8Encoding]::new($false))

$replacement = @{}
$replacement[$originalPath] = $patchedPath
$overlay = @{ Replace = $replacement } | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($fullOutput, $overlay,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "NET_HANDED_SCTP_CRC32C_PATCH_READY"
Write-Host "Uses CRC32c Castagnoli with SCTP little-endian checksum field."
Write-Host "No module-cache or account credential changes."
