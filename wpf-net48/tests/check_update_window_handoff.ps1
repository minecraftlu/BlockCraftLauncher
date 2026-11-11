$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path $env:RUNNER_TEMP ("BlockCraft handoff " + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$dist = (Resolve-Path 'dist').Path
$signals = Join-Path $env:LOCALAPPDATA 'BlockCraftLauncher/updates'
$updater = (Resolve-Path 'src/BlockCraftLauncher.Wpf/bin/Release/net48/BlockCraftLauncher.Updater.exe').Path
$main = 'BlockCraftLauncher.Wpf.exe'
$single = 'BlockCraftLauncher-v0.9.46-WPF-OneFile-Windows-x64.exe'
$archive = 'BlockCraftLauncher-v0.9.46-WPF-net48-Windows-x64.zip'

function Start-LauncherProcess([string]$Path, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path $Path
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    [Diagnostics.Process]::Start($start)
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$Failure) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw $Failure
}

function Get-VisibleLauncher([int]$Id) {
    $window = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($window -and $window.ProcessName -eq 'BlockCraftLauncher.Wpf') {
        $window.Refresh()
        if ($window.MainWindowHandle -ne [IntPtr]::Zero) { return $window }
    }
}

function Get-ContentIfPresent([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        try { Get-Content -LiteralPath $Path -ErrorAction Stop }
        catch [IO.IOException] { }
    }
}

function Stop-TestProcess($Process) {
    if ($Process) {
        if (-not $Process.HasExited) {
            $Process.Kill()
            $Process.WaitForExit()
        }
        $Process.Dispose()
    }
}

