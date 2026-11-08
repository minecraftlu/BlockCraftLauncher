using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraft.NeteaseRoomLab
{
    /// <summary>
    /// Stage 12.3 runs only the local cross-process transport fixture.
    /// Neither child program can connect to a NetEase room or access
    /// authentication cookies; this is NOT a Minecraft game login.
    /// </summary>
    public static class Stage12OfflineBridge
    {
        internal const string WorkerResource = "BlockCraft.Stage12.BedrockWorker.exe";
        internal const string FixtureResource = "BlockCraft.Stage12.IPCFixture.exe";
        private const string GoodOutput = "LOCAL_BRIDGE_IPC_OK_GAME_NOT_JOINED";
        private const int MaxOutputChars = 256;
        private const int TimeoutMilliseconds = 35000;

        public static bool IsEmbedded
        {
            get
            {
                Assembly assembly = typeof(Stage12OfflineBridge).Assembly;
                return assembly.GetManifestResourceInfo(WorkerResource) != null
                    && assembly.GetManifestResourceInfo(FixtureResource) != null;
            }
        }

        public static string ClassifyResult(int exitCode, string output)
        {
            // Never display child process output or arbitrary error text.
            if (exitCode == 0 &&
                string.Equals((output ?? "").Trim(), GoodOutput, StringComparison.Ordinal))
                return "STAGE12_OFFLINE_BRIDGE_OK_GAME_NOT_JOINED："
                    + "本机双进程游戏消息桥接通过（包含大数据帧与停止清理）。"
                    + " 未连接网易服务器，也没有进入 Minecraft 世界。";
            return "STAGE12_OFFLINE_BRIDGE_FAILED_GAME_NOT_JOINED："
                + "本机桥接测试未通过；未连接网易服务器，未进入世界。";
        }

        public static async Task<string> RunAsync(CancellationToken cancellationToken)
        {
            if (!IsEmbedded)
                return "STAGE12_OFFLINE_BRIDGE_UNAVAILABLE："
                    + "本 EXE 未内置 Stage 12 双进程测试组件。";
            if (cancellationToken.IsCancellationRequested)
                return "STAGE12_OFFLINE_BRIDGE_CANCELLED：本机测试已取消。";

            string folder = Path.Combine(Path.GetTempPath(), "BlockCraftRoomLab-Stage12",
                Guid.NewGuid().ToString("N"));
            Process child = null;
            try
            {
                Directory.CreateDirectory(folder);
                string worker = Path.Combine(folder, "Stage12Worker.exe");
                string fixture = Path.Combine(folder, "Stage12IPCFixture.exe");
                ExtractResource(WorkerResource, worker);
                ExtractResource(FixtureResource, fixture);

                var options = new ProcessStartInfo
                {
                    FileName = fixture,
                    Arguments = "-offline-ipc-selftest -worker \"" + worker + "\"",
                    WorkingDirectory = folder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                };
                child = new Process { StartInfo = options };
                if (!child.Start())
                    return ClassifyResult(-1, null);

                Process process = child;
                using (cancellationToken.Register(() => Terminate(process)))
                {
                    // The fixture outputs only one fixed status token; do not
                    // retain, log or display arbitrary subprocess output.
                    Task<string> read = process.StandardOutput.ReadToEndAsync();
                    bool exited = await Task.Run(() => process.WaitForExit(TimeoutMilliseconds));
                    if (!exited)
                    {
                        Terminate(process);
                        return "STAGE12_OFFLINE_BRIDGE_TIMEOUT_GAME_NOT_JOINED："
                            + "本机双进程通信超时；未连接真实网易房间。";
                    }
                    string output = await read;
                    if (cancellationToken.IsCancellationRequested)
                        return "STAGE12_OFFLINE_BRIDGE_CANCELLED：本机测试已取消。";
                    if (output.Length > MaxOutputChars)
                        return ClassifyResult(-1, null);
                    return ClassifyResult(process.ExitCode, output);
                }
            }
            catch (Exception)
            {
                return "STAGE12_OFFLINE_BRIDGE_FAILED_GAME_NOT_JOINED："
                    + "本机组件启动或读取失败；不会展示内部文件路径或账号数据。";
            }
            finally
            {
                if (child != null)
                {
                    Terminate(child);
                    child.Dispose();
                }
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
                catch (Exception) { /* The OS may still be releasing a child executable. */ }
            }
        }

        private static void ExtractResource(string name, string destination)
        {
            using (Stream stream = typeof(Stage12OfflineBridge).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null)
                    throw new InvalidOperationException("Stage12 resource unavailable");
                using (FileStream file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                    stream.CopyTo(file);
            }
        }

        private static void Terminate(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
