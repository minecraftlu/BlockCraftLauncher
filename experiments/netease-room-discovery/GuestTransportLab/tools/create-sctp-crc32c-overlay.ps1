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

# Go's download cache marks module files read-only; copied attributes must
# be normalized before writing into this disposable source fork.
[System.IO.File]::SetAttributes($patchedFile, [System.IO.FileAttributes]::Normal)
$patched = [System.IO.File]::ReadAllText($patchedFile)
foreach ($change in $changes) {
    $patched = $patched.Replace($change.Old, $change.New)
}
[System.IO.File]::WriteAllText($patchedFile, $patched,
    [System.Text.UTF8Encoding]::new($false))

# Stage11.1 adds read-only channel state diagnostics to the TEMPORARY fork,
# not to the pinned upstream repository or any installed Minecraft client.
# It does not send application payloads or log any inbound bytes.
$observerPath = [System.IO.Path]::Combine($copyDir, "core", "nethernet",
    "blockcraft_channel_observer.go")
if (Test-Path -LiteralPath $observerPath) {
    throw "Unexpected read-only channel observer already exists in locked module."
}
$observerCode = @'
package nethernet

import "github.com/Happy2018new/nemc-tan-lobby-solver/core/webrtc"

// BlockCraftChannelStates returns only whether each existing local channel
// reports OPEN. It exposes no candidate addresses or SDP credentials.
func (c *Conn) BlockCraftChannelStates() (reliableOpen, unreliableOpen bool) {
    if c == nil || c.reliable == nil || c.unreliable == nil {
        return false, false
    }
    return c.reliable.ReadyState() == webrtc.DataChannelStateOpen,
        c.unreliable.ReadyState() == webrtc.DataChannelStateOpen
}

// BlockCraftObserveUnreliableLengths installs one read-only callback on the
// channel unused by Conn.ReadPacket. Never retain, log, copy or forward bytes.
// Only called by a single isolated, explicitly approved guest probe.
func (c *Conn) BlockCraftObserveUnreliableLengths(onLength func(int)) {
    if c == nil || c.unreliable == nil || onLength == nil {
        return
    }
    c.unreliable.OnMessage(func(m webrtc.DataChannelMessage) {
        onLength(len(m.Data))
    })
}
'@
[System.IO.File]::WriteAllText($observerPath, $observerCode,
    [System.Text.UTF8Encoding]::new($false))

# Source project go.mod is changed only within GitHub Actions' disposable
# checkout. Relative path resolves from GuestTransportLab/go.mod directory.
go mod edit -replace="github.com/Happy2018new/nemc-tan-lobby-solver=./.blockcraft/nethernet-fork"
if ($LASTEXITCODE -ne 0) { throw "Could not select locally patched SCTP module." }

Write-Host "NETHERNET_SCTP_CRC32C_LOCAL_FORK_READY"
Write-Host "Patched RFC4960 polynomial Castagnoli and SCTP little-endian checksum."
Write-Host "No changes to GOMODCACHE, GitHub source, or account credentials."
