using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class OllamaPortablePackageChecks
    {
        static void Check(bool valid, string context)
        {
            if (!valid) throw new InvalidDataException("Ollama 安装自检失败：" + context);
        }

        static void WriteZip(string path, params string[] names)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var name in names)
                    using (var text = new StreamWriter(zip.CreateEntry(name).Open()))
                        text.Write("test-fixture:" + name);
        }

        internal static async Task RunAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "BlockCraft-ollama-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int fetched = 0;
            async Task Download(string path, CancellationToken ct)
            {
                Interlocked.Increment(ref fetched);
                acquired.TrySetResult(true);
                await proceed.Task;
                ct.ThrowIfCancellationRequested();
                WriteZip(path, "ollama.exe", "lib/ollama/llama-server.exe");
            }

            try
            {
                File.WriteAllText(Path.Combine(root, "leave-me.txt"), "user");
                var first = OllamaPortablePackage.InstallAsync(root, null, CancellationToken.None, Download);
                await acquired.Task;
                var second = OllamaPortablePackage.InstallAsync(root, null, CancellationToken.None, Download);
                using (var cancel = new CancellationTokenSource())
                {
                    var waiting = OllamaPortablePackage.InstallAsync(root, null, cancel.Token, Download);
                    cancel.Cancel();
                    try { await waiting; throw new InvalidDataException("锁等待未取消。"); }
                    catch (OperationCanceledException) { }
                }
                proceed.SetResult(true);
                var paths = await Task.WhenAll(first, second);
                Check(fetched == 1 && paths[0] == paths[1] && OllamaPortablePackage.IsInstalled(root), "并发安装未复用文件");
                string generation = Path.GetDirectoryName(paths[0]);
                Check(File.Exists(Path.Combine(generation, "lib", "ollama", "llama-server.exe")), "压缩包子目录缺失");

                File.WriteAllText(Path.Combine(generation, "lib", "ollama", "llama-server.exe"), "corrupt");
                try
                {
                    await OllamaPortablePackage.InstallAsync(root, null, CancellationToken.None, Download, false);
                    throw new InvalidDataException("离线启动使用了损坏的文件。");
                }
                catch (InvalidOperationException) { }
                Check(fetched == 1, "普通聊天意外发起下载");
                string repaired = await OllamaPortablePackage.InstallAsync(root, null, CancellationToken.None, Download);
                Check(repaired != paths[0] && fetched == 2, "修复没有重建完整组件");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(repaired), "extra.dll"), "unexpected");
                string refreshed = await OllamaPortablePackage.InstallAsync(root, null, CancellationToken.None, Download);
                Check(refreshed != repaired && fetched == 3, "重用了含未知程序文件的组件");
                Check(File.ReadAllText(Path.Combine(root, "leave-me.txt")) == "user", "改动了用户的其他文件");

                foreach (var invalid in new[] { "../escaped.dll", "lib/CON.txt", "C:/outside.dll", "lib\\..\\escape.dll", "/outside" })
                {
                    string isolated = Path.Combine(root, "rejected-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await OllamaPortablePackage.InstallAsync(isolated, null, CancellationToken.None, (path, token) =>
                        {
                            WriteZip(path, "ollama.exe", invalid); return Task.CompletedTask;
                        });
                        throw new Exception("非法运行组件条目被接受。");
                    }
                    catch (InvalidDataException) { }
                    Check(!OllamaPortablePackage.IsInstalled(isolated) &&
                        !Directory.EnumerateFiles(isolated, "ollama.exe", SearchOption.AllDirectories).Any(), "失败后残留可执行程序");
                }
            }
            finally
            {
                proceed.TrySetResult(true);
                Directory.Delete(root, true);
            }
        }
    }
}
