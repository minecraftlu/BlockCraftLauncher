using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf.Services;

namespace BlockCraftLauncher.Wpf
{
    public partial class App : Application
    {
        private bool smokeTest;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                if (args.ExceptionObject is Exception error) WriteDiagnostic(error);
            };

            bool authSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--auth-selftest", StringComparison.OrdinalIgnoreCase));
            bool downloadSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--download-selftest", StringComparison.OrdinalIgnoreCase));
            bool dependencySelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--modrinth-deps-selftest", StringComparison.OrdinalIgnoreCase));
            bool saveRestoreSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--saves-restore-selftest", StringComparison.OrdinalIgnoreCase));
            bool instanceDetectionSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--instance-detection-selftest", StringComparison.OrdinalIgnoreCase));
            bool modrinthSearchSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--modrinth-search-selftest", StringComparison.OrdinalIgnoreCase));
            bool downloadPageSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--download-ui-selftest", StringComparison.OrdinalIgnoreCase));
            bool layoutSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--ui-layout-selftest", StringComparison.OrdinalIgnoreCase));
            bool relaunchWindowSelfTest = e.Args.Any(arg =>
                string.Equals(arg, "--window-relaunch-selftest", StringComparison.OrdinalIgnoreCase));
            smokeTest = authSelfTest || downloadSelfTest || dependencySelfTest ||
                saveRestoreSelfTest || instanceDetectionSelfTest || modrinthSearchSelfTest ||
                downloadPageSelfTest || layoutSelfTest || relaunchWindowSelfTest ||
                e.Args.Any(arg =>
                string.Equals(arg, "--smoke-test", StringComparison.OrdinalIgnoreCase));
            // The CI probe must never open a browser, download Minecraft,
            // touch account tokens, or display a GUI message. Both ordinary
            // startup and smoke test explicitly create the same MainWindow.
            if (smokeTest)
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

