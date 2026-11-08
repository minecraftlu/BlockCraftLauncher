using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace BlockCraftLauncher.Wpf
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                if (args.ExceptionObject is Exception error) WriteDiagnostic(error);
            };
            base.OnStartup(e);
        }

        private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            string path = WriteDiagnostic(args.Exception);
            string details = args.Exception.GetBaseException().Message;
            MessageBox.Show("启动器发生错误：\n" + details
                + "\n\n诊断日志：\n" + path
                + "\n\n如需反馈，请勿发送包含账号令牌的日志。", "BlockCraft Launcher",
                MessageBoxButton.OK, MessageBoxImage.Error);
            // Avoid continuing with a partially initialized main window.
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
                    + error + Environment.NewLine + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception) { /* Never let diagnostic failure hide the original error. */ }
            return path;
        }
    }
}
