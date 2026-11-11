param([Parameter(Mandatory)][ValidateSet('Preflight', 'Live')][string]$Phase)

$ErrorActionPreference = 'Stop'
if ($env:RELEASE_TAG -ne 'v0.9.42') { throw 'Stable promotion requires exactly v0.9.42' }
$env:BCL_TAG = $env:RELEASE_TAG
$env:BCL_ZIP = $env:RELEASE_ZIP
$env:BCL_SINGLE = $env:RELEASE_EXE
$dist = (Resolve-Path 'dist').Path
$legacy = Join-Path $env:RUNNER_TEMP 'bcl-v0942-promotion'

function Find-OneFilePayload([string]$Sha256, [string]$CacheTag) {
  $cache = Join-Path $env:LOCALAPPDATA 'BlockCraftLauncher/onefile'
  $folders = @(Get-ChildItem -LiteralPath $cache -Directory -Filter "$CacheTag-*" | Where-Object {
    $main = Join-Path $_.FullName 'BlockCraftLauncher.Wpf.exe'
    (Test-Path -LiteralPath (Join-Path $_.FullName '.blockcraft-ready')) -and
      (Test-Path -LiteralPath $main) -and
      (Get-FileHash -LiteralPath $main -Algorithm SHA256).Hash -eq $Sha256
  })
  if ($folders.Count -ne 1) { throw 'Could not locate the exact OneFile payload by tag and hash' }
  return $folders[0].FullName
}

