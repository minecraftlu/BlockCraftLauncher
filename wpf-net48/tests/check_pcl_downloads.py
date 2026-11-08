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
):
    assert symbol in main, symbol
download=(base/'Services/NetworkDownloader.cs').read_text('utf8')
for symbol in ('RangeHeaderValue', 'ContentRange', 'SHA1.Create()', 'RetryAfterMs',
               'MaxConnectionsPerServer', 'MirrorMojangUri', 'HttpCompletionOption.ResponseHeadersRead',
               'ConcurrentDictionary', '.blockcraft.part'):
    assert symbol in download, symbol
game=(base/'Services/MinecraftDownloadFactory.cs').read_text('utf8')
for symbol in ('MinecraftLauncherParameters.CreateDefault', 'ParallelGameInstaller',
               'MinecraftRouteHandler', 'MaxConnectionsPerServer', 'BMCLAPI'):
    assert symbol in game, symbol
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
