using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BlockCraftLauncher.Wpf.Services
{
    // Disposing this Windows job ends this process and its descendants.
    internal sealed class ChildProcessJob : IDisposable
    {
        readonly SafeFileHandle job;
        internal Process Process { get; }

        ChildProcessJob(SafeFileHandle job, Process process) { this.job = job; Process = process; }

        internal static ChildProcessJob Start(ProcessStartInfo start)
        {
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
                return new ChildProcessJob(job, child);
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

        public void Terminate() => job.Dispose();
        public void Dispose() { job.Dispose(); Process.Dispose(); }

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