if ($Phase -eq 'Preflight') {
  $env:BCL_OLD_SINGLE = 'BlockCraftLauncher-v0.9.41-WPF-OneFile-Windows-x64.exe'
  $env:BCL_OLD_ZIP = 'BlockCraftLauncher-v0.9.41-WPF-net48-Windows-x64.zip'
  $known = @{
    $env:BCL_OLD_SINGLE = 'd2b2e4fbe2822d3d4a2f25a322d9bcab3a157f770267dd21ab84de4a39f1bbc7'
    $env:BCL_OLD_ZIP = '25e77c78ab6ffe74480e85adc88e0c74da29733c9d23b980492c8d15a5cb4b03'
  }
  New-Item -ItemType Directory -Path $legacy | Out-Null
  foreach ($name in $known.Keys) {
    $file = Join-Path $legacy $name
    Invoke-WebRequest -Uri "https://github.com/minecraftlu/BlockCraftLauncher/releases/download/v0.9.41/$name" `
      -OutFile $file -TimeoutSec 45
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $known[$name]) {
      throw "Unexpected v0.9.41 Stable bytes for $name"
    }
  }
  $newZip = (Get-FileHash -LiteralPath (Join-Path $dist $env:BCL_ZIP) -Algorithm SHA256).Hash.ToLowerInvariant()
  $newSingle = (Get-FileHash -LiteralPath (Join-Path $dist $env:BCL_SINGLE) -Algorithm SHA256).Hash.ToLowerInvariant()
  "BCL_ZIP_SHA=$newZip" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "BCL_SINGLE_SHA=$newSingle" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append

  $page = "https://github.com/$env:GITHUB_REPOSITORY/releases"
  $names = @($env:BCL_SINGLE, $env:BCL_ZIP, 'RELEASE-SHA256SUMS.txt')
  $candidate = @(@{
    tag_name = $env:BCL_TAG
    prerelease = $false
    draft = $false
    html_url = "$page/tag/$env:BCL_TAG"
    assets = @($names | ForEach-Object {
      @{ name = $_; size = (Get-Item -LiteralPath (Join-Path $dist $_)).Length;
         browser_download_url = "$page/download/$env:BCL_TAG/$_" }
    })
  })
  $fixture = Join-Path $legacy 'candidate.json'
  ConvertTo-Json -InputObject $candidate -Depth 6 -Compress | Set-Content -LiteralPath $fixture -Encoding utf8

  $unpacked = Join-Path $legacy 'unpacked'
  Expand-Archive -LiteralPath (Join-Path $legacy $env:BCL_OLD_ZIP) -DestinationPath $unpacked
  $oldWpf = @(Get-ChildItem -LiteralPath $unpacked -Recurse -File -Filter 'BlockCraftLauncher.Wpf.exe')
  if ($oldWpf.Count -ne 1) { throw 'Expected one v0.9.41 Stable WPF payload' }
  $oldRoot = $oldWpf[0].DirectoryName
  $probe = Join-Path $oldRoot 'ReleaseProbe.exe'
  $source = Join-Path $legacy 'ReleaseProbe.cs'
  @'
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BlockCraftLauncher.Wpf.Models;
using BlockCraftLauncher.Wpf.Services;

public static class ReleaseProbe
{
    static void Preflight(string tag, string fixture, string archive, string output)
    {
        string json = File.ReadAllText(fixture);
        foreach (string mode in new[] { "zip", "onefile" })
        {
            var offer = LauncherAutoUpdateService.SelectOffer(json, "stable", mode);
            if (offer == null || offer.Tag != tag ||
                LauncherAutoUpdateService.SelectOffer(json, "preview", mode) != null ||
                LauncherAutoUpdateService.SelectOffer(json, "stable", mode, tag) != null)
                throw new InvalidOperationException("Wrong channel selection for " + mode);
        }

        var type = typeof(LauncherAutoUpdateService);
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        string stage = (string)type.GetMethod("ExtractCheckedZip", methods)
            .Invoke(null, new object[] { archive, output });
        type.GetMethod("VerifyZipManifest", methods).Invoke(null, new object[] { stage });
        Console.WriteLine("PASS: v0.9.41 Stable selects both stable assets and verifies the v0.9.42 ZIP");
    }

    static void Upgrade(string mode, string tag, string sha256, string bootstrap, string signal)
    {
        Environment.SetEnvironmentVariable("BLOCKCRAFT_SINGLEFILE_BOOTSTRAP", mode == "onefile" ? bootstrap : null);
        if (LauncherAutoUpdateService.InstallMode != mode)
            throw new InvalidOperationException("Unexpected install mode");
        var updater = new LauncherAutoUpdateService(new LauncherConfig { UpdateChannel = "stable" });
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(140)))
        {
            LauncherUpdateOffer offer = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                offer = updater.CheckAsync(deadline.Token).GetAwaiter().GetResult();
                if (offer != null && offer.Tag == tag) break;
                Thread.Sleep(4000);
            }
            if (offer == null || offer.Tag != tag)
                throw new InvalidOperationException("Public stable feed did not offer " + tag);
            var prepared = updater.PrepareAsync(offer, deadline.Token).GetAwaiter().GetResult();
            if (!string.Equals(prepared.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Prepared update hash mismatch");
            Console.WriteLine("PASS: v0.9.41 Stable " + mode + " detected, downloaded and verified " + tag);

            string updates = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "updates");
            var before = Directory.GetDirectories(updates, "runner-*");
            updater.StartExternalInstaller(prepared);
            string runner = Directory.GetDirectories(updates, "runner-*").Except(before).Single();
            File.WriteAllText(signal + ".tmp", Path.Combine(runner, "BlockCraftLauncher.Updater.exe"));
            File.Move(signal + ".tmp", signal);
            // Leave the old process alive until the harness holds the real installer handle.
            var wait = Stopwatch.StartNew();
            while (!File.Exists(signal + ".go") && wait.Elapsed < TimeSpan.FromSeconds(15))
                Thread.Sleep(100);
            if (!File.Exists(signal + ".go")) throw new TimeoutException("Could not attach to updater");
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args[0] == "fixture") Preflight(args[1], args[2], args[3], args[4]);
            else Upgrade(args[0], args[1], args[2], args[3], args[4]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
'@ | Set-Content -LiteralPath $source -Encoding utf8
  $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
  & $csc /nologo /target:exe /platform:x64 "/out:$probe" "/reference:$($oldWpf[0].FullName)" $source
  if ($LASTEXITCODE -ne 0) { throw 'Could not compile the updater probe' }
  Copy-Item -LiteralPath ($oldWpf[0].FullName + '.config') -Destination ($probe + '.config')
  $checked = Join-Path $dist 'checked-zip'
  & $probe fixture $env:BCL_TAG $fixture (Join-Path $dist $env:BCL_ZIP) $checked
  if ($LASTEXITCODE -ne 0) { throw 'v0.9.41 Stable preflight failed' }
  $newWpf = @(Get-ChildItem -LiteralPath $checked -Recurse -File -Filter 'BlockCraftLauncher.Wpf.exe')
  if ($newWpf.Count -ne 1) { throw 'Expected one verified v0.9.42 WPF payload' }

  $settings = Join-Path $env:APPDATA 'BlockCraftLauncher/settings.wpf-preview.json'
  if (Test-Path -LiteralPath $settings) { throw 'Expected a clean Windows runner settings profile' }
  New-Item -ItemType Directory -Path (Split-Path $settings) -Force | Out-Null
  '{"auto_check_updates":false,"update_channel":"stable","account_mode":"offline"}' |
    Set-Content -LiteralPath $settings -Encoding utf8
  $bootstrap = Join-Path $legacy $env:BCL_OLD_SINGLE
  $start = Start-Process -FilePath $bootstrap -ArgumentList '--smoke-test' -PassThru
  if (-not $start.WaitForExit(70000)) { $start.Kill(); throw 'v0.9.41 Stable OneFile preflight timed out' }
  if ($start.ExitCode -ne 0) { throw 'v0.9.41 Stable OneFile preflight failed' }
  $oldWpfSha = (Get-FileHash -LiteralPath $oldWpf[0].FullName -Algorithm SHA256).Hash
  $oldCache = Find-OneFilePayload $oldWpfSha 'v0.9.41'
  Copy-Item -LiteralPath $probe, ($probe + '.config') -Destination $oldCache
  "BCL_ZIP_PROBE=$probe" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "BCL_SINGLE_PROBE=$(Join-Path $oldCache 'ReleaseProbe.exe')" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "BCL_OLD_BOOTSTRAP=$bootstrap" | Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  "BCL_NEW_WPF_SHA=$((Get-FileHash -LiteralPath $newWpf[0].FullName -Algorithm SHA256).Hash)" |
    Out-File -FilePath $env:GITHUB_ENV -Encoding utf8 -Append
  return
}

$zipTarget = Split-Path $env:BCL_ZIP_PROBE
$keep = Join-Path $zipTarget 'release-probe.keep'
'unmanaged file in the old install' | Set-Content -LiteralPath $keep

function Test-Upgrade($mode, $probe, $sha, $bootstrap) {
  $signal = Join-Path $env:RUNNER_TEMP "bcl-$mode-installer.txt"
  $info = [Diagnostics.ProcessStartInfo]::new($probe)
  $info.UseShellExecute = $false
  $info.WorkingDirectory = Split-Path $probe
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  foreach ($arg in @($mode, $env:BCL_TAG, $sha, $bootstrap, $signal)) { $info.ArgumentList.Add($arg) }
  $old = [Diagnostics.Process]::Start($info)
  $stdout = $old.StandardOutput.ReadToEndAsync()
  $stderr = $old.StandardError.ReadToEndAsync()
  $installer = $null
  $main = $null
  try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $signal) -and -not $old.HasExited -and $watch.Elapsed.TotalSeconds -lt 150) {
      Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $signal)) { throw "$mode did not prepare the public update" }
    $helper = Get-Content -LiteralPath $signal -Raw
    $helpers = @(Get-Process -Name 'BlockCraftLauncher.Updater' -ErrorAction SilentlyContinue |
      Where-Object { $_.Path -eq $helper })
    if ($helpers.Count -ne 1) { throw "$mode updater helper process was not started" }
    $installer = $helpers[0]
    $null = $installer.Handle
    New-Item -ItemType File -Path "$signal.go" | Out-Null
    if (-not $old.WaitForExit(10000) -or $old.ExitCode -ne 0) { throw "$mode old update probe failed" }
    if (-not $installer.WaitForExit(125000) -or $installer.ExitCode -ne 0) {
      throw "$mode external update helper did not finish successfully"
    }

    if ($mode -eq 'zip') {
      $main = Join-Path $zipTarget 'BlockCraftLauncher.Wpf.exe'
      if ((Get-Content -LiteralPath $keep -Raw).Trim() -ne 'unmanaged file in the old install') {
        throw 'ZIP update changed an unmanaged file in the install folder'
      }
    } else {
      if ((Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash -ne $sha) {
        throw 'OneFile update did not replace the original bootstrap'
      }
      $main = Join-Path (Find-OneFilePayload $env:BCL_NEW_WPF_SHA 'v0.9.42') 'BlockCraftLauncher.Wpf.exe'
    }
    if ((Get-FileHash -LiteralPath $main -Algorithm SHA256).Hash -ne $env:BCL_NEW_WPF_SHA) {
      throw "$mode installed an unexpected WPF executable"
    }
    $windows = @(Get-Process -Name 'BlockCraftLauncher.Wpf' -ErrorAction SilentlyContinue |
      Where-Object { $_.Path -eq $main -and $_.MainWindowHandle -ne [IntPtr]::Zero })
    if ($windows.Count -ne 1) { throw "$mode did not reopen the v0.9.42 main window" }
    Write-Host "PASS: v0.9.41 Stable $mode installed v0.9.42 and reopened the main window"
  } finally {
    if (-not $old.HasExited) { $old.Kill(); $old.WaitForExit() }
    if ($installer) {
      if (-not $installer.HasExited) { $installer.Kill() }
      $installer.Dispose()
    }
    if ($main) {
      Get-Process -Name 'BlockCraftLauncher.Wpf' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $main } | Stop-Process -Force
    }
    if ($stdout.Wait(2000) -and $stdout.Result) { Write-Host $stdout.Result.Trim() }
    if ($stderr.Wait(2000) -and $stderr.Result) { Write-Host $stderr.Result.Trim() }
    $old.Dispose()
  }
}
Test-Upgrade 'zip' $env:BCL_ZIP_PROBE $env:BCL_ZIP_SHA '-'
Test-Upgrade 'onefile' $env:BCL_SINGLE_PROBE $env:BCL_SINGLE_SHA $env:BCL_OLD_BOOTSTRAP
$global:LASTEXITCODE = 0
Write-Host 'PASS: v0.9.41 stable to v0.9.42 stable ZIP and OneFile in-place update'
