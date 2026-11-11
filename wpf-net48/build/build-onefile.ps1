$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'BlockCraftLauncher-v0.9.39-WPF-net48-Alpha-Windows-x64'
$payload = Join-Path $dist 'BlockCraftLauncher.OneFile.Payload.zip'
$project = Join-Path $root 'onefile\BlockCraftLauncher.SingleFile.csproj'
$name = 'BlockCraftLauncher-v0.9.39-WPF-OneFile-Windows-x64.exe'
if (!(Test-Path (Join-Path $stage 'SHA256SUMS.txt'))) { throw 'Staged WPF files missing' }
if (Test-Path $payload) { Remove-Item $payload -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage,$payload,[System.IO.Compression.CompressionLevel]::Optimal,$false)
$msbuild = Get-Command MSBuild.exe -ErrorAction SilentlyContinue
if ($msbuild) { $compiler = $msbuild.Source }
else {
  $vswhere = Join-Path (Get-Item Env:'ProgramFiles(x86)').Value 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (!(Test-Path $vswhere)) { throw 'Visual Studio MSBuild missing' }
  $compiler = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
}
if (!$compiler) { throw 'MSBuild.exe not found' }
& $compiler $project '-restore' '-t:Rebuild' '-p:Configuration=Release' '-p:Platform=AnyCPU' '-p:PlatformTarget=x64' '-v:minimal'
if ($LASTEXITCODE -ne 0) { throw 'Single-file EXE build failed' }
$built = Join-Path $root 'onefile\bin\Release\net48\BlockCraftLauncher.exe'
if (!(Test-Path $built)) { throw 'Single-file EXE not produced' }
$target = Join-Path $dist $name
Copy-Item $built $target -Force
$fs = [System.IO.File]::OpenRead($target)
$reader = [System.IO.BinaryReader]::new($fs)
try {
  if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Invalid MZ header' }
  [void]$fs.Seek(0x3c,[System.IO.SeekOrigin]::Begin)
  $offset = $reader.ReadInt32()
  [void]$fs.Seek($offset,[System.IO.SeekOrigin]::Begin)
  if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'Not an x64 PE file' }
} finally { $reader.Dispose() }
$hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name" | Set-Content ($target+'.sha256') -Encoding ASCII
Write-Host "OneFile: $target"
Write-Host "SHA-256: $hash"
