# Requires a disposable GitHub-hosted Windows runner. The test uses the real
# Desktop known folder, and refuses to touch any existing launcher there.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Desktop update checks must run on an ephemeral GitHub-hosted Windows runner.'
}
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$payloads = @(Get-ChildItem -LiteralPath (Join-Path $project 'dist') -Filter '*-WPF-OneFile-Windows-x64.exe' -File)
if ($payloads.Count -ne 1) { throw 'Expected exactly one freshly built standalone launcher.' }
$new = $payloads[0]
$newHash = (Get-FileHash -LiteralPath $new.FullName).Hash
$newTag = [IO.File]::ReadAllText((Join-Path $project 'src/BlockCraftLauncher.Wpf/Services/LauncherAutoUpdateService.cs'))
$newTag = [regex]::Match($newTag, 'internal const string CurrentTag = "(v[^"]+)";').Groups[1].Value
if (-not $newTag) { throw 'Cannot determine the expected new window version.' }
$helperPath = (Resolve-Path -LiteralPath (Join-Path $project 'updater/bin/Release/net48/BlockCraftLauncher.Updater.exe')).Path
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory, [Environment+SpecialFolderOption]::Create)
if (-not $desktop -or -not (Test-Path -LiteralPath $desktop -PathType Container)) { throw 'Desktop unavailable.' }
$canonical = Join-Path $desktop 'BlockCraftLauncher.exe'
function Get-DesktopLaunchers {
    @(Get-ChildItem -LiteralPath $desktop -Force -File -Filter 'BlockCraft*.exe')
}
if (@(Get-DesktopLaunchers).Count) { throw 'Existing BlockCraft desktop executable; refusing to run.' }
$root = Join-Path $env:RUNNER_TEMP ('BlockCraft desktop ' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
$old = Join-Path $root 'fixture.exe'
$oldHash = 'b8ea400657131aba4b5df6300ec06b38a7abdcf4d0360c104822f5009958159e'
Invoke-WebRequest -Uri 'https://github.com/minecraftlu/BlockCraftLauncher/releases/download/v0.9.51-wpf-rc.1/BlockCraftLauncher-v0.9.51-WPF-OneFile-Windows-x64.exe' -OutFile $old -TimeoutSec 90
if ((Get-FileHash -LiteralPath $old).Hash -ne $oldHash -or $newHash -eq $oldHash) {
    throw 'Old public fixture or new build identity mismatch.'
}
$signals = Join-Path $env:LOCALAPPDATA 'BlockCraftLauncher/updates'

function Start-Child([string]$File, [string[]]$Options, [string]$Bootstrap) {
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = Split-Path -LiteralPath $File
    if ($Bootstrap) { $info.Environment['BLOCKCRAFT_SINGLEFILE_BOOTSTRAP'] = $Bootstrap }
    foreach ($option in $Options) { $info.ArgumentList.Add($option) }
    return [Diagnostics.Process]::Start($info)
}
function Wait-For([scriptblock]$Predicate, [string]$Failure, [int]$Seconds = 80) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Predicate) { return }
        Start-Sleep -Milliseconds 100
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}
function Read-Signal([string]$File) {
    try { [IO.File]::ReadAllLines($File) }
    catch [IO.IOException] { }
}
function Visible-Wpf([int]$ProcessId) {
    if ($ProcessId -le 0) { return $null }
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq 'BlockCraftLauncher.Wpf') {
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $p }
    }
}
function Stop-Child($Child) {
    if ($Child) {
        if (-not $Child.HasExited) { $Child.Kill(); $Child.WaitForExit() }
        $Child.Dispose()
    }
}
function New-Stage([string]$Updates) {
    $staging = Join-Path $Updates ('staged-' + [guid]::NewGuid().ToString('N'))
    $null = [IO.Directory]::CreateDirectory($staging)
    [IO.File]::WriteAllText((Join-Path $staging '.blockcraft-stage'), 'BlockCraftLauncher update staging v1')
    $staged = Join-Path $staging $new.Name
    [IO.File]::Copy($new.FullName, $staged)
    return $staged
}

