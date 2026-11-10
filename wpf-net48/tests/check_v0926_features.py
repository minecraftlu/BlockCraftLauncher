"""v0.9.26 -> WPF parity gate. Static source checks; Windows MSBuild is separate."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
src = root / "src/BlockCraftLauncher.Wpf"
main = (src / "MainWindow.xaml.cs").read_text("utf8")
prj = ET.parse(src / "BlockCraftLauncher.Wpf.csproj").getroot()
assert prj.findtext(".//TargetFramework") == "net48"
assert prj.findtext(".//PlatformTarget") == "x64"

for name in (
    "MultiplayerService", "InstanceResourceService", "MrpackInstaller",
    "LauncherReleaseService", "CurseForgeMirrorClient", "ManagedModUpdateService",
    "WebView2RuntimeSupport",
):
    assert (src / "Services" / (name + ".cs")).exists(), name

for symbol in (
    "BuildModpacks", "ImportMrpackFromDisk", "InstallMrpackAsync",
    "BuildCurseForge", "InstallCurseForgeAsync", "BuildMultiplayer",
    "ScanLanWorldsAsync", "JoinLanWorldAsync", "BuildModUpdates",
    "CheckManagedUpdatesAsync", "ApplyManagedUpdatesAsync", "BuildLauncherUpdates",
    "CheckLauncherUpdatesAsync", "InstallLauncherUpdateAsync", "SaveSettings",
):
    assert symbol in main, symbol

modpack = (src / "Services/MrpackInstaller.cs").read_text("utf8")
for symbol in ("SafeRelative", "UnderRoot", "SHA512.Create()", "DownloadVerifiedAsync", "loaderVersion",
               "client-overrides", "Directory.Delete(instance, true)", "MaxDownload"):
    assert symbol in modpack, symbol

cf = (src / "Services/CurseForgeMirrorClient.cs").read_text("utf8")
for symbol in ("RequireEnabled", "AllowedDownload", "MaxDependencies", "DownloadVerifiedAsync",
               "relationType", "File.Exists(dest)", "SHA-1"):
    assert symbol in cf, symbol
assert 'x-api-key' not in cf.lower()
assert "settings.McimEnabled = settingsMcim?.IsChecked == true;" in main

update = (src / "Services/ManagedModUpdateService.cs").read_text("utf8")
for symbol in ("FileSha1(original)", ".blockcraft-backups", "File.Replace", "File.Copy(backup, original, true)"):
    assert symbol in update, symbol

multi = (src / "Services/MultiplayerService.cs").read_text("utf8")
assert 'IPAddress.Parse("224.0.2.60")' in multi
assert '4445' in multi
assert "ParseAddress" in multi

print("PASS: v0.9.26 WPF migration source contracts; Windows behavior still requires testing")
