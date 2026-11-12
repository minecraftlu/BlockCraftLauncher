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
):
    assert style in styles, style

assert 'FontFamily="Segoe MDL2 Assets"' in app
assert 'Style="{StaticResource HeaderGlyph}"' in shell
assert 'new FontFamily("Segoe MDL2 Assets")' in main
for shell_part in ("TopNavigation", "TopMultiplayerButton", "LaunchPane", "ContextNavigation",
                   "BackButton", "DetailHost", "TaskShortcut"):
    assert f'x:Name="{shell_part}"' in shell, shell_part

for symbol in (
    "SectionFor(", "BuildSidebar(", "BuildProfileSelection()", "BuildInstanceOverview()",
    "BuildInstanceSaves()", "SettingsForm()", "SettingRow(", "SearchFields(", "gameProcessText",
    "homeLaunchButton", "ShowDetails(", "detailReturnOffset",
    'FindResource("ServerItemTemplate")', 'FindResource("LocalResourceItemTemplate")',
    'FindResource("ProfileItemTemplate")',
):
    assert symbol in main, symbol

for page in (
    "首页", "版本选择", "版本设置", "存档管理", "游戏版本", "下载管理", "资源中心",
    "光影引擎", "资源包", "整合包", "模组更新", "多人联机", "局域网",
    "启动器更新", "设置", "下载设置", "个性化", "更新设置", "运行日志", "皮肤 DIY", "关于",
):
    assert f'case "{page}":' in main, page

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
assert 'await StartPreparedInstallationAsync()' in install_body
# The updater now takes over AFTER the previous window has closed;
# it verifies the staged package, replaces the original and reopens the new UI.
assert install_body.index('await Task.Run(() => autoUpdater.StartExternalInstaller(preparedUpdate)') < install_body.index(
    'Application.Current.Shutdown()')
assert 'ShowError("未能切换到新版启动器"' in install_body
closing = main.split('Closing +=', 1)[1].split('Closed +=', 1)[0]
assert 'args.Cancel = true' in closing and 'StartPreparedInstallationAsync()' in closing
automatic = main.split('void AdvanceAutomaticRestart()', 1)[1].split('void RefreshLauncherUpdatePanel()', 1)[0]
assert 'StartPreparedInstallationAsync()' in automatic
assert 'DateTime.UtcNow.AddSeconds(2)' in automatic
assert 'if (preparedUpdate != null && settings.AutoRestartAfterUpdateDownload)' in main

for search in (
    'PageHeading(', 'SearchCard(', 'ResultsCard(', 'ApplySearchInputStyle(',
    'FilterChipButton(', 'versionCounter', 'ApplyDownloadFilter()',
    'BuildModDownloadPage', 'resourceVersion', 'resourceCategory', 'resourceLoader',
    'packVersionFilter', 'packSortMode',
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
assert 'UploadSkinToMicrosoftAsync' in main
assert 'GetMicrosoftService().GetSessionAsync()' in main
assert 'MinecraftSkinUploadService.UploadAsync' in main
for fragment in ('uploadToMicrosoft', 'UploadToMicrosoftAsync', 'SaveRecoveryDraft',
                 'OfferRecoveryOnce', 'MarkUnsaved', 'SetArmModel',
                 'File.Replace', 'PixelAt(', 'OnMouseDown(', 'DrawGuides()'):
    assert fragment in (skin + skin_doc), f"Skin DIY feature missing: {fragment}"
uploader = (src / "Services" / "MinecraftSkinUploadService.cs").read_text("utf8")
assert 'https://api.minecraftservices.com/minecraft/profile/skins' in uploader
assert 'AllowAutoRedirect = false' in uploader
assert 'HttpMethod.Post' in uploader
assert 'MultipartFormDataContent' in uploader
assert 'ApiKey' not in uploader
assert 'RunOfflineSelfTest()' in uploader
assert '--skin-selftest' in (root / "onefile" / "Program.cs").read_text("utf8")

assert "CompatibleWpf" in updates
assert "Newer = wpfAsset && IsNewer" in updates
assert (root / "docs" / "STABLE_RELEASE_ACCEPTANCE.md").is_file()

print("PASS: unified navigation, alignment, icon templates and safe release channel labels")
