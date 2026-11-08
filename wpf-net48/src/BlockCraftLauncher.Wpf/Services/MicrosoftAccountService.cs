using System;
using System.IO;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// A normal, recoverable condition: the WPF preview account cache has no
    /// usable Microsoft refresh token. The launcher must offer interactive
    /// sign-in rather than silently switching an online-mode account offline.
    /// </summary>
    public sealed class MicrosoftReauthenticationRequiredException : InvalidOperationException
    {
        public MicrosoftReauthenticationRequiredException(string message, Exception inner = null)
            : base(message, inner) { }
    }

    /// <summary>
    /// Microsoft OAuth -> Xbox -> Minecraft Java. All credentials are handled
    /// by the CmlLib provider in the WPF-only account store, not by legacy
    /// Python settings. Never log token values or export account files.
    /// </summary>
    public sealed class MicrosoftAccountService
    {
        readonly JELoginHandler loginHandler;
        MSession current;
        DateTime freshSessionUtc = DateTime.MinValue;

        public MicrosoftAccountService()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockCraftLauncher", "wpf-preview-auth");
            Directory.CreateDirectory(directory);
            loginHandler = new JELoginHandlerBuilder()
                .WithAccountManager(Path.Combine(directory, "accounts.json"))
                .Build();
        }

        static bool SessionUsable(MSession session)
        {
            return session != null && !string.IsNullOrWhiteSpace(session.Username) &&
                !string.IsNullOrWhiteSpace(session.UUID) &&
                !string.IsNullOrWhiteSpace(session.AccessToken);
        }

        public string Username => SessionUsable(current) ? current.Username : null;
        public bool HasSession => SessionUsable(current);

        public async Task<string> SignInAsync()
        {
            // Never leave a prior account's usable session selected if a new
            // interactive sign-in is cancelled or fails.
            current = null;
            freshSessionUtc = DateTime.MinValue;
            var signedIn = await loginHandler.AuthenticateInteractively();
            if (!SessionUsable(signedIn))
                throw new InvalidOperationException("未取得有效的 Minecraft Java 正版身份。请确认微软账号拥有 Minecraft Java 版。");
            current = signedIn;
            freshSessionUtc = DateTime.UtcNow;
            return current.Username;
        }

        public static bool RequiresInteractiveLogin(Exception error)
        {
            if (error == null) return false;
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                    if (RequiresInteractiveLogin(inner)) return true;
            }
            for (Exception ex = error; ex != null; ex = ex.InnerException)
            {
                if (ex is MicrosoftReauthenticationRequiredException) return true;
                string message = ex.Message ?? "";
                if (message.IndexOf("Cached RefreshToken of the user was empty",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("Interactive Microsoft authentication is required",
                        StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("interaction_required", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    message.IndexOf("login_required", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public async Task<MSession> GetSessionAsync()
        {
            // Interactive sign-in just produced a valid Minecraft access token.
            // Re-running a disk-based silent refresh immediately may fail if the
            // underlying account provider did not persist a refresh token.
            if (SessionUsable(current) &&
                DateTime.UtcNow - freshSessionUtc < TimeSpan.FromMinutes(15))
                return current;

            try
            {
                var renewed = await loginHandler.AuthenticateSilently();
                if (!SessionUsable(renewed))
                    throw new MicrosoftReauthenticationRequiredException(
                        "微软账号会话已失效，需要重新完成正版登录。");
                current = renewed;
                freshSessionUtc = DateTime.UtcNow;
                return current;
            }
            catch (Exception ex) when (RequiresInteractiveLogin(ex))
            {
                current = null;
                freshSessionUtc = DateTime.MinValue;
                throw new MicrosoftReauthenticationRequiredException(
                    "微软账号没有有效的登录令牌。请重新登录后启动 Minecraft。", ex);
            }
        }

        public async Task SignOutAsync()
        {
            try { await loginHandler.SignoutWithBrowser(); }
            finally
            {
                current = null;
                freshSessionUtc = DateTime.MinValue;
            }
        }
    }
}