foreach ($mode in @('zip', 'onefile')) {
    $install = Join-Path $root "$mode install"
    $updates = Join-Path $install 'downloads/updates'
    $stageRoot = Join-Path $updates ('staged-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    'BlockCraftLauncher update staging v1' |
        Set-Content -LiteralPath (Join-Path $stageRoot '.blockcraft-stage') -Encoding ascii -NoNewline
    $keep = Join-Path $install 'user.keep'
    'preserve user files' | Set-Content -LiteralPath $keep

    if ($mode -eq 'zip') {
        Get-ChildItem -LiteralPath (Split-Path $env:BCL_ZIP_PROBE) | Copy-Item -Destination $install -Recurse
        $entry = Join-Path $install $main
        $target = $install
        $payload = Join-Path $dist $archive
        $unpacked = Join-Path $stageRoot 'unpacked'
        Expand-Archive -LiteralPath $payload -DestinationPath $unpacked
        $newMain = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File -Filter $main)
        if ($newMain.Count -ne 1) { throw 'Expected one staged ZIP launcher' }
        $staged = $newMain[0].DirectoryName
        $entryHash = $env:BCL_NEW_WPF_SHA
    } else {
        $entry = Join-Path $install 'BlockCraft Launcher.exe'
        $target = $entry
        $payload = Join-Path $dist $single
        $staged = Join-Path $stageRoot $single
        Copy-Item -LiteralPath $env:BCL_OLD_BOOTSTRAP -Destination $entry
        Copy-Item -LiteralPath $payload -Destination $staged
        $entryHash = (Get-FileHash -LiteralPath $payload).Hash
    }
    $digest = (Get-FileHash -LiteralPath $payload).Hash
    $oldHash = (Get-FileHash -LiteralPath $entry).Hash
    if ($oldHash -eq $entryHash) { throw 'Old and new test binaries must differ' }

    $oldToken = [guid]::NewGuid().ToString('N')
    $oldMarker = Join-Path $signals "relaunch-$oldToken.ok"
    $token = [guid]::NewGuid().ToString('N')
    $marker = Join-Path $signals "relaunch-$token.ok"
    $result = Join-Path $signals "relaunch-$token.result"
    $oldStart = $old = $helper = $candidate = $null
    try {
        $oldStart = Start-LauncherProcess $entry @('--after-update', $oldToken)
        Wait-Until {
            $lines = @(Get-ContentIfPresent $oldMarker)
            $lines.Count -ge 2 -and (Get-VisibleLauncher ([int]$lines[0]))
        } 70 "$mode old main window did not appear"
        $old = Get-VisibleLauncher ([int](Get-Content -LiteralPath $oldMarker)[0])

        $installerArgs = @('--apply', '--mode', $mode, '--pid', "$($old.Id)",
            '--target', $target, '--staged', $staged)
        if ($mode -eq 'onefile') {
            $badToken = [guid]::NewGuid().ToString('N')
            $badResult = Join-Path $signals "relaunch-$badToken.result"
            $invalid = Start-LauncherProcess $updater ($installerArgs + @('--sha256', ('0' * 64),
                '--updates-root', $updates, '--handoff', $badToken))
            try {
                if (-not $invalid.WaitForExit(12000) -or $invalid.ExitCode -eq 0) {
                    throw 'Invalid staged EXE was accepted'
                }
                if ((Get-Content -LiteralPath $badResult)[0] -ne 'rejected' -or
                    (Test-Path -LiteralPath (Join-Path $signals "relaunch-$badToken.ok")) -or
                    -not (Get-VisibleLauncher $old.Id) -or
                    (Get-FileHash -LiteralPath $entry).Hash -ne $oldHash) {
                    throw 'Rejected update did not leave the old launcher visible and intact'
                }
                Write-Host 'PASS: an invalid staged EXE left the old launcher visible and intact'
            } finally {
                Stop-TestProcess $invalid
                if (Test-Path -LiteralPath $badResult) { Remove-Item -LiteralPath $badResult }
            }
        }

        $helper = Start-LauncherProcess $updater ($installerArgs + @('--sha256', $digest,
            '--updates-root', $updates, '--handoff', $token))
        Wait-Until {
            $lines = @(Get-ContentIfPresent $result)
            if ($lines.Count -gt 0 -and $lines[0] -eq 'rejected') { throw ($lines -join ' ') }
            $lines.Count -eq 2 -and $lines[0] -eq 'ready' -and (Get-VisibleLauncher ([int]$lines[1]))
        } 80 "$mode new main window did not appear before the old window closed"
        $ready = @(Get-Content -LiteralPath $result)
        $candidate = Get-VisibleLauncher ([int]$ready[1])
        $ack = @(Get-Content -LiteralPath $marker)
        if ($ack.Count -ne 3 -or $ack[2] -ne 'handoff-v1' -or
            [int]$ack[0] -ne $candidate.Id -or $candidate.Id -eq $old.Id -or
            -not (Get-VisibleLauncher $old.Id) -or
            (Get-FileHash -LiteralPath $entry).Hash -ne $oldHash) {
            throw "$mode handoff did not confirm the rendered new window before touching the original files"
        }

        if (-not $old.CloseMainWindow() -or -not $old.WaitForExit(15000)) {
            throw "$mode old window did not close after readiness"
        }
        Wait-Until {
            $candidate.Refresh()
            if ($candidate.HasExited) { throw "$mode displayed new window unexpectedly exited" }
            $candidate.MainWindowTitle -like '*v0.9.46*'
        } 120 "$mode displayed window did not acknowledge successful installation"
        if ((Get-FileHash -LiteralPath $entry).Hash -ne $entryHash -or
            (Get-Content -LiteralPath $keep) -ne 'preserve user files') {
            throw "$mode did not update the original installation in place"
        }
        $visible = @(Get-Process -Name 'BlockCraftLauncher.Wpf' -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
        if ($visible.Count -ne 1 -or $visible[0].Id -ne $candidate.Id) {
            throw "$mode opened a second new window instead of keeping the displayed one"
        }
        if ($mode -eq 'onefile' -and @(Get-ChildItem -LiteralPath $install -Filter '*.blockcraft-previous-*.exe').Count) {
            throw 'OneFile update retained an obsolete EXE backup'
        }
        if (-not $candidate.CloseMainWindow() -or -not $candidate.WaitForExit(15000)) {
            throw "$mode new launcher did not close normally"
        }
        if (-not $helper.WaitForExit(15000) -or $helper.ExitCode -ne 0 -or
            (Test-Path -LiteralPath $stageRoot)) {
            throw "$mode updater did not clean its staged files"
        }
        Write-Host "PASS: $mode showed the new window first, replaced the original files, retained that window, and cleaned staging"
    } finally {
        Stop-TestProcess $candidate
        if ($old -and $old.Id -eq $oldStart.Id) { $oldStart.Dispose(); $oldStart = $null }
        Stop-TestProcess $old
        Stop-TestProcess $oldStart
        Stop-TestProcess $helper
        foreach ($path in @($oldMarker, $marker, $result)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        }
    }
}
