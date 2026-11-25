using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal sealed class LocalModelRuntime : IDisposable
    {
        internal const string Model = "qwen3.5:4b";
        static readonly Uri SystemAddress = new Uri("http://127.0.0.1:11434/");
        readonly LauncherConfig settings;
        readonly OllamaLocalApi api;
        readonly SemaphoreSlim prepareGate = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly object sync = new object();
        OllamaOwnedProcess owned;
        bool disposed;

        internal LocalModelRuntime(LauncherConfig settings) : this(settings, new HttpClientHandler {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, UseDefaultCredentials = false
        }) { }

        internal LocalModelRuntime(LauncherConfig settings, HttpMessageHandler handler)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            api = new OllamaLocalApi(handler);
        }

        internal static string ManagedPath(string selectedFolder)
        {
            string path = string.IsNullOrWhiteSpace(selectedFolder) ? DownloadPathService.DefaultDirectory : selectedFolder.Trim();
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' && path[2] != '/' ||
                path.IndexOf(':', 2) >= 0)
                throw new ArgumentException("请选择本机磁盘上的完整文件夹路径。");
            return Path.Combine(Path.GetFullPath(path), "BlockCraft-Qwen");
        }

        internal async Task<bool> CheckReadyAsync(string selectedFolder, CancellationToken ct)
        {
            try { await EnsureReadyAsync(selectedFolder, false, null, ct).ConfigureAwait(false); return true; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is IOException || ex is HttpRequestException ||
                ex is TimeoutException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception ||
                ex is OperationCanceledException)
            { ct.ThrowIfCancellationRequested(); return false; }
        }

        internal async Task<Uri> EnsureReadyAsync(string selectedFolder, bool allowDownload, IProgress<string> progress, CancellationToken ct)
        {
            using (var action = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct))
            {
                var token = action.Token;
                await prepareGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    string root = ManagedPath(selectedFolder);
                    var current = CurrentAddress(root);
                    if (current == null && string.IsNullOrWhiteSpace(selectedFolder) &&
                        await ExistingReadyAsync(token).ConfigureAwait(false)) return SystemAddress;

                    if (current == null)
                    {
                        if (!allowDownload && !OllamaPortablePackage.IsInstalled(root)) throw NotInstalled();
                        VerifiedModelDownload.RejectReparsePoints(Path.Combine(root, "models"));
                        // Receipt verification hashes the native libraries. Keep it off the WPF dispatcher.
                        string executable = await Task.Run(() => OllamaPortablePackage.EnsureAsync(settings, root, progress, token,
                            allowDownload), token).ConfigureAwait(false);
                        if (!NetworkDownloader.TryNormalizeProxy(settings.Proxy, out var proxy))
                            throw new ArgumentException("启动器代理地址格式不正确。");
                        Directory.CreateDirectory(Path.Combine(root, "models"));
                        progress?.Report("正在启动本机 Ollama…");
                        lock (sync)
                        {
                            token.ThrowIfCancellationRequested();
                            if (disposed) throw new ObjectDisposedException(nameof(LocalModelRuntime));
                            owned = OllamaOwnedProcess.Start(executable, root, proxy);
                            current = owned.Address;
                        }
                        try { await WaitForServerAsync(root, current, token).ConfigureAwait(false); }
                        catch { lock (sync) StopOwned(); throw; }
                    }

                    if (await api.HasModelAsync(current, token).ConfigureAwait(false)) return current;
                    if (!allowDownload) throw NotInstalled();
                    using (await LockPullAsync(root, token).ConfigureAwait(false))
                    {
                        if (!await api.HasModelAsync(current, token).ConfigureAwait(false))
                        {
                            progress?.Report("正在从 Ollama 官方模型库下载 Qwen3.5-4B…");
                            await api.PullAsync(current, progress, token).ConfigureAwait(false);
                            if (!await api.HasModelAsync(current, token).ConfigureAwait(false))
                                throw new InvalidDataException("下载结束，但本机模型列表中没有 Qwen3.5-4B。请重试安装。");
                        }
                    }
                    progress?.Report("本机千问已就绪。");
                    return current;
                }
                catch (Exception) when (token.IsCancellationRequested)
                { token.ThrowIfCancellationRequested(); throw; }
                finally { prepareGate.Release(); }
            }
        }

        static InvalidOperationException NotInstalled() =>
            new InvalidOperationException("本机尚未准备好 Qwen3.5-4B，请先点击「准备模型」。");

        async Task<bool> ExistingReadyAsync(CancellationToken ct)
        {
            if (!await api.IsServerAsync(SystemAddress, ct).ConfigureAwait(false)) return false;
            try { return await api.HasModelAsync(SystemAddress, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException ||
                ex is OperationCanceledException || ex is TimeoutException || ex is ObjectDisposedException)
            { ct.ThrowIfCancellationRequested(); return false; }
        }

        Uri CurrentAddress(string root)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(LocalModelRuntime));
                if (owned != null && string.Equals(owned.Root, root, StringComparison.OrdinalIgnoreCase) && owned.IsRunning)
                    return owned.Address;
                StopOwned();
                return null;
            }
        }

        async Task WaitForServerAsync(string root, Uri address, CancellationToken ct)
        {
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(30))
            {
                ct.ThrowIfCancellationRequested();
                if (CurrentAddress(root) == null) throw new InvalidOperationException("本机 Ollama 在启动时退出。");
                if (await api.IsServerAsync(address, ct).ConfigureAwait(false) && CurrentAddress(root) != null) return;
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            throw new TimeoutException("等待本机 Ollama 启动超时，请重试。");
        }

        static async Task<FileStream> LockPullAsync(string root, CancellationToken ct)
        {
            var path = Path.Combine(root, "pull.lock");
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                VerifiedModelDownload.RejectReparsePoints(path);
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException ex) when ((ex.HResult & 0xffff) == 32 || (ex.HResult & 0xffff) == 33)
                { await Task.Delay(300, ct).ConfigureAwait(false); }
            }
        }

        void StopOwned() { owned?.Dispose(); owned = null; }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }
            lifetime.Cancel();
            lock (sync) StopOwned();
            api.Dispose();
        }
    }
}
