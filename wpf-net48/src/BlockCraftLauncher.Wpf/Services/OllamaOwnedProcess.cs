using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlockCraftLauncher.Wpf.Services
{
    // A job closes the Ollama server and its inference runners even if the launcher crashes.
    internal sealed class OllamaOwnedProcess : IDisposable
    {
        readonly SafeFileHandle job;
        readonly Process process;
        internal Uri Address { get; }
        internal string Root { get; }
        internal bool IsRunning => !process.HasExited;

        OllamaOwnedProcess(SafeFileHandle job, Process process, Uri address, string root)
        {
            this.job = job;
            this.process = process;
            Address = address;
            Root = root;
        }

        internal static OllamaOwnedProcess Start(string executable, string root, string proxy)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            int port;
            try { listener.Start(); port = ((IPEndPoint)listener.LocalEndpoint).Port; }
            finally { listener.Stop(); }
            string host = "127.0.0.1:" + port;
            var start = new ProcessStartInfo(executable, "serve") {
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true
            };
            var variables = start.EnvironmentVariables;
            // Ollama reads its registry identity from the Windows user profile even in local mode.
            // Give this managed server its own identity, apart from an existing Ollama installation.
            foreach (string file in new[] { "id_ed25519", "id_ed25519.pub" })
                VerifiedModelDownload.RejectReparsePoints(Path.Combine(root, ".ollama", file));
            variables["USERPROFILE"] = root;
            variables["OLLAMA_HOST"] = host;
            variables["OLLAMA_MODELS"] = Path.Combine(root, "models");
            variables["OLLAMA_NO_CLOUD"] = "1";
            variables["OLLAMA_NOPRUNE"] = "1";
            variables.Remove("OLLAMA_ORIGINS");
            if (!string.IsNullOrWhiteSpace(proxy)) variables["HTTPS_PROXY"] = proxy;
            var job = CreateJobObject(IntPtr.Zero, null);
            Process child = null;
            try
            {
                if (job.IsInvalid) throw new Win32Exception();
                var limits = new ExtendedLimits();
                limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception();
                child = Process.Start(start);
                if (child == null || !AssignProcessToJobObject(job, child.Handle)) throw new Win32Exception();
                return new OllamaOwnedProcess(job, child, new Uri("http://" + host + "/"), root);
            }
            catch
            {
                if (child != null)
                {
                    try { if (!child.HasExited) child.Kill(); }
                    catch (InvalidOperationException) { }
                    finally { child.Dispose(); }
                }
                job.Dispose();
                throw;
            }
        }

        public void Dispose() { job.Dispose(); process.Dispose(); }

        [StructLayout(LayoutKind.Sequential)]
        struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
