using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using BlockCraftLauncher.Wpf.Models;

namespace BlockCraftLauncher.Wpf.Services
{
    /// <summary>
    /// Tracks only Process handles returned by launches initiated in this
    /// launcher session. Never scans other Java processes, reads their command
    /// lines or treats a version JSON as proof that Minecraft is running.
    /// </summary>
    public enum GameProcessStatus
    {
        NotTracked,
        Running,
        Exited,
        Unavailable
    }

    public sealed class GameProcessSnapshot
    {
        public GameProcessStatus Status { get; internal set; }
        public int ProcessId { get; internal set; }
        public int? ExitCode { get; internal set; }
        public DateTime StartedAtUtc { get; internal set; }
        public DateTime? ExitedAtUtc { get; internal set; }
    }

    public sealed class GameProcessTracker : IDisposable
    {
        sealed class Entry
        {
            public Process Process;
            public int ProcessId;
            public DateTime StartedAtUtc;
            public DateTime? ExitedAtUtc;
            public int? ExitCode;
            public GameProcessStatus Status;
        }

        readonly Dictionary<GameProfile, Entry> entries = new Dictionary<GameProfile, Entry>();
        bool disposed;

        public event Action<GameProfile, GameProcessSnapshot> ProcessExited;

        public void Track(GameProfile profile, Process process)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GameProcessTracker));
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (process == null) throw new ArgumentNullException(nameof(process));

            // Evaluate existing handle first. A second launch of the same
            // instance should not silently replace a still-running session.
            if (GetState(profile).Status == GameProcessStatus.Running)
                throw new InvalidOperationException("当前游戏实例仍在运行，请先返回游戏窗口。");

            int pid = process.Id; // Process.Start must already have succeeded.
            if (entries.TryGetValue(profile, out Entry previous))
                previous.Process?.Dispose();
            entries[profile] = new Entry
            {
                Process = process,
                ProcessId = pid,
                Status = GameProcessStatus.Running,
                StartedAtUtc = DateTime.UtcNow
            };
        }

        public GameProcessSnapshot GetState(GameProfile profile)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GameProcessTracker));
            if (profile == null || !entries.TryGetValue(profile, out Entry entry))
                return new GameProcessSnapshot { Status = GameProcessStatus.NotTracked };

            if (entry.Process != null)
            {
                try
                {
                    if (entry.Process.HasExited)
                    {
                        entry.ExitCode = entry.Process.ExitCode;
                        entry.ExitedAtUtc = DateTime.UtcNow;
                        entry.Status = GameProcessStatus.Exited;
                    }
                    else
                    {
                        return ToSnapshot(entry, GameProcessStatus.Running);
                    }
                }
                catch (InvalidOperationException)
                {
                    // An unreadable process handle is not proof of exit.
                    entry.Status = GameProcessStatus.Unavailable;
                }
                catch (Win32Exception)
                {
                    entry.Status = GameProcessStatus.Unavailable;
                }

                entry.Process.Dispose();
                entry.Process = null;
                if (entry.Status == GameProcessStatus.Exited)
                    ProcessExited?.Invoke(profile, ToSnapshot(entry, entry.Status));
            }

            return ToSnapshot(entry, entry.Status);
        }

        public void Refresh()
        {
            if (disposed) return;
            foreach (var profile in new List<GameProfile>(entries.Keys))
                GetState(profile);
        }

        static GameProcessSnapshot ToSnapshot(Entry entry, GameProcessStatus status)
        {
            return new GameProcessSnapshot
            {
                Status = status,
                ProcessId = entry.ProcessId,
                StartedAtUtc = entry.StartedAtUtc,
                ExitedAtUtc = entry.ExitedAtUtc,
                ExitCode = entry.ExitCode
            };
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in entries.Values)
                entry.Process?.Dispose(); // Never kill a running game on launcher exit.
            entries.Clear();
        }
    }
}
