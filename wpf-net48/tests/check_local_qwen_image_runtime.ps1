param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [Parameter(Mandatory=$true)][string]$Directory
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$instance = [Reflection.BindingFlags]'NonPublic,Instance'
$prefix = 'BlockCraftLauncher.Wpf.Services.'
$output = [IO.Directory]::CreateDirectory((Join-Path $Directory 'contract')).FullName
$root = [IO.Directory]::CreateDirectory((Join-Path $Directory 'models')).FullName
$utf8 = New-Object Text.UTF8Encoding($false)

function Read-Upstream([uri]$Uri, [hashtable]$Headers) {
    foreach ($attempt in 1..3) {
        try {
            return (Invoke-WebRequest -Uri $Uri -UseBasicParsing -Headers $Headers -TimeoutSec 40).Content
        } catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
}

# Extract the exact graphs and pins used by the Windows binary on this commit.
$checks = $assembly.GetType($prefix + 'LocalQwenImageChecks', $true)
$checks.GetMethod('ExportGraphs', $flags).Invoke($null, [object[]]@($output))
$assetType = $assembly.GetType($prefix + 'LocalQwenImageAsset', $true)
$verified = foreach ($name in 'Portable', 'Extractor', 'Encoder', 'Vae', 'Generator', 'Editor') {
    $asset = $assetType.GetField($name, $flags).GetValue($null)
    $url = $assetType.GetField('Source', $instance).GetValue($asset)
    $bytes = $assetType.GetField('Bytes', $instance).GetValue($asset)
    $hash = $assetType.GetField('Sha256', $instance).GetValue($asset)
    if ($url.Scheme -ne 'https' -or $hash -cnotmatch '^[0-9a-f]{64}$' -or $bytes -le 0) {
        throw "Invalid pinned metadata for $name"
    }
    if ($url.Host -eq 'github.com' -and $url.AbsolutePath -match '^/([^/]+/[^/]+)/releases/download/([^/]+)/([^/]+)$') {
        $repo, $tag, $file = $Matches[1], $Matches[2], $Matches[3]
        $headers = @{ Accept = 'application/vnd.github+json' }
        if ($env:GITHUB_TOKEN) { $headers.Authorization = 'Bearer ' + $env:GITHUB_TOKEN }
        $release = Read-Upstream "https://api.github.com/repos/$repo/releases/tags/$tag" $headers | ConvertFrom-Json
        $found = @($release.assets | Where-Object { $_.name -ceq $file })
        if ($found.Count -ne 1 -or $found[0].size -ne $bytes -or $found[0].digest -cne "sha256:$hash") {
            throw "Release asset mismatch for $repo $tag $file"
        }
    } elseif ($url.Host -eq 'huggingface.co' -and $url.AbsolutePath -cmatch '^/([^/]+/[^/]+)/resolve/([0-9a-f]{40})/(.+)$') {
        $repo, $rev, $file = $Matches[1], $Matches[2], $Matches[3]
        # /raw/ returns the small git-lfs pointer from the pinned commit.
        $pointer = (Read-Upstream "https://huggingface.co/$repo/raw/$rev/$file" @{}) -split '\r?\n'
        if ($pointer -cnotcontains "oid sha256:$hash" -or $pointer -cnotcontains "size $bytes") {
            throw "LFS metadata mismatch for $repo $rev $file"
        }
    } else {
        throw "Unexpected publisher or unpinned source for $name"
    }
    Write-Host "Verified $name | $file | $bytes bytes | SHA-256 $hash"
    [ordered]@{ name = $name; source = $url.AbsoluteUri; bytes = $bytes; sha256 = $hash }
}
[IO.File]::WriteAllText((Join-Path $output 'upstream.json'), (ConvertTo-Json -InputObject @($verified)), $utf8)

$settingsType = $assembly.GetType('BlockCraftLauncher.Wpf.Models.LauncherConfig', $true)
$config = [Activator]::CreateInstance($settingsType)
$installerType = $assembly.GetType($prefix + 'LocalQwenImageInstaller', $true)
$constructor = $installerType.GetConstructor($instance, $null, [Type[]]@([string], $settingsType), $null)
$installer = $constructor.Invoke([object[]]@($root, $config))
$limit = New-Object Threading.CancellationTokenSource([TimeSpan]::FromMinutes(23))
try {
    Write-Host 'Downloading and extracting the pinned Windows runtime using the launcher installer.'
    $task = $installerType.GetMethod('SmokeRuntimeAsync', $instance).Invoke($installer, [object[]]@($null, $limit.Token))
    $endpoint = $task.GetAwaiter().GetResult()
    if ($endpoint.Scheme -cne 'http' -or $endpoint.Host -cne '127.0.0.1') { throw 'Expected local-only ComfyUI' }
    $stats = Invoke-RestMethod -Uri ([Uri]::new($endpoint, 'system_stats')) -TimeoutSec 25
    if ($stats.system.comfyui_version -cne '0.46.0' -or 'cpu' -cnotin @($stats.devices | ForEach-Object { $_.type })) {
        throw 'Expected the pinned ComfyUI portable engine operating on CPU'
    }
    Write-Host "ComfyUI $($stats.system.comfyui_version) responded on loopback using the packaged Windows runtime."
} finally {
    $installer.Dispose()
    $limit.Dispose()
}

$runtime = Join-Path $root 'ComfyUI/runtime-0.46.0-cu126/ComfyUI_windows_portable'
$python = Join-Path $runtime 'python_embeded/python.exe'
$checker = Join-Path $PSScriptRoot 'check_local_qwen_graphs.py'
& $python -s $checker (Join-Path $runtime 'ComfyUI') $output
if ($LASTEXITCODE -ne 0) { throw "Native ComfyUI graph validation failed ($LASTEXITCODE)" }
$result = 'ComfyUI 0.46.0 Windows CPU startup and native graph validation passed for Qwen-Image-2512 and Qwen-Image-Edit-2511. A GPU inference run requires the full model weights.'
[IO.File]::WriteAllText((Join-Path $output 'result.txt'), $result, $utf8)
Write-Host $result
