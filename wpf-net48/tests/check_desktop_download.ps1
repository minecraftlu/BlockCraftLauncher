# Run sequentially with the install/handoff test on a disposable Windows runner.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'The download-only test writes to the real desktop of a GitHub-hosted Windows runner.'
}

$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$compiled = Join-Path $project 'src/BlockCraftLauncher.Wpf/bin/Release/net48'
$id = [guid]::NewGuid().ToString('N')
$work = Join-Path $env:RUNNER_TEMP "desktop-download-$id"
$runtime = Join-Path $env:LOCALAPPDATA "BlockCraftLauncher/onefile/desktop-download-$id"
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory, [Environment+SpecialFolderOption]::Create)
$base = 'https://github.com/minecraftlu/BlockCraftLauncher/releases'
$oldTag = 'v0.9.51-wpf-rc.1'
$oldName = 'BlockCraftLauncher-v0.9.51-WPF-OneFile-Windows-x64.exe'
$oldDigest = 'b8ea400657131aba4b5df6300ec06b38a7abdcf4d0360c104822f5009958159e'
$tag = 'v0.9.53-wpf-rc.1'
$name = 'BlockCraftLauncher-v0.9.53-WPF-OneFile-Windows-x64.exe'
$digest = '5c8e517eb569056d3c97327a066d3e3df4232192464f7cc2663f43a72777b838'
$destination = Join-Path $desktop $name
if (Test-Path -LiteralPath $destination) { throw 'Desktop fixture name already exists.' }
[void][IO.Directory]::CreateDirectory($work)
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $runtime))
Copy-Item -LiteralPath $compiled -Destination $runtime -Recurse
[IO.File]::WriteAllText((Join-Path $runtime '.blockcraft-ready'), 'desktop download probe')
$old = Join-Path $work $oldName
$oldUrl = "$base/download/$oldTag/$oldName"
Invoke-WebRequest -Uri $oldUrl -OutFile $old -TimeoutSec 90
if ((Get-FileHash -LiteralPath $old).Hash -ne $oldDigest) { throw 'Previous release fixture changed.' }

# Drive the new WPF service with two real releases representing an older install
# and an offered upgrade, independent of whether this development build is public.
$manifest = 'RELEASE-SHA256SUMS.txt'
$releases = @(@{
    tag_name = $tag
    prerelease = $true
    draft = $false
    html_url = "$base/tag/$tag"
    assets = @(
        @{ name = $name; size = 2422784; browser_download_url = "$base/download/$tag/$name" }
        @{ name = $manifest; size = 248; browser_download_url = "$base/download/$tag/$manifest" }
    )
})
$metadata = Join-Path $work 'releases.json'
ConvertTo-Json -InputObject $releases -Depth 5 | Set-Content -LiteralPath $metadata -Encoding utf8

$code = Join-Path $work 'DesktopDownloadProbe.cs'
@'
using System;
using System.IO;
using System.Linq;
using System.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

internal static class DesktopDownloadProbe
{
    private static int Main(string[] args)
    {
        var config = new LauncherConfig {
            UpdateChannel = "preview", AutoCheckUpdates = true, AutoDownloadUpdates = true,
            AutoInstallUpdatesOnExit = false, AutoRestartAfterUpdateDownload = false,
            Root = Path.Combine(args[1], "minecraft"), DownloadDirectory = Path.Combine(args[1], "cache")
        };
        var updater = new LauncherAutoUpdateService(config);
        if (LauncherAutoUpdateService.InstallMode != "onefile") throw new Exception("Expected OneFile mode");
        var offer = LauncherAutoUpdateService.SelectOffer(File.ReadAllText(args[0]), "preview", "onefile", "v0.9.51-wpf-rc.1");
        if (offer == null) throw new Exception("Previous install did not recognize the release");
        var originalUrl = offer.AssetUrl;
        var desktop = Path.GetDirectoryName(args[2]);
        var initial = Directory.GetFileSystemEntries(desktop).OrderBy(x => x).ToArray();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4)))
        {
            // Supply a different genuine public EXE under this offer's manifest.
            // Only this test mutates the selected URL. HTTPS and SHA-256 checks run normally.
            var setter = typeof(LauncherUpdateOffer).GetProperty("AssetUrl").GetSetMethod(true);
            setter.Invoke(offer, new object[] { args[3] });
            try
            {
                updater.PrepareAsync(offer, timeout.Token).GetAwaiter().GetResult();
                throw new Exception("Accepted mismatched download");
            }
            catch (InvalidDataException e)
            {
                if (!e.Message.Contains("SHA-256")) throw;
            }
            if (!initial.SequenceEqual(Directory.GetFileSystemEntries(desktop).OrderBy(x => x)) ||
                Directory.GetDirectories(DownloadPathService.UpdatesRoot(config.DownloadDirectory), "staged-*").Length != 0)
                throw new Exception("Checksum failure left a partial desktop or staged download");
            Console.WriteLine("PASS: rejected wrong release bytes without creating desktop or staging files");

            setter.Invoke(offer, new object[] { originalUrl });
            var prepared = updater.PrepareAsync(offer, timeout.Token).GetAwaiter().GetResult();
            if (!String.Equals(prepared.DesktopPath, args[2], StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(prepared.TargetPath, Environment.GetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP"),
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(prepared.StagedPath))
                throw new Exception("Download-only result did not retain the stage or report the expected desktop and source paths");
        }
        return 0;
    }
}
'@ | Set-Content -LiteralPath $code -Encoding utf8
$probe = Join-Path $runtime 'DesktopDownloadProbe.exe'
$main = Join-Path $runtime 'BlockCraftLauncher.Wpf.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$probe" "/reference:$main" $code
if ($LASTEXITCODE -ne 0) { throw 'Download-only probe compilation failed.' }
Copy-Item -LiteralPath "$main.config" -Destination "$probe.config"
$previousBootstrap = $env:BLOCKCRAFT_SINGLEFILE_BOOTSTRAP
$env:BLOCKCRAFT_SINGLEFILE_BOOTSTRAP = $old
try {
    & $probe $metadata $work $destination $oldUrl
    if ($LASTEXITCODE -ne 0) { throw "Download-only probe failed ($LASTEXITCODE)." }
    if ((Get-FileHash -LiteralPath $old).Hash -ne $oldDigest -or
        (Get-FileHash -LiteralPath $destination).Hash -ne $digest) {
        throw 'Old EXE changed or versioned desktop EXE differs from the public release.'
    }
    Write-Host 'PASS: verified versioned desktop EXE exists immediately with automatic installation and restart disabled; old EXE is intact'
} finally {
    $env:BLOCKCRAFT_SINGLEFILE_BOOTSTRAP = $previousBootstrap
    if ((Test-Path -LiteralPath $destination -PathType Leaf) -and
        (Get-FileHash -LiteralPath $destination).Hash -eq $digest) {
        Remove-Item -LiteralPath $destination
    }
}
