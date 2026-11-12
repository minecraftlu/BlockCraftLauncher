"""Checks the independent PCL-inspired fixed-shell UI structure (not a screenshot match)."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
src = root / "src/BlockCraftLauncher.Wpf"
shell = (src / "MainWindow.xaml").read_text(encoding="utf-8")
app = (src / "App.xaml").read_text(encoding="utf-8")
main = (src / "MainWindow.xaml.cs").read_text(encoding="utf-8")
mod = (src / "MainWindow.ModDownloads.cs").read_text(encoding="utf-8")

ET.fromstring(shell)
ET.fromstring(app)
for phrase in (
    'Width="960" Height="600"',
    'ResizeMode="CanMinimize"',
    'ResizeBorderThickness="0"',
    'Visibility="Collapsed"\n                  IsEnabled="False"',
    'x:Name="TopLaunchButton"',
    'x:Name="TopDownloadButton"',
    'x:Name="TopSettingsButton"',
    'x:Name="TopMoreButton"',
):
    assert phrase in shell, "Missing fixed window/header contract: " + phrase

for phrase in (
    "ConfigureFixedLauncherWindow();",
    "void ConfigureFixedLauncherWindow()",
    "MaximizeButton.Visibility = Visibility.Collapsed;",
    'TopMultiplayerButton.Visibility = Visibility.Collapsed',
    'AddNavSection("Minecraft", "游戏版本", "手动安装")',
    'case "手动安装": PageHost.Content = BuildManualInstallPage()',
    "UIElement BuildManualInstallPage()",
    'InstanceResourceService.Import(activeProfile, kind, picker.FileName)',
    'PageHeading("自动安装"',
    'PageHeading("整合包"',
    'PageHeading("下载任务"',
    "Math.Min(3, fields.Length)",
    'new Thickness(16, 12, 16, 8)',
):
    assert phrase in main, "Missing responsive downloads UI: " + phrase

for phrase in (
    'PageHeading("Mod 下载"',
    'SearchFields(("名称", resourceQuery), ("来源", resourceSource)',
    '("版本", resourceVersion)', '("分类", resourceCategory)',
    '("安装到", modTarget)', "PageColumns(results, modDetailsHost, 320)",
):
    assert phrase in mod, "Mod download must retain full filters and detail: " + phrase

for style in ("AccentButton", "LaunchButton", "HeaderTab", "SidebarItemActive",
              "SearchPanel", "ResultsPanel", "ResourceItemTemplate"):
    assert style in app, style
assert 'Property="Background" Value="{DynamicResource AccentBrush}"' in app
assert 'Property="Foreground" Value="{DynamicResource AccentTextBrush}"' in app

print("PASS: fixed PCL-style window, four main tabs, manual install, unified downloads and existing resource bindings")
