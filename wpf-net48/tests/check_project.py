"""Cross-platform source checks; do not mistake for Windows build tests."""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
PRJ = ROOT / 'src' / 'BlockCraftLauncher.Wpf'


def main():
    for path in (PRJ / 'App.xaml', PRJ / 'MainWindow.xaml', PRJ / 'BlockCraftLauncher.Wpf.csproj', PRJ / 'app.manifest'):
        ET.parse(path)
        print('[OK] XML:', path.relative_to(ROOT))
    project = (PRJ / 'BlockCraftLauncher.Wpf.csproj').read_text('utf-8')
    assert '<TargetFramework>net48</TargetFramework>' in project
    assert '<UseWPF>true</UseWPF>' in project
    assert 'CmlLib.Core' in project
    print('[OK] target framework and WPF')
    assert (PRJ / 'Assets/blockcraft_icon.ico').exists()
    assert (PRJ / 'Assets/blockcraft_icon.png').exists()
    print('[OK] visual assets')
    app = (PRJ / 'MainWindow.xaml.cs').read_text('utf-8')
    assert 'ApplyZoom()' in app and 'ApplyTheme()' in app and 'LaunchOfflineAsync' in app
    print('[OK] main code symbols')
    for xaml in (PRJ / 'App.xaml', PRJ / 'MainWindow.xaml'):
        assert 'BlockCraftLauncher.Wpf' in xaml.read_text('utf-8')
    for csharp in PRJ.rglob('*.cs'):
        source = csharp.read_text('utf-8')
        assert source.count('{') == source.count('}'), csharp
    print('[OK] C# lexical brace count (not compiler verification)')
    settings = (PRJ / 'Services/SettingsStore.cs').read_text('utf-8')
    assert 'settings.wpf-preview.json' in settings
    assert 'settings.json' in settings
    print('[OK] legacy settings read with independent preview output')
    assert not (ROOT / 'reference/BlockCraftLauncher-v0.9.26-IconFix-Source-Reference.zip').exists()
    print('[OK] old source archive is excluded from public contribution')
    print('PASS: static checks only. Requires Windows / Visual Studio to compile & run.')


if __name__ == '__main__':
    main()
