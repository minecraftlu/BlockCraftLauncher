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
assert "OnlineSkinSearchService.RunOfflineSelfTest()" in app
assert "dpplugins.com" in docs and "Mojang" in docs
print("PASS: online skin search/download static wiring (real Windows tests still required)")
