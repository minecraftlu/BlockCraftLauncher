param(
    [Parameter(Mandatory)][ValidateSet('Preflight', 'Live')][string]$Phase,
    [string]$Tag = 'v0.9.51-wpf-rc.1',
    [string]$Previous = 'v0.9.50-wpf-rc.1',
    [ValidatePattern('^[0-9a-fA-F]{64}$')][string]$PreviousZipSha256 = '0a8c90507f504164741462789c53a14c1785eab7dd6c44794a972127684bf721'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = $Tag.Split('-')[0]
$previousVersion = $Previous.Split('-')[0]
$exe = "BlockCraftLauncher-$version-WPF-OneFile-Windows-x64.exe"
$zip = "BlockCraftLauncher-$version-WPF-net48-Alpha-Windows-x64.zip"
$manifest = 'RELEASE-SHA256SUMS.txt'
$base = "https://github.com/minecraftlu/BlockCraftLauncher/releases"
$dist = (Resolve-Path -LiteralPath 'dist').Path
$work = Join-Path $env:RUNNER_TEMP "blockcraft-$Tag-preview-compatibility"
$probe = Join-Path $work 'PreviewOfferProbe.exe'
$metadataPath = Join-Path $work 'preview-releases.json'

if ($Phase -eq 'Preflight') {
    New-Item -ItemType Directory -Path $work | Out-Null
    $previousName = "BlockCraftLauncher-$previousVersion-WPF-net48-Alpha-Windows-x64.zip"
    $previousZip = Join-Path $work $previousName
    Invoke-WebRequest -Uri "$base/download/$Previous/$previousName" -OutFile $previousZip -TimeoutSec 45
    if ((Get-FileHash -LiteralPath $previousZip -Algorithm SHA256).Hash -ine $PreviousZipSha256) {
        throw "Previous preview ZIP differs from the verified $Previous release"
    }
    $unpacked = Join-Path $work 'old'
    Expand-Archive -LiteralPath $previousZip -DestinationPath $unpacked
    $main = @(Get-ChildItem -LiteralPath $unpacked -Recurse -Filter 'BlockCraftLauncher.Wpf.exe' -File)
    if ($main.Count -ne 1) { throw 'Expected exactly one previous WPF executable' }
    $probe = Join-Path $main[0].DirectoryName 'PreviewOfferProbe.exe'
    $probe | Set-Content -LiteralPath (Join-Path $work 'probe-path.txt') -Encoding utf8
    $source = Join-Path $work 'PreviewOfferProbe.cs'
    @'
using System;
using System.IO;
using System.Reflection;
using BlockCraftLauncher.Wpf.Services;

internal static class PreviewOfferProbe
{
    public static int Main(string[] args)
    {
        var updater = typeof(LauncherAutoUpdateService);
        const BindingFlags internals = BindingFlags.Static | BindingFlags.NonPublic;
        var currentTag = (string)updater.GetField("CurrentTag", internals).GetRawConstantValue();
        if (currentTag != args[2])
            throw new InvalidOperationException("Probe must use the released previous preview updater");

        string json = File.ReadAllText(args[0]);
        string tag = args[1];
        foreach (string mode in new[] { "onefile", "zip" })
        {
            var preview = LauncherAutoUpdateService.SelectOffer(json, "preview", mode);
            var stable = LauncherAutoUpdateService.SelectOffer(json, "stable", mode);
            var current = LauncherAutoUpdateService.SelectOffer(json, "preview", mode, tag);
            if (preview == null || preview.Tag != tag || (stable != null && stable.Tag == tag) || current != null)
                throw new InvalidOperationException("Incorrect preview update selection for " + mode);
        }

        if (args.Length == 5)
        {
            string extracted = (string)updater.GetMethod("ExtractCheckedZip", internals)
                .Invoke(null, new object[] { args[3], args[4] });
            updater.GetMethod("VerifyZipManifest", internals).Invoke(null, new object[] { extracted });
        }

        Console.WriteLine("PASS: " + currentTag + " recognizes both update formats for " + tag + " on the preview channel");
        return 0;
    }
}
'@ | Set-Content -LiteralPath $source -Encoding utf8
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe /platform:x64 "/out:$probe" "/reference:$($main[0].FullName)" $source
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile previous-preview update probe' }
    Copy-Item -LiteralPath ($main[0].FullName + '.config') -Destination ($probe + '.config')

    $candidate = @(@{
        tag_name = $Tag
        prerelease = $true
        draft = $false
        html_url = "$base/tag/$Tag"
        assets = @(@($exe, $zip, $manifest) | ForEach-Object {
            @{
                name = $_
                size = (Get-Item -LiteralPath (Join-Path $dist $_)).Length
                browser_download_url = "$base/download/$Tag/$_"
            }
        })
    })
    ConvertTo-Json -InputObject $candidate -Depth 6 -Compress |
        Set-Content -LiteralPath $metadataPath -Encoding utf8
    & $probe $metadataPath $Tag $Previous (Join-Path $dist $zip) (Join-Path $work 'checked-zip')
    if ($LASTEXITCODE -ne 0) { throw 'Previous-preview update preflight failed' }
    return
}

$probe = (Get-Content -LiteralPath (Join-Path $work 'probe-path.txt') -Raw).Trim()
$metadata = & gh api 'repos/minecraftlu/BlockCraftLauncher/releases?per_page=30'
if ($LASTEXITCODE -ne 0) { throw 'Could not retrieve the public release feed' }
$metadata | Set-Content -LiteralPath $metadataPath -Encoding utf8
& $probe $metadataPath $Tag $Previous
if ($LASTEXITCODE -ne 0) { throw 'Previous preview did not detect the published release' }

$live = Join-Path $work 'published'
New-Item -ItemType Directory -Path $live | Out-Null
foreach ($name in @($manifest, $exe, $zip)) {
    $destination = Join-Path $live $name
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Invoke-WebRequest -Uri "$base/download/$Tag/$name" -OutFile $destination -TimeoutSec 35
            break
        } catch {
            if ($attempt -eq 4) { throw }
            Start-Sleep -Seconds 3
        }
    }
    $actual = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    $expected = (Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash
    if ($actual -cne $expected) { throw "Public release digest mismatch for $name" }
    Write-Host "PASS: public release bytes match the verified build for $name"
}
