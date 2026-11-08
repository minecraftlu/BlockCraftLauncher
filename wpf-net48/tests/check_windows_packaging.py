"""Verify Windows CI wiring and package staging (not executable compilation)."""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
project = ET.parse(ROOT / 'src/BlockCraftLauncher.Wpf/BlockCraftLauncher.Wpf.csproj').getroot()
assert project.findtext('.//TargetFramework') == 'net48'
assert project.findtext('.//UseWPF') == 'true'
assert project.findtext('.//OutputType') == 'WinExe'

script = (ROOT / 'build/build-windows.bat').read_text('utf8')
workflow_path = ROOT / '.github/workflows/wpf-alpha-build.yml'
if not workflow_path.exists():
    workflow_path = ROOT.parent / '.github/workflows/wpf-net48-alpha-build.yml'
workflow = workflow_path.read_text('utf8')
packager = (ROOT / 'build/package-windows.ps1').read_text('utf8')

assert 'vswhere.exe' in script and '-restore' in script and 'MSBUILD_EXE' in script
assert 'package-windows.ps1' in script
assert 'windows-2022' in workflow and 'microsoft/setup-msbuild@v2' in workflow
assert 'upload-artifact@v4' in workflow and 'build-windows.bat' in workflow
assert 'Get-FileHash' in packager and 'Compress-Archive' in packager
assert 'BlockCraftLauncher.Wpf.exe' in packager
print('PASS: Windows MSBuild/packaging contract (static checks only)')
