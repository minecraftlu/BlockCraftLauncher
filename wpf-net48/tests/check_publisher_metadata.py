"""Validate Windows file-descriptive publisher metadata, not Authenticode."""
import pathlib
import xml.etree.ElementTree as ET
root = pathlib.Path(__file__).resolve().parents[1]
projects = [
    root / "src/BlockCraftLauncher.Wpf/BlockCraftLauncher.Wpf.csproj",
    root / "onefile/BlockCraftLauncher.SingleFile.csproj",
    root / "updater/BlockCraftLauncher.Updater.csproj",
]
for filename in projects:
    xml = ET.parse(filename).getroot()
    for key,value in {"Company":"晓银","Authors":"晓银","Product":"BlockCraft Launcher","Version":"0.9.48.0","FileVersion":"0.9.48.0"}.items():
        found=xml.findtext(f"./PropertyGroup/{key}")
        assert found == value, (filename.name,key,found,value)
    print("PASS: metadata",filename.name)
manifest=(root/"src/BlockCraftLauncher.Wpf/app.manifest").read_text(encoding="utf-8")
assert 'version="0.9.48.0" name="BlockCraftLauncher.Wpf"' in manifest
print("PASS: descriptive CompanyName metadata, no Authenticode identity claim")
