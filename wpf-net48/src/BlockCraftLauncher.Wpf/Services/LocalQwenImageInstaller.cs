using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class LocalQwenImageInstaller : IDisposable
    {
        const string RuntimeName = "runtime-0.46.0-cu126";
        const string ReleaseFolder = "ComfyUI_windows_portable";
        const long RuntimeDiskAllowance = 10L * 1024 * 1024 * 1024;
        readonly string directory;
        readonly LauncherConfig settings;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly object processLock = new object();
        Process server;
        Uri endpoint;
        bool disposed;

        internal LocalQwenImageInstaller(string root, LauncherConfig settings)
        {
            directory = DataDirectory(root);
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        static string DataDirectory(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) throw new ArgumentException("请选择模型保存目录。");
            return Path.Combine(Path.GetFullPath(root), "ComfyUI");
        }

        static string Runtime(string dir) => Path.Combine(dir, RuntimeName, ReleaseFolder);
        static string Python(string dir) => Path.Combine(Runtime(dir), "python_embeded", "python.exe");
        static string Main(string dir) => Path.Combine(Runtime(dir), "ComfyUI", "main.py");
        static string RuntimeReceipt(string dir) => Path.Combine(dir, RuntimeName, ".blockcraft-package");

        static string FileStamp(string file)
        {
            var info = new FileInfo(file);
            return info.Length.ToString(CultureInfo.InvariantCulture) + "/" +
                info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        static string RuntimeStamp(string python, string main) =>
            LocalQwenImageAsset.Portable.Sha256 + "/" + FileStamp(python) + "/" + FileStamp(main);

        static bool RuntimeInstalled(string dir) =>
            File.Exists(Python(dir)) && File.Exists(Main(dir)) && File.Exists(RuntimeReceipt(dir)) &&
            File.ReadAllText(RuntimeReceipt(dir)) == RuntimeStamp(Python(dir), Main(dir));

        static bool WeightInstalled(string dir, LocalQwenImageAsset asset)
        {
            string path = asset.FilePath(dir);
            return File.Exists(path) && new FileInfo(path).Length == asset.Bytes &&
                File.Exists(path + ".verified") && File.ReadAllText(path + ".verified") == asset.Sha256 + "/" + FileStamp(path);
        }

        internal static bool IsInstalled(string root, bool edit)
        {
            string dir = DataDirectory(root);
            return RuntimeInstalled(dir) && LocalQwenImageAsset.Weights(edit).All(a => WeightInstalled(dir, a));
        }

        internal static long DownloadBytes(string root, bool edit)
        {
            string dir = DataDirectory(root);
            long total = LocalQwenImageAsset.Weights(edit).Where(a => !WeightInstalled(dir, a)).Sum(a => Missing(dir, a));
            if (!RuntimeInstalled(dir))
                total += Missing(dir, LocalQwenImageAsset.Extractor) + Missing(dir, LocalQwenImageAsset.Portable);
            return total;
        }

        static long Missing(string dir, LocalQwenImageAsset a)
        {
            string path = a.FilePath(dir);
            if (File.Exists(path) && new FileInfo(path).Length == a.Bytes) return 0;
            string partial = VerifiedModelDownload.PartPath(path, a.Sha256);
            long size = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            return size > a.Bytes ? a.Bytes : a.Bytes - size;
        }

        // Called only for the selected local image operation after an explicit install decision.
        internal async Task<Uri> EnsureReadyAsync(bool edit, IProgress<string> progress, CancellationToken token)
        {
            return await PrepareAsync(edit, false, progress, token).ConfigureAwait(false);
        }

        // Disposable Windows CI runs the same extractor, offline backend and readiness probe
        // on CPU, without fetching the two 20 GB diffusion weights.
        internal Task<Uri> SmokeRuntimeAsync(IProgress<string> progress, CancellationToken ct) =>
            PrepareAsync(false, true, progress, ct);

        async Task<Uri> PrepareAsync(bool edit, bool runtimeOnly, IProgress<string> progress, CancellationToken token)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token))
            {
                var ct = linked.Token;
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Task.Run(async () =>
                    {
                        string architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ??
                            Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
                        if (!Environment.Is64BitOperatingSystem || !architecture.Equals("AMD64", StringComparison.OrdinalIgnoreCase))
                            throw new PlatformNotSupportedException("图像运行环境需要 Windows x64 和支持 CUDA 12.6 的 NVIDIA 显卡。");
                        VerifiedModelDownload.RejectReparsePoints(directory);
                        Directory.CreateDirectory(directory);
                        string installLock = Path.Combine(directory, ".blockcraft-install.lock");
                        VerifiedModelDownload.RejectReparsePoints(installLock);
                        using (new FileStream(installLock, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                        {
                            bool runtimeReady = RuntimeInstalled(directory);
                            long remaining = runtimeOnly
                                ? (runtimeReady ? 0 : Missing(directory, LocalQwenImageAsset.Portable) + Missing(directory, LocalQwenImageAsset.Extractor))
                                : DownloadBytes(Path.GetDirectoryName(directory), edit);
                            if (!runtimeReady) remaining += RuntimeDiskAllowance;
                            var drive = new DriveInfo(Path.GetPathRoot(directory));
                            if (drive.AvailableFreeSpace < remaining)
                                throw new IOException(string.Format("所选磁盘空间不足，本次安装预计还需 {0:F1} GB（含解压空间）。", remaining / 1e9));
                            if (!runtimeReady) await InstallRuntimeAsync(progress, ct).ConfigureAwait(false);
                            if (runtimeOnly) return;
                            foreach (var asset in LocalQwenImageAsset.Weights(edit))
                            {
                                ct.ThrowIfCancellationRequested();
                                var path = asset.FilePath(directory);
                                VerifiedModelDownload.RejectReparsePoints(path);
                                VerifiedModelDownload.RejectReparsePoints(path + ".verified");
                                if (WeightInstalled(directory, asset)) continue;
                                await DownloadAsync(asset, progress, ct).ConfigureAwait(false);
                                File.WriteAllText(path + ".verified", asset.Sha256 + "/" + FileStamp(path));
                            }
                        }
                    }, ct).ConfigureAwait(false);
                    return await StartAsync(progress, runtimeOnly, ct).ConfigureAwait(false);
                }
                finally { gate.Release(); }
            }
        }

        Task DownloadAsync(LocalQwenImageAsset asset, IProgress<string> p, CancellationToken ct) =>
            VerifiedModelDownload.FetchAsync(asset.Source, asset.FilePath(directory), asset.Bytes, asset.Sha256, settings, p, ct);

        async Task InstallRuntimeAsync(IProgress<string> progress, CancellationToken ct)
        {
            // A published runtime folder appears only via the final atomic rename below.
            // Keep any existing damaged files intact for the player's inspection/recovery.
            string target = Path.Combine(directory, RuntimeName);
            VerifiedModelDownload.RejectReparsePoints(target);
            if (Directory.Exists(target))
                throw new IOException("图像运行环境安装不完整，请将 " + target + " 重命名后重试；已下载模型可以继续使用。");
            await DownloadAsync(LocalQwenImageAsset.Portable, progress, ct).ConfigureAwait(false);
            await DownloadAsync(LocalQwenImageAsset.Extractor, progress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            string stage = Path.Combine(directory, ".unpack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                progress?.Report("正在解压 ComfyUI 本机运行环境…");
                var start = new ProcessStartInfo
                {
                    FileName = LocalQwenImageAsset.Extractor.FilePath(directory),
                    Arguments = "x -y -bd " + Quote(LocalQwenImageAsset.Portable.FilePath(directory)),
                    WorkingDirectory = stage,
                    UseShellExecute = false, CreateNoWindow = true
                };
                using (var extractor = Process.Start(start))
                using (ct.Register(() => Stop(extractor)))
                {
                    while (!extractor.HasExited) await Task.Delay(200).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (extractor.ExitCode != 0) throw new IOException("ComfyUI 解压失败，7-Zip 错误码 " + extractor.ExitCode + "。");
                }
                string package = Path.Combine(stage, ReleaseFolder);
                var python = Path.Combine(package, "python_embeded", "python.exe");
                var main = Path.Combine(package, "ComfyUI", "main.py");
                File.WriteAllText(Path.Combine(stage, ".blockcraft-package"), RuntimeStamp(python, main));
                ct.ThrowIfCancellationRequested();
                Directory.Move(stage, target);
                // The extracted version is already committed. The temporary, verified archive is no longer needed.
                try { File.Delete(LocalQwenImageAsset.Portable.FilePath(directory)); } catch (IOException) { }
            }
            finally
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }

        async Task<Uri> StartAsync(IProgress<string> progress, bool cpuOnly, CancellationToken ct)
        {
            Process instance;
            Uri local;
            lock (processLock)
            {
                ct.ThrowIfCancellationRequested();
                if (disposed) throw new ObjectDisposedException(nameof(LocalQwenImageInstaller));
                if (server != null && !server.HasExited) return endpoint;
                server?.Dispose();
                server = null;
                var listener = new TcpListener(IPAddress.Loopback, 0);
                int port;
                try { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
                finally { listener.Stop(); }
                local = new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/");
                VerifiedModelDownload.RejectReparsePoints(Python(directory));
                VerifiedModelDownload.RejectReparsePoints(Main(directory));
                var start = new ProcessStartInfo
                {
                    FileName = Python(directory),
                    WorkingDirectory = Runtime(directory),
                    Arguments = "-s ComfyUI/main.py --listen 127.0.0.1 --port " + port.ToString(CultureInfo.InvariantCulture) +
                        " --base-directory " + Quote(directory) + " --offline --disable-all-custom-nodes --disable-auto-launch" +
                        (cpuOnly ? " --cpu" : ""),
                    UseShellExecute = false, CreateNoWindow = true
                };
                instance = server = Process.Start(start);
                endpoint = local;
            }
            try
            {
                progress?.Report("正在启动本机图像服务，首次载入可能需要数分钟…");
                var wait = Stopwatch.StartNew();
                using (var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
                    { Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 64 * 1024 })
                {
                    while (wait.Elapsed < TimeSpan.FromMinutes(4))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (instance.HasExited)
                            throw new InvalidOperationException("本机图像服务已退出，请检查 NVIDIA 显卡驱动是否支持 CUDA 12.6（退出码 " + instance.ExitCode + "）。");
                        try
                        {
                            using (var response = await http.GetAsync(new Uri(local, "system_stats"), ct).ConfigureAwait(false))
                                if (response.IsSuccessStatusCode &&
                                    (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Contains("\"comfyui_version\"") &&
                                    !instance.HasExited)
                                    return local;
                        }
                        catch (HttpRequestException) { }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                        await Task.Delay(500, ct).ConfigureAwait(false);
                    }
                }
                throw new TimeoutException("本机图像服务启动超时。");
            }
            catch
            {
                lock (processLock)
                {
                    if (ReferenceEquals(server, instance)) { Stop(server); server.Dispose(); server = null; }
                }
                throw;
            }
        }

        static string Quote(string path) => "\"" + path + "\"";
        static void Stop(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            lock (processLock)
            {
                if (disposed) return;
                disposed = true;
                lifetime.Cancel();
                if (server != null) { Stop(server); server.Dispose(); server = null; }
            }
        }
    }
}