foreach ($case in @('downloads', 'collision', 'already-desktop', 'candidate-exits')) {
    if (@(Get-DesktopLaunchers).Count) { throw "Unowned desktop EXE before case $case" }
    $caseRoot = Join-Path $root $case
    $null = [IO.Directory]::CreateDirectory($caseRoot)
    $entry = if ($case -eq 'already-desktop') { $canonical } else { Join-Path $caseRoot 'BlockCraftLauncher previous.exe' }
    [IO.File]::Copy($old, $entry, $false)
    $untouched = Join-Path $caseRoot 'another launcher copy.exe'
    [IO.File]::Copy($old, $untouched, $false)
    $sentinelHash = $null
    if ($case -eq 'collision') {
        [IO.File]::WriteAllText($canonical, 'Unrelated executable fixture ' + [guid]::NewGuid().ToString('N'))
        $sentinelHash = (Get-FileHash -LiteralPath $canonical).Hash
    }
    $updates = Join-Path $caseRoot 'downloads/updates'
    $staged = New-Stage $updates
    $oldToken = [guid]::NewGuid().ToString('N')
    $token = [guid]::NewGuid().ToString('N')
    $badToken = [guid]::NewGuid().ToString('N')
    $oldMarker = Join-Path $signals "relaunch-$oldToken.ok"
    $marker = Join-Path $signals "relaunch-$token.ok"
    $result = Join-Path $signals "relaunch-$token.result"
    $badResult = Join-Path $signals "relaunch-$badToken.result"
    $oldWrapper = $oldWindow = $newWindow = $installer = $rejected = $null
    try {
        $oldWrapper = Start-Child $entry @('--after-update', $oldToken)
        Wait-For {
            $lines = @(Read-Signal $oldMarker)
            $lines.Count -ge 2 -and (Visible-Wpf ([int]$lines[0]))
        } "Old WPF window did not appear in $case"
        $oldWindow = Visible-Wpf ([int](@(Read-Signal $oldMarker)[0]))
        $options = @('--apply', '--mode', 'onefile', '--pid', $oldWindow.Id.ToString(),
            '--target', $entry, '--updates-root', $updates, '--desktop-install', '1')
        if ($case -eq 'downloads') {
            $badStaged = New-Stage $updates
            $rejected = Start-Child $helperPath ($options + @('--staged', $badStaged, '--sha256', ('0' * 64), '--handoff', $badToken)) $entry
            if (-not $rejected.WaitForExit(15000) -or $rejected.ExitCode -eq 0 -or
                (@(Read-Signal $badResult) -join ' ') -notmatch '^rejected ' -or
                -not (Visible-Wpf $oldWindow.Id) -or (Get-FileHash -LiteralPath $entry).Hash -ne $oldHash -or
                @(Get-DesktopLaunchers).Count) { throw 'Invalid package changed the original launcher or desktop.' }
        }
        $installer = Start-Child $helperPath ($options + @('--staged', $staged, '--sha256', $newHash, '--handoff', $token)) $entry
        Wait-For {
            $lines = @(Read-Signal $result)
            if ($lines.Count -gt 0 -and $lines[0] -in @('rejected','failed')) { throw ($lines -join ' ') }
            $lines.Count -eq 2 -and $lines[0] -eq 'ready' -and (Visible-Wpf ([int]$lines[1]))
        } "New WPF window did not open alongside the old window in $case"
        $newWindow = Visible-Wpf ([int](@(Read-Signal $result)[1]))
        $ack = @(Read-Signal $marker)
        if ($newWindow.Id -eq $oldWindow.Id -or $ack.Count -ne 3 -or $ack[2] -ne 'handoff-v1' -or
            [int]$ack[0] -ne $newWindow.Id -or -not (Visible-Wpf $oldWindow.Id) -or
            (Get-FileHash -LiteralPath $entry).Hash -ne $oldHash) {
            throw 'The old file or visible launcher changed before handoff approval.'
        }

        if ($case -eq 'candidate-exits') {
            $newWindow.Kill()
            $newWindow.WaitForExit()
            if (-not $installer.WaitForExit(15000) -or $installer.ExitCode -eq 0 -or
                (@(Read-Signal $result) -join ' ') -notmatch '^rejected ' -or
                -not (Visible-Wpf $oldWindow.Id) -or (Get-FileHash -LiteralPath $entry).Hash -ne $oldHash -or
                @(Get-DesktopLaunchers).Count) { throw 'Failed handoff did not restore the original state.' }
        } else {
            if (-not $oldWindow.CloseMainWindow() -or -not $oldWindow.WaitForExit(20000)) {
                throw "Old WPF did not close after visible handoff in $case"
            }
            Wait-For {
                $newWindow.Refresh()
                if ($newWindow.HasExited) { throw 'New main window exited during file installation.' }
                $newWindow.MainWindowTitle.Contains($newTag)
            } "New window did not acknowledge its installed launcher path in $case"
            if (-not $installer.WaitForExit(20000) -or $installer.ExitCode -ne 0) { throw "Update failed in $case" }
            $entries = @(Get-DesktopLaunchers)
            $installed = @($entries | Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -eq $newHash })
            $expectedCount = if ($sentinelHash) { 2 } else { 1 }
            if ($installed.Count -ne 1 -or $entries.Count -ne $expectedCount) {
                throw "New desktop executable missing, duplicated, or incorrect in $case"
            }
            if ($sentinelHash) {
                if ($installed[0].FullName -eq $canonical -or (Get-FileHash -LiteralPath $canonical).Hash -ne $sentinelHash) {
                    throw 'Unrelated desktop file was overwritten.'
                }
            } elseif ($installed[0].FullName -ne $canonical) { throw "Unexpected desktop destination in $case" }
            if ($entry -ne $installed[0].FullName -and (Test-Path -LiteralPath $entry)) {
                throw 'Verified update left the exact original EXE behind.'
            }
            if (Test-Path -LiteralPath (Split-Path -LiteralPath $staged)) { throw 'Successful update left its marked staging directory behind.' }
            if ($case -eq 'downloads') {
                if (-not $newWindow.CloseMainWindow() -or -not $newWindow.WaitForExit(20000)) {
                    throw 'Installed window would not close normally.'
                }
                # No old EXE or staged payload remains. Launch the actual desktop
                # entry again and require a fresh ContentRendered acknowledgement.
                $freshToken = [guid]::NewGuid().ToString('N')
                $freshMarker = Join-Path $signals "relaunch-$freshToken.ok"
                $fresh = Start-Child $installed[0].FullName @('--after-update', $freshToken, '--window-relaunch-selftest')
                try {
                    if (-not $fresh.WaitForExit(70000) -or $fresh.ExitCode -ne 0 -or
                        @(Read-Signal $freshMarker).Count -lt 2) { throw 'Desktop EXE failed to reopen independently.' }
                } finally {
                    Stop-Child $fresh
                    if (Test-Path -LiteralPath $freshMarker) { Remove-Item -LiteralPath $freshMarker }
                }
            }
        }
        if ((Get-FileHash -LiteralPath $untouched).Hash -ne $oldHash) { throw 'An unrelated copy of the launcher was modified.' }
        Write-Host "PASS: desktop update $case"
    } finally {
        foreach ($child in @($newWindow, $oldWindow, $oldWrapper, $installer, $rejected)) { Stop-Child $child }
        foreach ($signal in @($marker, $result, $oldMarker, $badResult)) {
            if (Test-Path -LiteralPath $signal) { Remove-Item -LiteralPath $signal }
        }
        # Only remove known fixture bytes in this exclusively owned namespace.
        $fixtureHashes = @($newHash, $oldHash, $sentinelHash)
        foreach ($file in @(Get-DesktopLaunchers)) {
            if ((Get-FileHash -LiteralPath $file.FullName).Hash -in $fixtureHashes) {
                Remove-Item -LiteralPath $file.FullName
            }
        }
    }
}

