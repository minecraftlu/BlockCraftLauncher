[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Path)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($env:BCL_CODESIGN_THUMBPRINT)) { return }
$thumbprint = ($env:BCL_CODESIGN_THUMBPRINT -replace '\s+', '').ToUpperInvariant()
if ($thumbprint -notmatch '\A[0-9A-F]{40}\z') {
    throw 'BCL_CODESIGN_THUMBPRINT must be a Windows code signing certificate thumbprint'
}
$certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint"
if (-not $certificate.HasPrivateKey) {
    throw 'The selected certificate does not have an accessible signing key'
}

$signTool = Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty Source
if (-not $signTool) {
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $signTool = Get-ChildItem -Path (Join-Path $sdk '*\x64\signtool.exe') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Parent.Name -as [version] } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $signTool) { throw 'SignTool.exe was not found. Install the Windows SDK signing tools.' }

foreach ($file in $Path) {
    $executable = (Resolve-Path -LiteralPath $file).Path
    & $signTool sign /s My /sha1 $thumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $executable
    if ($LASTEXITCODE -ne 0) { throw "Code signing failed for $(Split-Path $executable -Leaf)" }

    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode' -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint -or -not $signature.TimeStamperCertificate) {
        throw "Expected a valid timestamped Authenticode signature on $(Split-Path $executable -Leaf)"
    }
    Write-Host "Verified Authenticode signature on $(Split-Path $executable -Leaf)"
}
