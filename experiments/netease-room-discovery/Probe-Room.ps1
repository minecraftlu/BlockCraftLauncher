#requires -Version 5.1
<#
.SYNOPSIS
  One-shot, read-only research probe for a NetEase Minecraft China Edition room.
.DESCRIPTION
  Sends one POST to a community-documented room metadata endpoint.
  Does NOT log in, join a room, fetch game assets, request passwords,
  collect cookies, or launch a game client.
  This endpoint is not a documented public third-party API. The probe may be
  rejected or return incomplete results; such a result does not prove the
  room is absent or that a game connection is possible.
.EXAMPLE
  .\Probe-Room.ps1 -RoomId 453649
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d{4,6}$')]
    [string]$RoomId = '453649',

    # 0 is only a test value; it may not satisfy server-side identity requirements.
    [uint32]$Uid = 0
)

$ErrorActionPreference = 'Stop'
$endpoint = 'https://g79mcltransfer.minecraft.cn/room-with-name'
$payload = @{ name = $RoomId; uid = $Uid } | ConvertTo-Json -Compress

Write-Host ('[Room discovery] Room number: ' + $RoomId)
Write-Host '[Room discovery] Sending exactly one read-only metadata request...'

try {
    $response = Invoke-RestMethod -Uri $endpoint -Method Post `
        -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($payload)) `
        -TimeoutSec 15 -ErrorAction Stop
}
catch {
    $status = ''
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
        $status = [string][int]$_.Exception.Response.StatusCode
    }
    if ($status) {
        Write-Warning ("HTTP $status. This endpoint may require an authorized identity, or may have changed.")
    }
    else {
        Write-Warning 'Network request failed. Check HTTPS connectivity, DNS and server availability.'
    }
    Write-Host 'STATUS: INCONCLUSIVE (no successful metadata response)'
    exit 2
}

if ($null -eq $response) {
    Write-Host 'STATUS: INCONCLUSIVE (empty response)'
    exit 2
}

$code = $response.code
if ($null -ne $code -and [int]$code -ne 0) {
    Write-Warning ("Service returned code $code; do not interpret as a missing room.")
    Write-Host 'STATUS: INCONCLUSIVE (service response code)'
    exit 2
}

$rooms = @($response.list) | Where-Object { $null -ne $_ }
$room = $rooms | Where-Object {
    ([string]$_.rid -eq $RoomId) -or ([string]$_.name -eq $RoomId)
} | Select-Object -First 1

if ($null -eq $room) {
    Write-Host 'STATUS: INCONCLUSIVE (no matching room in the response)'
    Write-Host 'This could reflect room expiry, visibility, user identity, or API changes.'
    exit 2
}

Write-Host 'STATUS: ROOM_METADATA_FOUND'
Write-Host ('Room ID: ' + [string]$room.rid)
Write-Host ('Room name/number: ' + [string]$room.name)
Write-Host ('Players: ' + [string]$room.cnt + '/' + [string]$room.cap)
Write-Host ('Game version marker: ' + [string]$room.version)
Write-Host ('Platform marker: ' + [string]$room.platform)
Write-Host ('Server marker: ' + [string]$room.srv)
Write-Host 'Only public-looking metadata is displayed. No host UID, members, or credentials are printed.'
Write-Host 'NOTE: Metadata lookup success is NOT login, joining, or playing in the room.'
