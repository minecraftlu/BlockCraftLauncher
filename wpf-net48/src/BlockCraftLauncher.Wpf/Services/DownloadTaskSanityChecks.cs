using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BlockCraftLauncher.Wpf.Services
{
    // Deterministic real-job checks: no downloads, accounts or player files.
    internal static class DownloadTaskSanityChecks
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Download task check: " + message);
        }

        static void Finish(Task task)
        {
            Require(task.Wait(5000), "job did not finish");
            task.GetAwaiter().GetResult();
        }

        public static void Run()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                VerifyCompletion();
                VerifyQueuedCancellation();
                VerifyReentrantRetry();
                VerifyDelayedProgress();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        static void VerifyCompletion()
        {
            using (var slots = new SemaphoreSlim(1, 1))
            {
                foreach (bool knownSize in new[] { false, true })
                {
                    var changes = new HashSet<string>();
                    var job = new DownloadJob("test", "completion", (token, stage, bytes) =>
                    {
                        if (knownSize) bytes.Report(new DownloadBytes(25, 100));
                        stage.Report("installed successfully");
                        return Task.CompletedTask;
                    });
                    job.PropertyChanged += (sender, args) => changes.Add(args.PropertyName);
                    Finish(job.RunAsync(slots));
                    Require(job.Status == "已完成" && !job.IsRunning && !job.CanRetry &&
                        !job.IsIndeterminate && job.Percent == 100 && job.Speed == "",
                        "completed jobs must stop animating, including unknown-size work");
                    Require(job.Detail == "installed successfully", "last install message was lost");
                    Require(changes.Contains(nameof(DownloadJob.IsRunning)) &&
                        changes.Contains(nameof(DownloadJob.CanRetry)) &&
                        changes.Contains(nameof(DownloadJob.PercentLabel)),
                        "dependent UI properties must notify bindings");
                }
                Require(slots.CurrentCount == 1, "completion leaked a queue slot");
            }
        }

        static void VerifyQueuedCancellation()
        {
            using (var slots = new SemaphoreSlim(0, 1))
            {
                int calls = 0;
                var job = new DownloadJob("test", "queued cancel", (token, stage, bytes) =>
                {
                    calls++;
                    return Task.CompletedTask;
                });
                var run = job.RunAsync(slots);
                Require(job.IsRunning, "queued job should be cancellable");
                job.Cancel();
                Finish(run);
                Require(job.Status == "已取消" && job.CanRetry && !job.IsRunning &&
                    !job.IsIndeterminate && calls == 0 && slots.CurrentCount == 0,
                    "queued cancellation must not run work or release an unowned slot");
                slots.Release();
                Finish(job.RunAsync(slots));
                job.Cancel();
                Require(calls == 1 && job.Status == "已完成" && slots.CurrentCount == 1,
                    "cancelled queued work should retry exactly once");
            }
        }

        static void VerifyReentrantRetry()
        {
            using (var slots = new SemaphoreSlim(1, 1))
            {
                int calls = 0;
                var job = new DownloadJob("test", "retry", (token, stage, bytes) =>
                {
                    if (++calls == 1) throw new InvalidOperationException("expected failure");
                    return Task.CompletedTask;
                });
                Finish(job.RunAsync(slots));
                Require(job.CanRetry && job.Error == "expected failure" && !job.IsIndeterminate,
                    "failed work must expose retry and a stationary progress bar");
                Task duplicate = null;
                bool reentered = false;
                job.PropertyChanged += (sender, args) =>
                {
                    if (!reentered && args.PropertyName == nameof(DownloadJob.Percent) && job.CanRetry)
                    {
                        reentered = true;
                        duplicate = job.RunAsync(slots);
                    }
                };
                Finish(job.RunAsync(slots));
                Require(duplicate != null && duplicate.IsCompleted && calls == 2 &&
                    job.Status == "已完成" && job.Error == "" && slots.CurrentCount == 1,
                    "a retry notification must not start a duplicate attempt");
            }
        }

        static void VerifyDelayedProgress()
        {
            var context = new QueuedContext();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                using (var slots = new SemaphoreSlim(1, 1))
                {
                    int calls = 0;
                    var firstDone = new TaskCompletionSource<bool>();
                    var retryDone = new TaskCompletionSource<bool>();
                    IProgress<string> oldStage = null;
                    IProgress<DownloadBytes> oldBytes = null;
                    var job = new DownloadJob("test", "late callbacks", (token, stage, bytes) =>
                    {
                        if (++calls == 1)
                        {
                            oldStage = stage;
                            oldBytes = bytes;
                            return firstDone.Task;
                        }
                        stage.Report("new attempt");
                        bytes.Report(new DownloadBytes(20, 200));
                        return retryDone.Task;
                    });
                    var first = job.RunAsync(slots);
                    SynchronizationContext.SetSynchronizationContext(null);
                    oldStage.Report("stale attempt");
                    oldBytes.Report(new DownloadBytes(99, 100));
                    SynchronizationContext.SetSynchronizationContext(context);
                    var delayed = context.TakePending();
                    Require(delayed.Count == 2, "background progress must marshal to the captured context");
                    job.Cancel();
                    firstDone.SetException(new System.IO.IOException("cancelled transport"));
                    context.Drain();
                    Require(first.IsCompleted && job.Status == "已取消" && job.Error == "",
                        "transport failure after cancel should remain cancelled");

                    var retry = job.RunAsync(slots);
                    foreach (var callback in delayed) callback();
                    Require(job.Detail == "new attempt" && job.BytesDownloaded == 20 &&
                        job.TotalBytes == 200 && job.Percent == 10,
                        "old queued progress overwrote a retry");

                    retryDone.SetResult(true);
                    context.Drain();
                    oldStage.Report("late completion");
                    oldBytes.Report(new DownloadBytes(1, 100));
                    Require(retry.IsCompleted && job.Status == "已完成" && job.Percent == 100 &&
                        job.Detail == "new attempt" && !job.IsIndeterminate && slots.CurrentCount == 1,
                        "late callbacks changed completed job state");
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        sealed class QueuedContext : SynchronizationContext
        {
            readonly Queue<Action> pending = new Queue<Action>();
            public override void Post(SendOrPostCallback callback, object state)
            {
                lock (pending) pending.Enqueue(() => callback(state));
            }
            public List<Action> TakePending()
            {
                lock (pending)
                {
                    var result = pending.ToList();
                    pending.Clear();
                    return result;
                }
            }
            public void Drain()
            {
                foreach (var callback in TakePending()) callback();
            }
        }
    }
}
