$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\src\BlockCraftLauncher.Wpf\bin\Release\net48\BlockCraftLauncher.Wpf.exe'
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }
$checksum = (Get-FileHash -Algorithm SHA256 $exe).Hash.ToLowerInvariant()
$target = "$exe.sha256"
"$checksum  BlockCraftLauncher.Wpf.exe" | Set-Content $target -Encoding ASCII
Write-Host "SHA256: $checksum"
