from pathlib import Path
import re
import xml.etree.ElementTree as ET
r=Path(__file__).resolve().parent.parent
p=ET.parse(r/'onefile/BlockCraftLauncher.SingleFile.csproj').getroot()
assert p.findtext('.//TargetFramework')=='net48'
assert p.findtext('.//PlatformTarget')=='x64'
s=(r/'onefile/Program.cs').read_text('utf8')
for value in ('BlockCraftLauncher.Payload.zip','ZipArchiveMode.Read','SHA256SUMS.txt','CmlLib.Core.Commons.dll','ValidCache','Directory.Move','--smoke-test'):
    assert value in s,value
b=(r/'build/build-onefile.ps1').read_text('utf8')
assert 'CreateFromDirectory' in b and '0x8664' in b
# Regression: the first v0.9.37 stable EXE was labelled v0.9.37 but
# extracted to an old v0.9.36 cache because the bootstrap's Release
# constant wasn't advanced. Keep ALL package and updater tags identical.
current = (r/'src/BlockCraftLauncher.Wpf/Services/LauncherAutoUpdateService.cs').read_text('utf8')
packager = (r/'build/package-windows.ps1').read_text('utf-8-sig')
bootstrap = re.search(r'private const string Release = "(v\d+\.\d+\.\d+)";', s)
updater = re.search(r'internal const string CurrentTag = "(v\d+\.\d+\.\d+)(?:-wpf-rc\.\d+)?";', current)
assert bootstrap and updater and bootstrap.group(1) == updater.group(1), 'OneFile cache tag != updater tag'
assert updater.group(1) in b, 'OneFile artifact filename does not match binary version'
assert updater.group(1) in packager, 'ZIP version does not match binary version'

print('PASS one-file bootstrap static contract')
