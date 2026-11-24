using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    internal static class CurseForgeAppDownload
    {
        // The "Download standalone" Windows button on https://www.curseforge.com/download/app.
        const string InstallerUrl = "https://download.overwolf.com/install/Download?ExtensionId=cfiahnpaolfnlgaihhmobmnjdafknjnjdpdabpcm&Name=CurseForge";
        internal const string SuggestedName = "CurseForge-Standalone-Installer.exe";
        const long MaxBytes = 512L * 1024 * 1024;

        internal static bool TrustedEndpoint(Uri url) =>
            url != null && url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort &&
            url.UserInfo.Length == 0 && url.Fragment.Length == 0 &&
            (url.IdnHost.Equals("overwolf.com", StringComparison.OrdinalIgnoreCase) ||
             url.IdnHost.EndsWith(".overwolf.com", StringComparison.OrdinalIgnoreCase));

        internal static async Task SaveAsync(LauncherConfig settings, string destination, bool overwriteApproved,
            IProgress<string> stage, IProgress<DownloadBytes> progress, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择 .exe 安装程序的保存位置。");
            var original = new FileInfo(path);
            bool replace = original.Exists;
            if (replace && (!overwriteApproved || (original.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException("目标位置已有文件，请重新选择保存位置。");
            long originalSize = replace ? original.Length : 0;
            DateTime originalWritten = replace ? original.LastWriteTimeUtc : DateTime.MinValue;
            string temporary = Path.Combine(Path.GetDirectoryName(path),
                ".blockcraft-curseforge-" + Guid.NewGuid().ToString("N") + ".exe");
            bool created = false;
            try
            {
                using (var client = new HttpClient(NetworkDownloader.CreateHttpHandler(settings, false)) { Timeout = Timeout.InfiniteTimeSpan })
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("BlockCraftLauncher/1.0");
                    var url = new Uri(InstallerUrl);
                    for (int hop = 0; ; hop++)
                    {
                        if (!TrustedEndpoint(url) || hop > 5)
                            throw new InvalidDataException("官方安装包重定向到未经确认的地址：" + url.Host);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        stage?.Report("正在从 " + url.Host + " 下载安装程序…");
                        try
                        {
                            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                            {
                                int status = (int)response.StatusCode;
                                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                                {
                                    if (response.Headers.Location == null) throw new InvalidDataException("官方下载重定向缺少目标地址。");
                                    url = new Uri(url, response.Headers.Location);
                                    continue;
                                }
                                response.EnsureSuccessStatusCode();
                                if (response.StatusCode != HttpStatusCode.OK)
                                    throw new InvalidDataException("官方服务未返回完整的安装程序。");
                                long length = response.Content.Headers.ContentLength ?? 0;
                                if (length > MaxBytes) throw new InvalidDataException("安装程序超过允许的大小。");
                                using (timeout.Token.Register(response.Dispose))
                                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                                {
                                    created = true;
                                    await CopyAsync(source, file, length, progress, timeout).ConfigureAwait(false);
                                }
                                break;
                            }
                        }
                        catch (Exception) when (timeout.IsCancellationRequested)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            throw new TimeoutException("CurseForge 官方下载连接超时，可在下载任务中重试。");
                        }
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                stage?.Report("正在验证 Overwolf 的 Windows 数字签名…");
                await Task.Run(() => VerifyPublisher(temporary), cancellation).ConfigureAwait(false);
                MarkAsInternetDownload(temporary);
                cancellation.ThrowIfCancellationRequested();
                original.Refresh();
                if (replace && original.Exists && (original.Length != originalSize || original.LastWriteTimeUtc != originalWritten ||
                    (original.Attributes & FileAttributes.ReparsePoint) != 0))
                    throw new IOException("下载期间目标文件已改变，请另选保存位置。");
                // Same-directory rename is atomic. Without explicit overwrite approval a
                // file created by someone else during the transfer cannot be replaced.
                if (!MoveFileExW(temporary, path, replace ? 1u | 8u : 8u))
                    throw new IOException("无法保存安装程序：" + path, new Win32Exception(Marshal.GetLastWin32Error()));
                created = false;
            }
            finally
            {
                if (created)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        static async Task CopyAsync(Stream source, Stream target, long length, IProgress<DownloadBytes> progress,
            CancellationTokenSource timeout)
        {
            var buffer = new byte[81920];
            long total = 0;
            var clock = Stopwatch.StartNew();
            while (true)
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(40));
                int count = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
                if (total > MaxBytes || (length > 0 && total > length))
                    throw new InvalidDataException("安装程序的下载大小不正确。");
                await target.WriteAsync(buffer, 0, count, timeout.Token).ConfigureAwait(false);
                if (clock.ElapsedMilliseconds >= 150)
                {
                    progress?.Report(new DownloadBytes(total, length));
                    clock.Restart();
                }
            }
            if (total == 0 || (length > 0 && total != length)) throw new IOException("安装程序下载不完整。");
            progress?.Report(new DownloadBytes(total, length));
        }

        static void MarkAsInternetDownload(string file)
        {
            // Preserve Windows' Internet zone on filesystems which support named streams.
            // File.WriteAllText rejects alternate-stream paths on .NET Framework.
            using (var handle = CreateFileW(file + ":Zone.Identifier", 0x40000000, 0,
                IntPtr.Zero, 2, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid) return;
                using (var stream = new FileStream(handle, FileAccess.Write))
                using (var writer = new StreamWriter(stream))
                    writer.Write("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=" + InstallerUrl + "\r\n");
            }
        }

        // Verify the Authenticode signature against Windows policy, then inspect the
        // very signer chosen by WinVerifyTrust (an untrusted embedded name is insufficient).
        internal static void VerifyPublisher(string path)
        {
            Guid action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            IntPtr name = Marshal.StringToCoTaskMemUni(path);
            var file = new TrustFile { Size = (uint)Marshal.SizeOf(typeof(TrustFile)), Path = name };
            IntPtr info = Marshal.AllocHGlobal((int)file.Size);
            var data = new TrustData { Size = (uint)Marshal.SizeOf(typeof(TrustData)), UI = 2, Choice = 1,
                File = info, StateAction = 1 };
            try
            {
                Marshal.StructureToPtr(file, info, false);
                int result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                if (result != 0) throw new InvalidDataException("Windows 无法验证安装包的数字签名 (0x" + result.ToString("X8") + ")。");
                IntPtr provider = WTHelperProvDataFromStateData(data.State);
                IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
                IntPtr cert = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
                IntPtr context = cert == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(cert, IntPtr.Size);
                if (context == IntPtr.Zero) throw new InvalidDataException("安装包缺少已验证的签名证书。");
                using (var identity = new X509Certificate2(context))
                {
                    var publisher = identity.GetNameInfo(X509NameType.SimpleName, false).Trim().TrimEnd('.');
                    if (!publisher.Equals("Overwolf Ltd", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("安装包签名发行者与 Overwolf 不匹配：" + publisher);
                }
            }
            finally
            {
                if (data.State != IntPtr.Zero)
                {
                    data.StateAction = 2;
                    WinVerifyTrust(new IntPtr(-1), ref action, ref data);
                }
                Marshal.FreeHGlobal(info);
                Marshal.FreeCoTaskMem(name);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustFile { public uint Size; public IntPtr Path, Handle, Subject; }
        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public uint Size;
            public IntPtr Policy, Client;
            public uint UI, Revocation, Choice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr State, Url;
            public uint Flags, Context;
            public IntPtr SignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, bool counterSigner, uint counterIndex);
        [DllImport("wintrust.dll", ExactSpelling = true)]
        static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileExW(string oldName, string newName, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
    }
}
