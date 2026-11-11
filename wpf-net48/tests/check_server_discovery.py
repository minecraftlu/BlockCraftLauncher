"""Offline source, navigation and safety wiring checks for server discovery."""
from pathlib import Path

root = Path(__file__).resolve().parent.parent
src = root / "src/BlockCraftLauncher.Wpf"
service = (src / "Services/PublicServerDirectoryService.cs").read_text("utf8")
main = (src / "MainWindow.xaml.cs").read_text("utf8")
xaml = (src / "App.xaml").read_text("utf8")
app = (src / "App.xaml.cs").read_text("utf8")
privacy = (root / "docs/PRIVACY.md").read_text("utf8")

# Servers are listed from public, documented, read-only directories;
# Microsoft tokens and private scraped data are never needed.
for host in ("www.mc-monitor.app/api/servers", "minecraftserve.rs/api/servers",
             "api.minecraftserver.id/api/v1/public/servers"):
    assert host in service, host
for keyword in ("AllowAutoRedirect = false", "UseCookies = false",
                "MaxJsonBytes = 768 * 1024", "MaxJsonLength = MaxJsonBytes",
                "TimeSpan.FromSeconds(60)", "DateTime.UtcNow",
                "SafeDirectoryAddress", "ParseDirectory", "SearchAsync",
                "RunOfflineSelfTest", "source == \"MSID\"",
                "edition=JAVA", "edition=java", "http", "429"):
    assert keyword in service, keyword
for bad in ("192.168", "localhost", ".internal", "169", "127", "224"):
    assert bad in service
assert "Authorization" not in service and "Bearer " not in service
assert "ConnectAsync(" not in service and "ScanLanAsync(" not in service
assert "MC Monitor" in service and "MSID" in service
# The public directory never exposes unverified third-party images as WPF Image URLs.
assert 'x:Key="PublicServerResultTemplate"' in xaml
for field in ("Name", "Summary", "Address", "PlayerLabel", "VersionLabel", "StatusLabel", "SourceLabel"):
    assert "{Binding " + field + "}" in xaml
server_template = xaml.split('x:Key="PublicServerResultTemplate"', 1)[1].split('</DataTemplate>', 1)[0]
assert "<Image " not in server_template  # no untrusted remote catalogue icons

# Search is a separate multiplayer subpage; favorites and LAN retain their
# pre-existing pages and only a real user's button triggers lookups or joins.
assert 'AddNavSection("Minecraft Java", "多人联机", "查找服务器", "局域网")' in main
assert 'case "查找服务器": PageHost.Content = BuildServerFinder()' in main
assert 'Btn("查找服务器", () => Navigate("查找服务器"))' in main
for item in ("BuildServerFinder()", "SearchServersAsync(", "serverFinderResults",
             "serverFinderQuery", "serverFinderVersion", "serverFinderProvider",
             "serverFinderSort", "serverFinderOnlineOnly", "serverFinderPrevButton",
             "serverFinderNextButton", "SaveDiscoveredServer", "JoinDiscoveredServerAsync",
             "CopyDiscoveredServer", "OpenDiscoveredSource", "MultiplayerService.ParseAddress",
             "multiplayer.Load()", "multiplayer.Save(saved)", "LaunchAsync(hit.Address)"):
    assert item in main, item
# Navigation must not call Cancel on a completed request that has already
# disposed its CancellationTokenSource. The old direct Cancel implementation
# crashed the entire WPF window after searches/navigation.
assert 'CancelAndDetach(ref serverFinderCancellation)' in main
assert 'serverFinderCancellation = null;' in main
assert 'catch (ObjectDisposedException)' in main
assert '切换页面后仍保留已释放的 CancellationTokenSource' in main
assert 'PublicServerDirectoryService.RunOfflineSelfTest()' in app
assert '"--server-search-selftest"' in app
assert "mc-monitor.app" in privacy and "minecraftserver.id" in privacy
print("PASS: public Minecraft Java server discovery, safe parsing and navigation wiring")
