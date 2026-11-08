"""Cross-platform static regression checks for phase 2 (NOT C# compilation)."""
from pathlib import Path
import xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parent.parent
SRC=ROOT/'src'/'BlockCraftLauncher.Wpf'

def check():
    project=ET.parse(SRC/'BlockCraftLauncher.Wpf.csproj').getroot()
    packages={x.attrib['Include']:x.attrib['Version'] for x in project.iter('PackageReference')}
    for name in ('CmlLib.Core','CmlLib.Core.Auth.Microsoft','CmlLib.Core.Installer.Forge'):
        assert name in packages, name
    assert project.find('.//TargetFramework').text == 'net48'
    auth=(SRC/'Services/MicrosoftAccountService.cs').read_text('utf8')
    for line in ('AuthenticateInteractively','AuthenticateSilently','SignoutWithBrowser','wpf-preview-auth','WithAccountManager'):
        assert line in auth, line
    assert 'settings.json' not in auth and 'Console.WriteLine(current.AccessToken)' not in auth
    mod=(SRC/'Services/ModLoaderInstaller.cs').read_text('utf8')
    for loader in ('FabricInstaller','QuiltInstaller','ForgeInstaller','NeoForgeInstaller'):
        assert loader in mod
    assert 'File.Exists(metadata)' in mod and 'await launcher.InstallAsync(installedId)' in mod
    main=(SRC/'MainWindow.xaml.cs').read_text('utf8')
    for symbol in ('LaunchMicrosoftAsync', 'GetSessionAsync','LoginMicrosoftAsync','SignOutAsync','modLoaders.InstallAsync','versionCancelButton'):
        assert symbol in main, symbol
    modrinth=(SRC/'Services/ModrinthClient.cs').read_text('utf8')
    assert 'loaderValues.Any' in modrinth and 'sha1.Length != 40' in modrinth
    network=(SRC/'Services/NetworkDownloader.cs').read_text('utf8')
    assert 'File.Replace(temp, destination' in network and 'RetryAfterMs=' in network
    assert 'CmlLib.Core' in (ROOT/'docs/MIGRATION_STATUS.md').read_text('utf8')
    print('PASS: 7 source checks / packaging constraints, NOT a Windows WPF build')

if __name__=='__main__':
    check()
