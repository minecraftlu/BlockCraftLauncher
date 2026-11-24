$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSEdition -ne 'Desktop') {
    # Load the launcher into its normal .NET Framework runtime for this probe.
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $windowsPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw "CurseForge live installer check failed ($LASTEXITCODE)." }
    return
}

$project = Join-Path $PSScriptRoot '../src/BlockCraftLauncher.Wpf/bin/Release/net48/BlockCraftLauncher.Wpf.exe'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $project).Path)
$service = $assembly.GetType('BlockCraftLauncher.Wpf.Services.CurseForgeAppDownload', $true)
$config = [Activator]::CreateInstance($assembly.GetType('BlockCraftLauncher.Wpf.Models.LauncherConfig', $true))
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$save = $service.GetMethod('SaveAsync', $flags)
$verify = $service.GetMethod('VerifyPublisher', $flags)
$trusted = $service.GetMethod('TrustedEndpoint', $flags)
$probeDir = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('blockcraft-cf-' + [guid]::NewGuid().ToString('N')))
$path = Join-Path $probeDir.FullName 'CurseForge-Standalone-Installer.exe'
try {
    foreach ($bad in @('http://download.overwolf.com/installer', 'https://download.overwolf.com.test.invalid/',
        'https://user@download.overwolf.com/', 'https://download.overwolf.com:8081/')) {
        if ($trusted.Invoke($null,@([uri]$bad))) { throw "Unsafe redirect accepted: $bad" }
    }
    [IO.File]::WriteAllText($path, 'original file to preserve')
    $unsignedRejected = $false
    try { $verify.Invoke($null,@([string]$path)) } catch { $unsignedRejected = $true }
    if (!$unsignedRejected) { throw 'Unsigned file accepted' }
    $cancel = New-Object Threading.CancellationTokenSource
    try {
        $cancel.Cancel()
        try { $save.Invoke($null,@($config,[string]$path,$true,$null,$null,$cancel.Token)).GetAwaiter().GetResult() } catch { }
    } finally { $cancel.Dispose() }
    if ([IO.File]::ReadAllText($path) -cne 'original file to preserve') { throw 'Cancellation changed the destination' }

    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $token = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(4))
    try {
        $task = $save.Invoke($null,@($config,[string]$path,$true,$null,$null,$token.Token))
        $task.GetAwaiter().GetResult() | Out-Null
    } finally { $token.Dispose() }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid') { throw "Windows signature status: $($signature.Status)" }
    $publisher = $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false)
    if ($publisher.TrimEnd('.') -ne 'Overwolf Ltd') { throw "Unexpected publisher: $publisher" }
    if (@(Get-ChildItem $probeDir.FullName -Force).Length -ne 1) { throw 'Temporary download not cleaned up' }
    Write-Host "PASS: official standalone installer downloaded, publisher $publisher, SHA-256 $((Get-FileHash $path).Hash), $((Get-Item $path).Length) bytes. No installer executed."
} finally {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    if ((Get-ChildItem -LiteralPath $probeDir.FullName -Force | Measure-Object).Count -eq 0) {
        Remove-Item -LiteralPath $probeDir.FullName
    }
}
