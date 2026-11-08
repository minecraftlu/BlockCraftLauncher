"""Microsoft refresh-token recovery must be opt-in, confidential, and separately testable."""
from pathlib import Path
root=Path(__file__).resolve().parent.parent
src=root/'src/BlockCraftLauncher.Wpf'
service=(src/'Services/MicrosoftAccountService.cs').read_text('utf8')
ui=(src/'MainWindow.xaml.cs').read_text('utf8')
app=(src/'App.xaml.cs').read_text('utf8')
one=(root/'onefile/Program.cs').read_text('utf8')
workflow=(root.parent/'.github/workflows/wpf-net48-alpha-build.yml').read_text('utf8')

for item in ('MicrosoftReauthenticationRequiredException', 'RequiresInteractiveLogin',
             'Cached RefreshToken of the user was empty', 'AuthenticateSilently',
             'AuthenticateInteractively', 'freshSessionUtc', 'TimeSpan.FromMinutes(15)'):
    assert item in service, item
assert 'return current;' in service
assert 'SignInAsync' in service
assert 'wpf-preview-auth' in service
assert 'Console.WriteLine(current.AccessToken)' not in service

for item in ('catch (MicrosoftReauthenticationRequiredException)', 'MessageBoxButton.YesNo',
             'await LoginMicrosoftAsync();', 'if (!GetMicrosoftService().HasSession)', 
             'await minecraft.LaunchMicrosoftAsync(activeProfile, session, serverAddress)'):
    assert item in ui, item

assert '--auth-selftest' in app and '--auth-selftest' in one
assert 'AuthSanityChecks.Run()' in app
assert 'Offline Microsoft refresh-token recovery tests' in workflow
assert 'Microsoft recovery test from single EXE' in workflow
print('PASS: Microsoft token recovery is opt-in and tested without credentials')