            try
            {
                VerifyCoreAssemblies();
            }
            catch (Exception ex)
            {
                string log = WriteDiagnostic(ex);
                if (!smokeTest)
                {
                    MessageBox.Show(
                        "启动器依赖检查未通过：\n" + ex.GetBaseException().Message +
                        "\n\n请将完整 ZIP 解压到一个全新文件夹，" +
                        "然后从该文件夹启动 BlockCraftLauncher.Wpf.exe。" +
                        "\n不要只复制 EXE，也不要与旧版 DLL 混用。" +
                        "\n\n诊断日志：\n" + log,
                        "BlockCraft Launcher · DLL 依赖错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                Shutdown(21);
                return;
            }

            base.OnStartup(e);

            // The external updater supplies a one-time random token. Only a
            // fully rendered, visible main window may acknowledge relaunch.
            // Normal starts and every offline selftest never write this file.
            string postUpdateToken = smokeTest && !relaunchWindowSelfTest ? null :
                LauncherRelaunchSignal.ReadToken(e.Args);

            if (authSelfTest)
            {
                try
                {
                    AuthSanityChecks.Run();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    WriteDiagnostic(ex);
                    Shutdown(27);
                }
                return;
            }

            if (downloadSelfTest)
            {
                try
                {
                    DownloadSanityChecks.Run();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    WriteDiagnostic(ex);
                    Shutdown(26);
                }
                return;
            }

            if (dependencySelfTest)
            {
                try
                {
                    ModrinthDependencySanityChecks.Run();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    WriteDiagnostic(ex);
                    Shutdown(28);
                }
                return;
            }

            if (saveRestoreSelfTest)
            {
                try
                {
                    InstanceRestoreSanityChecks.Run();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    WriteDiagnostic(ex);
                    Shutdown(29);
                }
                return;
            }

            if (instanceDetectionSelfTest)
            {
                try
                {
                    InstanceDetectionSanityChecks.Run();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    WriteDiagnostic(ex);
                    Shutdown(30);
                }
                return;
            }

            if (modrinthSearchSelfTest)
            {
                try
                {
                    ModrinthSearchSanityChecks.Run();
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    WriteDiagnostic(ex);
                    Shutdown(31);
                }
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var window = new MainWindow();
                    if (window.Content == null)
                        throw new InvalidOperationException("WPF 主窗口没有初始化内容");
                    if (smokeTest)
                    {
                        if (downloadPageSelfTest)
                            window.VerifyDownloadPageBindings();
                        if (layoutSelfTest)
                            window.VerifyPclLayout();
                        Shutdown(0);
                    }
                    else
                    {
                        MainWindow = window;
                        if (postUpdateToken != null)
                        {
                            bool acknowledged = false;
                            window.ContentRendered += (sender, args) =>
                            {
                                if (acknowledged || !window.IsVisible) return;
                                acknowledged = true;
                                // Restore the new launcher to the foreground
                                // after update without ending Minecraft.
                                if (window.WindowState == WindowState.Minimized)
                                    window.WindowState = WindowState.Normal;
                                window.Activate();
                                window.Focus();
                                try
                                {
                                    LauncherRelaunchSignal.AcknowledgeVisibleWindow(postUpdateToken);
                                }
                                catch (Exception confirmError)
                                {
                                    // Confirmation failure should not crash
                                    // the newly opened launcher.
                                    WriteDiagnostic(confirmError);
                                }
                            };
                        }
                        window.Show();
                    }
                }
                catch (Exception ex)
                {
                    string log = WriteDiagnostic(ex);
                    if (!smokeTest)
                        MessageBox.Show("启动器窗口初始化失败：\n" + ex.GetBaseException().Message
                            + "\n\n错误日志：\n" + log,
                            "BlockCraft Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
                    Shutdown(smokeTest ? 22 : 1);
                }
            }), DispatcherPriority.ApplicationIdle);
        }

        private static void VerifyCoreAssemblies()
        {
            var directory = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var name in new[]
            {
                "CmlLib.Core.Commons",
                "CmlLib.Core",
                "CmlLib.Core.Auth.Microsoft",
                "CmlLib.Core.Installer.Forge",
                "System.Text.Json",
                "Microsoft.Web.WebView2.Core"
            })
            {
                string file = Path.Combine(directory, name + ".dll");
                if (!File.Exists(file))
                    throw new FileNotFoundException("缺少 " + name + ".dll。请使用完整 ZIP 解压后的文件夹。", file);

                AssemblyName identity = AssemblyName.GetAssemblyName(file);
                if (name == "CmlLib.Core.Commons" && identity.Version != new Version(4, 0, 0, 0))
                    throw new FileLoadException("CmlLib.Core.Commons 版本不符：需要 4.0.0.0，实际为 "
                        + identity.Version + "。请清理旧 DLL 后重新解压完整包。", file);

                // Assembly.Load resolves using the same application binding
                // redirects and probing policy as normal WPF startup.
                Assembly.Load(identity);
            }
        }

        private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            string path = WriteDiagnostic(args.Exception);
            if (Current is App app && app.smokeTest)
            {
                args.Handled = true;
                Current.Shutdown(23);
                return;
            }
            MessageBox.Show("启动器发生错误：\n" + args.Exception.GetBaseException().Message
                + "\n\n如涉及缺失 DLL，请完整解压新版 ZIP 后运行，勿只复制 EXE。"
                + "\n\n诊断日志：\n" + path
                + "\n\n如需反馈，请勿发送包含账号令牌的日志。", "BlockCraft Launcher",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Current?.Shutdown(1);
        }

        private static string WriteDiagnostic(Exception error)
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlockCraftLauncher", "wpf-preview-logs");
            string path = Path.Combine(directory, "startup-error.log");
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(path, DateTime.Now.ToString("u") + Environment.NewLine
                    + "EXE directory: " + AppDomain.CurrentDomain.BaseDirectory + Environment.NewLine
                    + error + Environment.NewLine + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { /* Do not replace the original exception. */ }
            return path;
        }
    }
}
