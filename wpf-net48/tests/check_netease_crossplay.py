"""Static checks: official NetEase Bedrock mobile/PC crossplay integration."""
from pathlib import Path
root=Path(__file__).resolve().parent.parent
src=root/"src/BlockCraftLauncher.Wpf"
main=(src/"MainWindow.xaml.cs").read_text("utf8")
service=(src/"Services/NeteaseBedrockSupport.cs").read_text("utf8")
config=(src/"Models/LauncherConfig.cs").read_text("utf8")
settings=(src/"Services/SettingsStore.cs").read_text("utf8")
app=(src/"App.xaml.cs").read_text("utf8")
onefile=(root/"onefile/Program.cs").read_text("utf8")
flow=(root.parent/".github/workflows/wpf-net48-alpha-build.yml").read_text("utf8")
for sym in ("网易跨端联机", "BuildNeteaseCrossplay()", "VerifyNeteaseCrossplayPage()",
            "neteaseLauncherPath", "RefreshNeteaseDiagnostics", "ChooseNeteaseLauncher",
            "LaunchNeteaseClient", "CopyNeteaseChecklist", "OpenNeteaseWebsite"):
    assert sym in main, sym
for sym in ("https://minecraft.com.cn/", "ValidateSelectedLauncher", "DetectInstalledClients",
            "RegistryHive", "RegistryView", "IsSupportedWindowsVersion",
            "RunSelfTests", "ProcessStartInfo"):
    assert sym in service, sym
assert "public string NeteaseLauncherExe" in config
assert "netease_launcher_exe" in settings
assert "NeteaseBedrockSupport.LaunchSelectedLauncher(settings.NeteaseLauncherExe)" in main
assert "基岩互通版" in main and "不是 Java" in main
assert "--netease-ui-selftest" in app and "--netease-ui-selftest" in onefile
assert "window.VerifyNeteaseCrossplayPage();" in app
assert "Smoke test NetEase crossplay from single EXE" in flow
for blocked in ("password", "access_token", "refresh_token", "secret", "WebClient", "HttpClient"):
    assert blocked not in service, blocked
print("PASS: NetEase official crossplay page static and no-credential contracts")
