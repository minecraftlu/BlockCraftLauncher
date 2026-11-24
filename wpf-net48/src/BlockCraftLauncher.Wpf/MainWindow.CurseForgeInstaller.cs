using System.IO;
using BlockCraftLauncher.Wpf.Services;
using Microsoft.Win32;

namespace BlockCraftLauncher.Wpf
{
    public partial class MainWindow
    {
        void DownloadOfficialCurseForgeInstaller()
        {
            var picker = new SaveFileDialog
            {
                Title = "保存 CurseForge 官方 Windows 独立安装程序",
                Filter = "Windows 安装程序 (*.exe)|*.exe",
                FileName = CurseForgeAppDownload.SuggestedName,
                DefaultExt = ".exe",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (Directory.Exists(settings.DownloadDirectory)) picker.InitialDirectory = settings.DownloadDirectory;
            if (picker.ShowDialog(this) != true) return;
            string destination = picker.FileName;
            bool overwrite = File.Exists(destination);
            downloads.Enqueue("CurseForge 官方", "CurseForge Windows 安装程序", async (token, stage, bytes) =>
            {
                await CurseForgeAppDownload.SaveAsync(settings, destination, overwrite, stage, bytes, token);
                stage.Report("已保存到 " + destination);
                Status("CurseForge 安装程序已保存到 " + destination);
            });
            Status("CurseForge 安装程序已加入下载任务。");
        }
    }
}
