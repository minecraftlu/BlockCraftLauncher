"""Regression gate for a read-only DownloadJob.Percent WPF progress binding."""
from pathlib import Path
root = Path(__file__).resolve().parent.parent
main = (root / "src/BlockCraftLauncher.Wpf/MainWindow.xaml.cs").read_text("utf-8")
app = (root / "src/BlockCraftLauncher.Wpf/App.xaml.cs").read_text("utf-8")
bootstrap = (root / "onefile/Program.cs").read_text("utf-8")
workflow = (root.parent / ".github/workflows/wpf-net48-alpha-build.yml").read_text("utf-8")
start = main.index("UIElement BuildDownloadCenter()")
end = main.index("internal void VerifyDownloadPageBindings()", start)
page = main[start:end]
assert 'new Binding("SelectedItem.Percent")' in page
assert 'Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = 0d' in page
assert 'new Binding("SelectedItem.IsIndeterminate")' in page
assert 'Source = downloadList, Mode = BindingMode.OneWay, FallbackValue = false' in page
assert "void VerifyDownloadPageBindings()" in main
assert "window.VerifyDownloadPageBindings();" in app
assert "--download-ui-selftest" in app and "--download-ui-selftest" in bootstrap
assert "Smoke test download page from single EXE" in workflow
print("PASS: WPF download page read-only progress binding regression contract")
