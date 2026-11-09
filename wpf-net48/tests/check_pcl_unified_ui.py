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
    "设置", "运行日志", "皮肤 DIY",
):
    assert f'Navigate("{page}")' in main, page

updater = (src / "Services" / "LauncherAutoUpdateService.cs").read_text("utf8")
assert 'RELEASE-SHA256SUMS.txt' in updater
assert 'StartExternalInstaller' in updater
assert 'OpenPreparedDirectlyAsync' in updater
assert 'DirectPreviewExecutable' in updater
assert 'IsPathInsideUpdateStaging' in updater
assert 'updateDirectOpenButton' in main
assert 'OpenDownloadedUpdateDirectlyAsync' in main
assert 'ShowDirectOpenNotice' in main
assert 'ContentRendered' in (src / "App.xaml.cs").read_text("utf8")
assert 'AcknowledgeVisibleWindow' in (src / "App.xaml.cs").read_text("utf8")
assert 'AwaitWindow' in (root / "updater" / "Program.cs").read_text("utf8")
assert 'SelectOffer' in updater
assert 'CheckLauncherUpdatesAsync(false)' in main
assert 'settingsAutoCheck' in main and 'settingsAutoDownload' in main
# The user click itself authorizes installation: a successful upgrade must
# never show the old Windows "确认安装 BlockCraft Launcher 更新" Yes/No modal.
install_body = main.split('async Task InstallLauncherUpdateAsync()', 1)[1].split(
    '// Shared two-column settings rows', 1)[0]
assert 'MessageBox.Show' not in install_body
assert 'StartExternalInstaller(preparedUpdate)' in install_body
assert 'Application.Current.Shutdown()' in install_body
assert 'ShowError("无法启动独立更新程序"' in install_body

for search in (
    'PageHeading(', 'SearchCard(', 'ResultsCard(', 'ApplySearchInputStyle(',
    'FilterChipButton(', 'versionCounter', 'ApplyDownloadFilter()',
    'cfVersionFilter', 'cfCategoryFilter', 'cfSortMode',
    'ApplyCurseForgeFilters()', 'packVersionFilter', 'packSortMode',
    'packPrevButton', 'packNextButton'
):
    assert search in main, f"Missing PCL-style download search UI: {search}"
for design in ('SearchPanel', 'ResultsPanel', 'SearchTextBox', 'SearchComboBox',
               'FilterChip', 'SearchSurfaceBrush', 'SoftDividerBrush'):
    assert design in app, f"Missing shared UI style: {design}"
assert 'Set("SearchSurfaceBrush"' in main

skin = (src / "SkinStudioView.cs").read_text("utf8")
skin_doc = (src / "Services" / "SkinEditorDocument.cs").read_text("utf8")
assert 'skinStudio.VerifyOfflineLayout()' in main
assert 'case "皮肤 DIY"' in main
for name in ("画笔", "橡皮擦", "取色器", "油漆桶", "基础层", "外层",
             "保存到皮肤库", "导入 PNG", "导出 PNG", "Ctrl+Z", "Alex", "Steve"):
    assert name in skin, f"Missing DIY editor control: {name}"
for method in ("RunOfflineSelfTest", "LoadPng", "SavePng", "MirrorLegacyLimb",
               "AvatarImage", "CanPaint", "Undo()", "Redo()", "SafeLibraryPath"):
    assert method in skin_doc, f"Missing skin safety/model behavior: {method}"
assert '--skin-selftest' in (src / "App.xaml.cs").read_text("utf8")
assert '--skin-selftest' in (root / "onefile" / "Program.cs").read_text("utf8")

assert "CompatibleWpf" in updates
assert "Newer = wpfAsset && IsNewer" in updates
assert (root / "docs" / "STABLE_RELEASE_ACCEPTANCE.md").is_file()

print("PASS: unified navigation, alignment, icon templates and safe release channel labels")
