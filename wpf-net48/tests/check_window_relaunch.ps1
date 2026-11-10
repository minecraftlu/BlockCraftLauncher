# Exercise an actual WPF main window, not only the startup dependency probe.
# ContentRendered must emit the same readiness acknowledgement that the
# standalone updater awaits after the EXE/ZIP is installed.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$versionSource = Get-Content -LiteralPath (Join-Path $root 'src\BlockCraftLauncher.Wpf\Services\LauncherAutoUpdateService.cs') -Raw
$tag = [regex]::Match($versionSource, 'internal const string CurrentTag = "([^"]+)";').Groups[1].Value
if ($tag -notmatch '^v\d+\.\d+\.\d+(?:-wpf-rc\.\d+)?$') {
    throw "Invalid WPF release tag in updater: $tag"
}
$releaseVersion = ($tag -split '-', 2)[0]
$launchers = @(
    (Join-Path $root 'src\BlockCraftLauncher.Wpf\bin\Release\net48\BlockCraftLauncher.Wpf.exe'),
    (Join-Path $root "dist\BlockCraftLauncher-$releaseVersion-WPF-OneFile-Windows-x64.exe")
)
foreach ($exe in $launchers) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Missing WPF/OneFile relaunch target: $exe"
    }
    foreach ($mode in @('installed-update', 'direct-open')) {
        $nonce = [Guid]::NewGuid().ToString('N')
        $marker = Join-Path $env:LOCALAPPDATA ("BlockCraftLauncher\updates\relaunch-$nonce.ok")
        if (Test-Path -LiteralPath $marker) {
            throw 'Untrusted pre-existing relaunch marker; refusing to pass'
        }
        $process = $null
        try {
            $flags = @('--after-update', $nonce, '--window-relaunch-selftest')
            if ($mode -eq 'direct-open') { $flags += '--direct-open' }
            $process = Start-Process -FilePath $exe -ArgumentList $flags -PassThru
            if (-not $process.WaitForExit(60000)) {
                $process.Kill()
                throw "WPF relaunch-window probe timed out ($mode): $exe"
            }
            $process.Refresh()
            if ($process.ExitCode -ne 0) {
                throw "WPF relaunch-window probe exited $($process.ExitCode) ($mode): $exe"
            }
            if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                throw "The new main window was not rendered or acknowledged ($mode): $exe"
            }
            $lines = @(Get-Content -LiteralPath $marker)
            if ($lines.Count -lt 2 -or $lines[0] -notmatch '^[0-9]+$' -or
                $lines[1] -notmatch '^20[0-9]{2}-') {
                throw "Invalid rendered-window acknowledgement ($mode): $exe"
            }
            $childPid = [int]$lines[0]
            if ($exe -like '*OneFile*' -and $childPid -eq $process.Id) {
                throw 'OneFile wrapper did not acknowledge from the actual WPF child process'
            }
            if ($exe -notlike '*OneFile*' -and $childPid -ne $process.Id) {
                throw 'Wrong WPF PID wrote the window acknowledgement'
            }
            Write-Host "PASS: $mode displays real main window for $(Split-Path $exe -Leaf)"
        }
        finally {
            if (Test-Path -LiteralPath $marker) {
                Remove-Item -LiteralPath $marker -Force
            }
            if ($process -ne $null) {
                try { $process.Dispose() } catch { }
            }
        }
    }
}
) {
    throw "Invalid WPF release tag in updater: $tag"
}
$releaseVersion = ($tag -split '-', 2)[0]
$launchers = @(
    (Join-Path $root 'src\BlockCraftLauncher.Wpf\bin\Release\net48\BlockCraftLauncher.Wpf.exe'),
    (Join-Path $root "dist\BlockCraftLauncher-$releaseVersion-WPF-OneFile-Windows-x64.exe")
)
foreach ($exe in $launchers) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Missing WPF/OneFile relaunch target: $exe"
    }
    foreach ($mode in @('installed-update', 'direct-open')) {
        $nonce = [Guid]::NewGuid().ToString('N')
        $marker = Join-Path $env:LOCALAPPDATA ("BlockCraftLauncher\updates\relaunch-$nonce.ok")
        if (Test-Path -LiteralPath $marker) {
            throw 'Untrusted pre-existing relaunch marker; refusing to pass'
        }
        $process = $null
        try {
            $flags = @('--after-update', $nonce, '--window-relaunch-selftest')
            if ($mode -eq 'direct-open') { $flags += '--direct-open' }
            $process = Start-Process -FilePath $exe -ArgumentList $flags -PassThru
            if (-not $process.WaitForExit(60000)) {
                $process.Kill()
                throw "WPF relaunch-window probe timed out ($mode): $exe"
            }
            $process.Refresh()
            if ($process.ExitCode -ne 0) {
                throw "WPF relaunch-window probe exited $($process.ExitCode) ($mode): $exe"
            }
            if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                throw "The new main window was not rendered or acknowledged ($mode): $exe"
            }
            $lines = @(Get-Content -LiteralPath $marker)
            if ($lines.Count -lt 2 -or $lines[0] -notmatch '^[0-9]+$' -or
                $lines[1] -notmatch '^20[0-9]{2}-') {
                throw "Invalid rendered-window acknowledgement ($mode): $exe"
            }
            $childPid = [int]$lines[0]
            if ($exe -like '*OneFile*' -and $childPid -eq $process.Id) {
                throw 'OneFile wrapper did not acknowledge from the actual WPF child process'
            }
            if ($exe -notlike '*OneFile*' -and $childPid -ne $process.Id) {
                throw 'Wrong WPF PID wrote the window acknowledgement'
            }
            Write-Host "PASS: $mode displays real main window for $(Split-Path $exe -Leaf)"
        }
        finally {
            if (Test-Path -LiteralPath $marker) {
                Remove-Item -LiteralPath $marker -Force
            }
            if ($process -ne $null) {
                try { $process.Dispose() } catch { }
            }
        }
    }
}
