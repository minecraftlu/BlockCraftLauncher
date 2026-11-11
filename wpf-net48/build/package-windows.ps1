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

# Prevent x86 WebView2Loader from being placed beside an x64 WPF process.
# The Microsoft.Web.WebView2 NuGet package supplies architecture-specific
# native loaders; the .NET Framework build may otherwise stage the x86 one.
function Get-PeMachine([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "File not found: $path" }
    $file = [System.IO.File]::OpenRead($path)
    $reader = [System.IO.BinaryReader]::new($file)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Not a Windows PE image: $path" }
        [void]$file.Seek(0x3C, [System.IO.SeekOrigin]::Begin)
        $offset = $reader.ReadInt32()
        [void]$file.Seek($offset, [System.IO.SeekOrigin]::Begin)
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $path" }
        return $reader.ReadUInt16()
    }
    finally { $reader.Dispose() }
}
if ((Get-PeMachine $exe) -ne 0x8664) {
    throw 'WPF EXE must be x64 (PE machine AMD64), refusing architecture-mismatched package'
}
$loader = Join-Path $folder 'WebView2Loader.dll'
if (-not (Test-Path -LiteralPath $loader) -or (Get-PeMachine $loader) -ne 0x8664) {
    # Copy the actual x64 native library from the restored NuGet dependency.
    $assetsFile = Join-Path $root 'src\BlockCraftLauncher.Wpf\obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsFile)) { throw 'NuGet assets file missing; cannot resolve x64 WebView2Loader' }
    $assets = Get-Content -LiteralPath $assetsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $webviewRef = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Microsoft.Web.WebView2/*' } | Select-Object -First 1)
    if ($webviewRef.Count -eq 0) { throw 'Microsoft.Web.WebView2 package not found in project.assets.json' }
    $relative = $webviewRef[0].ToLowerInvariant() -replace '/', '\'
    $candidate = $null
    foreach ($rootFolder in $assets.packageFolders.PSObject.Properties.Name) {
        $path = Join-Path $rootFolder ($relative + '\runtimes\win-x64\native\WebView2Loader.dll')
        if (Test-Path -LiteralPath $path) { $candidate = $path; break }
    }
    if (-not $candidate) { throw '64-bit WebView2Loader.dll not present in NuGet restore' }
    Copy-Item -LiteralPath $candidate -Destination $loader -Force
}
if ((Get-PeMachine $loader) -ne 0x8664) {
    throw 'WebView2Loader.dll is not x64; refusing to package an incompatible Microsoft login dependency'
}
Write-Host 'Verified x64 WPF EXE and x64 WebView2Loader.dll'

# Update helper must run from a separate process after the launcher exits.
# WPF solution builds it next to its own csproj, then staging includes it for
# both ZIP and the onefile embedded payload.
$updater = Join-Path $root 'updater\bin\Release\net48\BlockCraftLauncher.Updater.exe'
if (-not (Test-Path -LiteralPath $updater -PathType Leaf) -or
    (Get-PeMachine $updater) -ne 0x8664) {
    throw 'Standalone x64 updater did not build; refusing to publish a broken update package'
}
Copy-Item -LiteralPath $updater -Destination (Join-Path $folder 'BlockCraftLauncher.Updater.exe') -Force
Write-Host 'Verified standalone x64 update helper'

$dist = Join-Path $root 'dist'
New-Item -Path $dist -ItemType Directory -Force | Out-Null
$name = 'BlockCraftLauncher-v0.9.43-WPF-net48-Alpha-Windows-x64'
$staging = Join-Path $dist $name
if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -Path $staging -ItemType Directory | Out-Null
# Copy ALL managed and native dependencies; the EXE is not self-contained.
Get-ChildItem -LiteralPath $folder -Force | Where-Object { $_.Name -ne 'obj' } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
}
# Verify the actual shipped files, not merely the successful MSBuild output.
$required = @(
  'BlockCraftLauncher.Wpf.exe',
  'BlockCraftLauncher.Updater.exe',
  'CmlLib.Core.Commons.dll',
  'CmlLib.Core.dll',
  'CmlLib.Core.Auth.Microsoft.dll',
  'CmlLib.Core.Installer.Forge.dll',
  'System.Text.Json.dll',
  'System.Buffers.dll',
  'System.Memory.dll',
  'Microsoft.Web.WebView2.Core.dll',
  'WebView2Loader.dll'
)
foreach ($item in $required) {
    $path = Join-Path $staging $item
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Release package missing a required DLL or EXE: $item"
    }
}
$commonsName = [System.Reflection.AssemblyName]::GetAssemblyName(
    (Join-Path $staging 'CmlLib.Core.Commons.dll'))
if ($commonsName.Name -ne 'CmlLib.Core.Commons' -or
    $commonsName.Version.ToString() -ne '4.0.0.0') {
    throw "Wrong CmlLib.Core.Commons assembly identity: $commonsName"
}
# Preserve a complete hash manifest so users can detect mixed old/new DLLs.
$hashList = Get-ChildItem -LiteralPath $staging -File |
    Where-Object { $_.Extension -eq '.dll' -or $_.Extension -eq '.exe' } |
    Sort-Object Name | ForEach-Object {
        $digest = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $($_.Name)"
    }
$hashList | Set-Content -LiteralPath (Join-Path $staging 'SHA256SUMS.txt') -Encoding ASCII
Write-Host "Verified bundled CmlLib.Core.Commons 4.0.0.0 and all required DLLs"

$stagedExe = Join-Path $staging 'BlockCraftLauncher.Wpf.exe'
$hash = (Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  BlockCraftLauncher.Wpf.exe" | Set-Content -LiteralPath "$stagedExe.sha256" -Encoding ASCII
@'
BlockCraft Launcher v0.9.43 RC2 测试版

运行环境为 Windows 10/11 x64 和 .NET Framework 4.8。Microsoft 登录需要 WebView2 Runtime。
请完整解压 ZIP，在解压目录中打开 BlockCraftLauncher.Wpf.exe，并保留同目录的 DLL。
在「设置 → 更新设置」中选择「预览版」，保存后进入「启动器更新」检查更新。
从旧版 Python 启动器迁移时，请将 WPF 启动器放在独立目录中，并选择已有游戏实例。
SHA256SUMS.txt 包含包内 EXE 和 DLL 的校验值。
'@ | Set-Content -LiteralPath (Join-Path $staging 'README.txt') -Encoding UTF8
$archive = Join-Path $dist "$name.zip"
if (Test-Path $archive) { Remove-Item -LiteralPath $archive -Force }
# ZIP entry names use forward slashes, including on Windows PowerShell.
# Existing WPF updaters require portable paths when verifying/extracting releases.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$prefix = $staging.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $_.FullName, "$name/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $zip.Dispose() }
if (-not (Test-Path $archive)) { throw 'ZIP creation failed' }
Write-Host "Built EXE: $exe"
Write-Host "SHA-256: $hash"
Write-Host "Release ZIP: $archive"
