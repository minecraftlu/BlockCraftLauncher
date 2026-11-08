using System;
using System.IO;
using System.Threading.Tasks;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Microsoft OAuth -> Xbox -> Minecraft Java. The token store is strictly
    /// preview-only; we never import credentials from the legacy Python app.
    /// Authentication is delegated to the maintained CmlLib Microsoft library.
    /// </summary>
    public sealed class MicrosoftAccountService
    {
        readonly JELoginHandler loginHandler;
        MSession current;

        public MicrosoftAccountService()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlockCraftLauncher", "wpf-preview-auth");
            Directory.CreateDirectory(directory);
            // The provider owns token serialization. Never add this folder to
            // a diagnostic report, zip archive, source control or launcher log.
            loginHandler = new JELoginHandlerBuilder()
                .WithAccountManager(Path.Combine(directory, "accounts.json"))
                .Build();
        }

        public string Username => current?.Username;
        public bool HasSession => current != null;

        public async Task<string> SignInAsync()
        {
            current = await loginHandler.AuthenticateInteractively();
            if (current == null || string.IsNullOrWhiteSpace(current.Username))
                throw new InvalidOperationException("未取得有效的 Minecraft Java 玩家身份。请确认该微软账号拥有 Minecraft Java 版。");
            return current.Username;
        }

        public async Task<MSession> GetSessionAsync()
        {
            // This renews the Minecraft token if necessary; do not reuse the
            // UI-displayed name as an authentication credential.
            current = await loginHandler.AuthenticateSilently();
            if (current == null || string.IsNullOrWhiteSpace(current.AccessToken))
                throw new InvalidOperationException("登录会话已过期，请重新登录微软正版账号。");
            return current;
        }

        public async Task SignOutAsync()
        {
            try { await loginHandler.SignoutWithBrowser(); }
            finally { current = null; }
        }
    }
}
