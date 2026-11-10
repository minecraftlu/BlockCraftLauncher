using System;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// No OAuth calls or account files: verifies recoverable auth failures
    /// are distinguished from unrelated network / Minecraft launch errors.
    /// </summary>
    internal static class AuthSanityChecks
    {
        static void Require(bool value, string purpose)
        {
            if (!value) throw new InvalidOperationException("Microsoft auth recovery regression: " + purpose);
        }

        internal static void Run()
        {
            Require(MicrosoftAccountService.RequiresInteractiveLogin(
                new Exception("Cached RefreshToken of the user was empty. Interactive Microsoft authentication is required.")),
                "missing cached refresh token");
            Require(MicrosoftAccountService.RequiresInteractiveLogin(
                new AggregateException(new InvalidOperationException("wrapper"),
                    new Exception("interactive Microsoft authentication is required"))),
                "nested aggregate exception");
            Require(MicrosoftAccountService.RequiresInteractiveLogin(
                new Exception("OAuth returned invalid_grant")),
                "expired or revoked token");
            Require(MicrosoftAccountService.RequiresInteractiveLogin(
                new MicrosoftReauthenticationRequiredException("重新登录")),
                "typed recovery exception");
            Require(!MicrosoftAccountService.RequiresInteractiveLogin(null),
                "null error");
            Require(!MicrosoftAccountService.RequiresInteractiveLogin(
                new System.IO.IOException("Minecraft resources failed to download")),
                "non-auth download failure must not request reauthentication");
            Require(!MicrosoftAccountService.RequiresInteractiveLogin(
                new Exception("No Java runtime was installed")),
                "non-auth game launch failure must not request reauthentication");
        }
    }
}
