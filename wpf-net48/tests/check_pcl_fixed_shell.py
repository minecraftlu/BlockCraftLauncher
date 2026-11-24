"""Checks the launcher shell, resource actions and complete rendered route coverage."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
src = root / "src/BlockCraftLauncher.Wpf"
shell = (src / "MainWindow.xaml").read_text(encoding="utf-8")
app = (src / "App.xaml").read_text(encoding="utf-8")
main = (src / "MainWindow.xaml.cs").read_text(encoding="utf-8")
mod = (src / "MainWindow.ModDownloads.cs").read_text(encoding="utf-8")

window = ET.fromstring(shell)
ET.fromstring(app)
assert int(window.attrib["MinWidth"]) <= int(window.attrib["Width"])
assert int(window.attrib["MinHeight"]) <= int(window.attrib["Height"])
# Each supported route is entered with the attached UI control in the gallery.
# Derive the inventory from the page dispatcher so new pages can't go untested.
dispatch = main.split("void Navigate(string page)", 1)[1].split("default: throw", 1)[0]
routes = set(re.findall(r'case "([^"]+)":', dispatch))
probe = (src / "MainWindow.NavigationSelfTest.cs").read_text(encoding="utf8")
tested = set(re.findall(r'(?:Side|Mode|Visit)\("([^"]+)",', probe))
assert routes and routes == tested, f"Missing or unknown rendered routes: {routes ^ tested}"
assert 'window.VerifyRedesignedNavigation()' in (src / "App.xaml.cs").read_text(encoding="utf8")

for phrase in (
    'case "手动安装": PageHost.Content = BuildGameInstallWorkspace(page)',
    "UIElement BuildManualInstallPage()",
    'WorkspaceHeader("安装游戏"',
    '("本地导入",',
    'InstanceResourceService.Import(activeProfile, kind, picker.FileName)',
    'PageHeading("整合包"',
    'PageHeading("下载任务"',
    "Math.Min(3, fields.Length)",
):
    assert phrase in main, "Missing responsive downloads UI: " + phrase

for phrase in (
    "PageHeading(", "SetName(resourceQuery,", "resourceSource)",
    "resourceVersion)", "resourceCategory)", "resourceLoader)", "resourceSort)",
    "modTarget)", "PageColumns(results, modDetailsHost,",
):
    assert phrase in mod, "Mod download must retain full filters and detail: " + phrase

for style in ("AccentButton", "LaunchButton", "HeaderTab", "SidebarItemActive",
              "SearchPanel", "ResultsPanel", "ResourceItemTemplate"):
    assert style in app, style
assert 'Property="Background" Value="{DynamicResource AccentBrush}"' in app
assert 'Property="Foreground" Value="{DynamicResource AccentTextBrush}"' in app

for old_entry in ("手动安装", "CurseForge 官方", "皮肤库", "查找服务器", "局域网", "启动器更新"):
    assert f'Side("{old_entry}",' not in probe, old_entry
assert 'ISelectionItemProvider' in probe and 'selection.Select()' in probe
assert 'FixtureButton(PageHost, "CurseForge 官方")' in probe and 'VerifyFixtureSidebar("资源中心")' in probe
print(f"PASS: {len(routes)} rendered routes, merged workspaces, retained import and source selection")

