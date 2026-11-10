from pathlib import Path
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
print('PASS one-file bootstrap static contract')
