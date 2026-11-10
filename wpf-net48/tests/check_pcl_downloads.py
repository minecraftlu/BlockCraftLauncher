"""PCL-style download subsystem static contracts; runtime coverage comes from Windows CI smoke."""
from pathlib import Path
root=Path(__file__).resolve().parent.parent
base=root/'src/BlockCraftLauncher.Wpf'
main=(base/'MainWindow.xaml.cs').read_text('utf8')
for symbol in (
    'BuildDownloadCenter', 'DownloadTaskCenter', 'versionCategory', 'MinecraftVersionCatalog',
    'LoadVersionsAsync', 'InstallSelectedVersionAsync', 'resourceMatchVersion', 'resourceSort',
    'resourcePage', 'BuildModpacks', 'BuildCurseForge', 'BuildResources("resourcepack")',
    'settingsNetworkMode', 'settingsDownloadThreads', 'settingsDownloadRetries',
    'downloads.Enqueue("整合包"', 'downloads.Enqueue("CurseForge"',
    'downloads.Enqueue("Modrinth"', '下载管理', '资源包',
    'YL57xq9U', 'AANobbMI', 'https://optifine.net/downloads',
):
    assert symbol in main, symbol
download=(base/'Services/NetworkDownloader.cs').read_text('utf8')
for symbol in ('RangeHeaderValue', 'ContentRange', 'SHA1.Create()', 'RetryAfterMs',
               'MaxConnectionsPerServer', 'MirrorMojangUri', 'HttpCompletionOption.ResponseHeadersRead',
               'ConcurrentDictionary', '.blockcraft.part'):
    assert symbol in download, symbol
game=(base/'Services/MinecraftDownloadFactory.cs').read_text('utf8')
for symbol in ('MinecraftLauncherParameters.CreateDefault', 'ParallelGameInstaller',
               'MinecraftRouteHandler', 'CreateHttpHandler', 'BMCLAPI', 'PreferMirrorAsync', 'ProbeAsync',
               'VerifiedGameInstaller', 'LocalJsonVersionLoader'):
    assert symbol in game, symbol
# Cross-origin mirror routing must preserve public Range while NEVER forwarding
# account credentials, cookies or origin-specific entity validators.
for symbol in ('CloneDownloadRequest', 'PublicMirrorHeaders', 'Range',
               'sameOrigin', 'GameDownloadTimeout', 'TaskCanceledException'):
    assert symbol in game, symbol
route_test=(base/'Services/MinecraftRouteSanityChecks.cs').read_text('utf8')
for symbol in ('FakeHttpHandler', 'HasAuthorization', 'HasCookie', 'HasIfRange',
               'ServiceUnavailable', 'TaskCanceledException', 'RangeHeaderValue'):
    assert symbol in route_test, symbol
selftest=(base/'Services/DownloadSanityChecks.cs').read_text('utf8')
assert 'MinecraftRouteSanityChecks.RunAsync().GetAwaiter().GetResult()' in selftest
assert 'RegionalNetworkSanityChecks.RunAsync().GetAwaiter().GetResult()' in selftest
catalog=(base/'Services/MinecraftVersionCatalog.cs').read_text('utf8')
for symbol in ('version_manifest_v2.json', '正式版', '快照版', '远古 Beta', 'releaseTime', 'LoadAsync'):
    assert symbol in catalog, symbol
tasks=(base/'Services/DownloadTaskCenter.cs').read_text('utf8')
for symbol in ('SemaphoreSlim', 'INotifyPropertyChanged', 'IProgress<DownloadBytes>',
               'CanRetry', 'Cancel()', 'BytesDownloaded', 'IsIndeterminate'):
    assert symbol in tasks, symbol
modrinth=(base/'Services/ModrinthClient.cs').read_text('utf8')
for symbol in ('gameVersion', 'loader', 'sort', 'offset', 'SearchAsync', 'DownloadVerifiedDetailedAsync'):
    assert symbol in modrinth, symbol
print('PASS: PCL-inspired download center migration contracts; real-world throughput not benchmarked')
