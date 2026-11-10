"""Static wiring checks for the online skin browser; Windows CI runs the real --skin-selftest."""
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "src/BlockCraftLauncher.Wpf"
service = (SRC / "Services/OnlineSkinSearchService.cs").read_text("utf8")
studio = (SRC / "SkinStudioView.cs").read_text("utf8")
app = (SRC / "App.xaml.cs").read_text("utf8")
docs = (ROOT / "docs/PRIVACY.md").read_text("utf8")

# Public endpoints must work without API keys or Microsoft login credentials.
assert "api.minecraftservices.com/minecraft/profile/lookup/name/" in service
assert "api.mojang.com/users/profiles/minecraft/" in service
assert "sessionserver.mojang.com/session/minecraft/profile/" in service
assert "dpplugins.com/api/skins?search=" in service
assert "textures.minecraft.net" in service
assert "Authorization" not in service and "Bearer " not in service

# Reject URL redirects, invalid usernames and non-PNG / oversized textures.
assert "AllowAutoRedirect = false" in service
assert "ValidPlayerInput" in service
assert "MaxPngBytes = 4 * 1024 * 1024" in service
assert "PngBitmapDecoder" in service and "PixelWidth != 64" in service
assert "SafePublicSkinUri" in service
assert "CanonicalMojangTexture" in service

# Actual editor and skin library are used instead of a second ad hoc file store.
assert "BuildOnlineSearch()" in studio
assert "SearchCommunityAsync" in studio and "FindPlayerAsync" in studio
assert "PreviewOnlineSelectionAsync" in studio
assert "Get-AuthenticodeSignature" not in studio  # independent from release signing
assert "SkinEditorDocument.SafeLibraryPath" in studio
assert "preview.SavePng(path)" in studio
assert "document.LoadPng(temporary, item.SlimArms)" in studio
assert "ConfirmDiscard()" in studio
# Open-license, paginated catalog and thumbnail/metadata download layout.
app_xaml = (SRC / "App.xaml").read_text("utf8")
assert 'OnlineSkinResultTemplate' in app_xaml
for field in ("Thumbnail", "Name", "AuthorLabel", "LicenseLabel", "ModelLabel"):
    assert "{Binding " + field + "}" in app_xaml
for keyword in (
    "BrowseMosaicaryAsync", "ParseMosaicaryPage", "source=published",
    "size=64", "per=24", "IsFreeLicense", "cc0", "cc-by-sa-4.0",
    'found is bool', "DownloadThumbnailAsync", "PreviewPngAsync",
    "SkinSourceNote", "?for=view", "mosaicary.com/api/skins/"
):
    assert keyword in service, keyword
for keyword in (
    "onlineCategory", "onlineArms", "onlineSort", "onlineOpenOnly",
    "onlinePrevButton", "onlineNextButton", "LoadOnlineThumbnailsAsync",
    "BrowseMosaicaryAsync", "IsEnabled = item.CanDownload",
    "source.txt", "onlineDetailButton"
):
    assert keyword in studio, keyword
assert "onlineAutoLoaded" not in studio, "online catalogs must not auto-connect on UI open"

assert "OnlineSkinSearchService.RunOfflineSelfTest()" in app
assert "dpplugins.com" in docs and "Mojang" in docs
print("PASS: online skin search/download static wiring (real Windows tests still required)")
