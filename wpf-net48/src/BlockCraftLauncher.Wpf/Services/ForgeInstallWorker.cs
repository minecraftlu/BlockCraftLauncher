using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    // CmlLib extracts Forge packages using Path.GetTempPath(). An isolated process
    // keeps its installer and Java processors on the selected volume per job.
    internal static class ForgeInstallWorker
    {
        internal const string Argument = "--forge-install-worker";
        const string Prefix = "BLOCKCRAFT-FORGE ";

        sealed class InstallRequest
        {
            public string Game, Loader, Build, Root, Cache, Java, Network, Proxy;
            public int Threads, Retries;
        }

        sealed class Message
        {
            public string Stage, Error;
            public long Completed, Total;
            public bool HasBytes;
            public GameProfile Profile;
        }

        sealed class WorkerProgress<T> : IProgress<T>
        {
            readonly Action<T> report;
            public WorkerProgress(Action<T> report) { this.report = report; }
            public void Report(T value) => report(value);
        }

        internal static ProcessStartInfo StartInfo(string scratch)
        {
            var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, Argument)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = scratch,
                RedirectStandardInput = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
            };
            foreach (string key in new[] { "TMP", "TEMP", "TMPDIR" }) start.EnvironmentVariables[key] = scratch;
            start.EnvironmentVariables["_JAVA_OPTIONS"] = ((start.EnvironmentVariables["_JAVA_OPTIONS"] ?? "") +
                " \"-Djava.io.tmpdir=" + scratch + "\"").Trim();
            return start;
        }

        internal static async Task<GameProfile> InstallAsync(LauncherConfig config, string game, string loader, string build,
            IProgress<string> stage, IProgress<DownloadBytes> bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var request = new InstallRequest { Game = game, Loader = loader, Build = build, Root = config.Root,
                Cache = config.DownloadDirectory, Java = config.JavaPath, Network = config.NetworkMode, Proxy = config.Proxy,
                Threads = config.DownloadThreads, Retries = config.DownloadRetries };
            var json = new JavaScriptSerializer();
            string scratch = Path.Combine(DownloadPathService.ForgeTempRoot(request.Cache), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            try
            {
                using var job = ChildProcessJob.Start(StartInfo(scratch));
                var child = job.Process;
                try
                {
                    // Start only after the worker belongs to our Windows job. ASCII framing keeps
                    // Unicode paths intact independently of the player's console code page.
                    child.StandardInput.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Serialize(request))));
                    child.StandardInput.Flush();
                    using var deadline = new Timer(_ => job.Terminate(), null, Timeout.Infinite, Timeout.Infinite);
                    using var cancel = token.Register(() =>
                    {
                        deadline.Change(4000, Timeout.Infinite);
                        try { child.StandardInput.WriteLine("stop"); child.StandardInput.Flush(); }
                        catch (Exception ex) when (ex is IOException || ex is InvalidOperationException) { }
                    });
                    GameProfile result = null;
                    string failure = null;
                    while (true)
                    {
                        var line = await child.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                        var message = json.Deserialize<Message>(line.Substring(Prefix.Length));
                        if (message.Stage != null) stage?.Report(message.Stage);
                        if (message.HasBytes) bytes?.Report(new DownloadBytes(message.Completed, message.Total));
                        if (message.Profile != null || message.Error != null)
                        {
                            result = message.Profile;
                            failure = message.Error;
                            child.StandardInput.Close();
                            break;
                        }
                    }
                    await Task.Run(() => child.WaitForExit()).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (child.ExitCode != 0 || result == null)
                        throw new IOException(failure ?? "Forge 安装进程提前退出。");
                    return result;
                }
                catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                finally
                {
                    job.Terminate();
                    if (!child.HasExited) await Task.Run(() => child.WaitForExit(5000)).ConfigureAwait(false);
                }
            }
            finally
            {
                try { Directory.Delete(scratch, true); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { stage?.Report("临时安装文件未能清除：" + scratch); }
            }
        }

        internal static int Run()
        {
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            void Send(Message data)
            {
                lock (output) output.WriteLine(Prefix + new JavaScriptSerializer().Serialize(data));
            }
            try
            {
                using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
                string encoded = input.ReadLine();
                if (string.IsNullOrEmpty(encoded) || encoded.Length > 256 * 1024) throw new InvalidDataException("未收到有效的安装任务。");
                var request = new JavaScriptSerializer().Deserialize<InstallRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                if (request.Loader != "Forge" && request.Loader != "NeoForge") throw new InvalidDataException("加载器无效。");
                var config = new LauncherConfig { Root = request.Root, DownloadDirectory = request.Cache, JavaPath = request.Java,
                    NetworkMode = request.Network, Proxy = request.Proxy, DownloadThreads = request.Threads, DownloadRetries = request.Retries };
                DownloadPathService.EnsureGameStorageAvailable(config);
                string scratch = Path.GetFullPath(Environment.CurrentDirectory);
                if (!Guid.TryParseExact(Path.GetFileName(scratch), "N", out _) ||
                    !string.Equals(Path.GetDirectoryName(scratch), DownloadPathService.ForgeTempRoot(request.Cache), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(scratch, Path.GetTempPath().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Forge 临时目录未指向选定的下载位置。");
                using var stop = new CancellationTokenSource();
                _ = Task.Run(() =>
                {
                    try { if (input.ReadLine() == "stop") stop.Cancel(); }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
                });
                var timer = Stopwatch.StartNew();
                var counts = new WorkerProgress<DownloadBytes>(value =>
                {
                    lock (timer)
                    {
                        if (timer.ElapsedMilliseconds < 250) return;
                        timer.Restart();
                        Send(new Message { HasBytes = true, Completed = value.Completed, Total = value.Total });
                    }
                });
                var phases = new WorkerProgress<string>(value => Send(new Message { Stage = value }));
                var profile = Task.Run(() => new ModLoaderInstaller(config).InstallInProcessAsync(request.Game,
                    request.Loader, request.Build, phases, counts, stop.Token)).GetAwaiter().GetResult();
                Send(new Message { Profile = profile });
                return 0;
            }
            catch (Exception ex)
            {
                Send(new Message { Error = ex.Message });
                return 1;
            }
        }
    }
}
