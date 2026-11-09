"""Offline source gate for unified PCL-inspired layout (not visual approval)."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
src = root / "src" / "BlockCraftLauncher.Wpf"
app_path = src / "App.xaml"
shell_path = src / "MainWindow.xaml"
main_path = src / "MainWindow.xaml.cs"
service_path = src / "Services" / "LauncherReleaseService.cs"

for file in (app_path, shell_path):
    ET.parse(file)

app = app_path.read_text("utf8")
shell = shell_path.read_text("utf8")
main = main_path.read_text("utf8")
updates = service_path.read_text("utf8")
styles = {
    node.attrib.get("{http://schemas.microsoft.com/winfx/2006/xaml}Key")
    for node in ET.parse(app_path).getroot().iter()
}

for style in (
    "HeaderTab", "HeaderTabActive", "SidebarItem", "SidebarItemActive",
    "AccentButton", "Panel", "ResourceItemTemplate", "VersionItemTemplate",
    "DownloadItemTemplate", "ServerItemTemplate", "LocalResourceItemTemplate",
    "CurseForgeItemTemplate",
):
    assert style in styles, style

assert 'FontFamily="Segoe MDL2 Assets"' in app
assert 'FontFamily="Segoe MDL2 Assets"' in shell
assert 'new FontFamily("Segoe MDL2 Assets")' in main
assert 'CornerRadius="19"' in app  # Selected header pill
assert '<ColumnDefinition x:Name="SideColumn" Width="188"/>' in shell
assert 'SideColumn.Width = page == "首页" ? new GridLength(0) : new GridLength(188);' in main

for symbol in (
    "SettingsForm()", "SettingRow(", "gameProcessText", "homeLaunchButton",
    'FindResource("ServerItemTemplate")',
    'FindResource("LocalResourceItemTemplate")',
):
    assert symbol in main, symbol

for page in (
    "首页", "游戏版本", "下载管理", "资源中心", "光影引擎", "资源包",
    "整合包", "CurseForge", "模组更新", "多人联机", "启动器更新",
    "设置", "运行日志",
):
    assert f'Navigate("{page}")' in main, page

updater = (src / "Services" / "LauncherAutoUpdateService.cs").read_text("utf8")
assert 'RELEASE-SHA256SUMS.txt' in updater
assert 'StartExternalInstaller' in updater
assert 'ContentRendered' in (src / "App.xaml.cs").read_text("utf8")
assert 'AcknowledgeVisibleWindow' in (src / "App.xaml.cs").read_text("utf8")
assert 'AwaitWindow' in (root / "updater" / "Program.cs").read_text("utf8")
assert 'SelectOffer' in updater
assert 'CheckLauncherUpdatesAsync(false)' in main
assert 'settingsAutoCheck' in main and 'settingsAutoDownload' in main
assert "CompatibleWpf" in updates
assert "Newer = wpfAsset && IsNewer" in updates
assert (root / "docs" / "STABLE_RELEASE_ACCEPTANCE.md").is_file()

print("PASS: unified navigation, alignment, icon templates and safe release channel labels")
