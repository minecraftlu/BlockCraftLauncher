# Run with PowerShell 7 after build/build-windows.bat on Windows.
param(
    [Parameter(Mandatory)] [string]$CaptureDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The UI gallery requires Windows WPF.' }
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$capture = [IO.Path]::GetFullPath($CaptureDirectory)
$wpf = (Resolve-Path -LiteralPath (Join-Path $project 'src/BlockCraftLauncher.Wpf/bin/Release/net48/BlockCraftLauncher.Wpf.exe')).Path
$onefile = @(Get-ChildItem -LiteralPath (Join-Path $project 'dist') -File -Filter 'BlockCraftLauncher-v*-WPF-OneFile-Windows-x64.exe')
if ($onefile.Count -ne 1) { throw 'Expected a single freshly built Windows OneFile EXE.' }
$pages = @('home', 'versions', 'version-settings', 'installed-mods', 'saves', 'game-download',
    'manual-install', 'mods', 'resource-packs', 'shaders', 'modpacks', 'skins', 'skin-library',
    'tasks', 'servers', 'server-search', 'lan', 'start-settings', 'download-settings', 'appearance',
    'update-settings', 'about', 'updates', 'logs', 'skin-editor', 'resource-detail')

function Test-RenderedImage([string]$File, [int]$Width = 0, [int]$Height = 0) {
    $bitmap = [Drawing.Bitmap]::new($File)
    try {
        if (($Width -and ($bitmap.Width -ne $Width -or $bitmap.Height -ne $Height)) -or
            $bitmap.Width -lt 140 -or $bitmap.Height -lt 64) {
            throw "Unexpected PNG dimensions for $File ($($bitmap.Size))."
        }
        # Sample only opaque pixels. Reject transparent, blank and flat
        # backgrounds from a window that never measured/rendered its content.
        $colors = [Collections.Generic.HashSet[int]]::new()
        $opaque = 0
        for ($y = 0; $y -lt $bitmap.Height; $y += 6) {
            for ($x = 0; $x -lt $bitmap.Width; $x += 6) {
                $pixel = $bitmap.GetPixel($x, $y)
                if ($pixel.A -lt 128) { continue }
                $opaque++
                $null = $colors.Add($pixel.ToArgb())
            }
        }
        if ($opaque -lt 80 -or $colors.Count -lt 12) { throw "Empty or flat rendered image: $File" }
    } finally { $bitmap.Dispose() }
}

Add-Type -AssemblyName System.Drawing
foreach ($build in @(@('wpf', $wpf), @('onefile', $onefile[0].FullName))) {
    $folder = Join-Path $capture $build[0]
    if ((Test-Path -LiteralPath $folder) -and @(Get-ChildItem -Force -LiteralPath $folder).Count) {
        throw "Use an empty capture folder to avoid stale evidence: $folder"
    }
    $null = [IO.Directory]::CreateDirectory($folder)
    $start = [Diagnostics.ProcessStartInfo]::new($build[1])
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($build[1])
    $start.UseShellExecute = $false
    $start.ArgumentList.Add('--ui-layout-selftest')
    $start.Environment['BLOCKCRAFT_UI_CAPTURE_DIR'] = $folder
    $start.Environment['BLOCKCRAFT_UI_DIALOG_PROBE'] = '1'
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(180000)) {
            $process.Kill($true)
            throw "Timed out rendering the $($build[0]) gallery."
        }
        if ($process.ExitCode -ne 0) {
            $log = Join-Path $env:LOCALAPPDATA 'BlockCraftLauncher/wpf-preview-logs/startup-error.log'
            if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding utf8 -Tail 16 | Write-Host }
            throw "$($build[0]) UI regression exited with code $($process.ExitCode)."
        }
    } finally { $process.Dispose() }
    foreach ($theme in @('light','dark')) {
        $digests = [Collections.Generic.HashSet[string]]::new()
        foreach ($page in $pages) {
            $path = Join-Path $folder "$theme-$page.png"
            if ($theme -eq 'light') { Test-RenderedImage $path 960 600 } else { Test-RenderedImage $path 850 560 }
            if (-not $digests.Add((Get-FileHash -LiteralPath $path).Hash)) {
                throw "$theme-$page produced another page's identical screenshot."
            }
        }
    }
    foreach ($image in @('startup-loading', 'page-loading', 'game-launch-loading', 'dialog-account', 'dialog-skin')) {
        Test-RenderedImage (Join-Path $folder "$image.png")
    }
    Write-Host "PASS $($build[0]): 25 navigable pages, resource details, both themes, startup, loading and two real dialogs."
}
