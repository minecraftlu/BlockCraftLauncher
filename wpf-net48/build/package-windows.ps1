$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$folder = Join-Path $root 'src\BlockCraftLauncher.Wpf\bin\Release\net48'
$exe = Join-Path $folder 'BlockCraftLauncher.Wpf.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "WPF EXE not found: $exe" }
if ((Get-Item -LiteralPath $exe).Length -lt 30000) { throw 'WPF EXE is unexpectedly small: build may be incomplete' }
$header = [byte[]]::new(2)
$stream = [System.IO.File]::OpenRead($exe)
try { [void]$stream.Read($header, 0, 2) } finally { $stream.Dispose() }
if ($header[0] -ne 0x4d -or $header[1] -ne 0x5a) { throw 'EXE lacks the Windows MZ header' }

$dist = Join-Path $root 'dist'
New-Item -Path $dist -ItemType Directory -Force | Out-Null
$name = 'BlockCraftLauncher-v0.9.27-WPF-net48-Alpha-Windows-x64'
$staging = Join-Path $dist $name
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -Path $staging -ItemType Directory | Out-Null
# Copy ALL managed and native dependencies; the EXE is not self-contained.
Get-ChildItem -LiteralPath $folder -Force | Where-Object { $_.Name -ne 'obj' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
}
$stagedExe = Join-Path $staging 'BlockCraftLauncher.Wpf.exe'
$hash = (Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  BlockCraftLauncher.Wpf.exe" | Set-Content -LiteralPath "$stagedExe.sha256" -Encoding ASCII
@'
BlockCraft Launcher v0.9.27 WPF Alpha - .NET Framework 4.8
This is an incomplete migration preview. Do not overwrite the stable v0.9.26 Python/Go launcher.
Requires Windows 10/11, .NET Framework 4.8 and Microsoft Edge WebView2 Runtime for Microsoft login.
Some functionality (CurseForge, modpack installation, multiplayer, updater) is not yet migrated.
Start BlockCraftLauncher.Wpf.exe in this folder and keep all DLLs and runtime files next to it.
No .NET Framework 4.8 redistributable, Minecraft assets, or login tokens are bundled.
'@ | Set-Content -LiteralPath (Join-Path $staging 'README-ALPHA.txt') -Encoding UTF8
$archive = Join-Path $dist "$name.zip"
if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path $staging -DestinationPath $archive -CompressionLevel Optimal
if (-not (Test-Path $archive)) { throw 'ZIP creation failed' }
Write-Host "Built EXE: $exe"
Write-Host "SHA-256: $hash"
Write-Host "Release ZIP: $archive"
