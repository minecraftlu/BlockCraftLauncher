using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace BlockCraftLauncher.Wpf.Services
{
    public sealed class DownloadJob : INotifyPropertyChanged
    {
        readonly Func<CancellationToken, IProgress<string>, IProgress<DownloadBytes>, Task> work;
        readonly object gate = new object();
        CancellationTokenSource cancellation;
        string status = "等待中", detail = "", rate = "", error = "";
        double percent;
        long transferred, total;
        bool indeterminate = true;
        DateTime lastSample = DateTime.UtcNow;
        long lastBytes;

        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; }
        public string Kind { get; }
        public string Status { get => status; private set { status = value; Notify(nameof(Status)); Notify(nameof(CanRetry)); } }
        public string Detail { get => detail; private set { detail = value; Notify(nameof(Detail)); } }
        public string Speed { get => rate; private set { rate = value; Notify(nameof(Speed)); } }
        public string Error { get => error; private set { error = value; Notify(nameof(Error)); } }
        public double Percent { get => percent; private set { percent = value; Notify(nameof(Percent)); Notify(nameof(PercentLabel)); } }
        public long BytesDownloaded { get => transferred; private set { transferred = value; Notify(nameof(BytesDownloaded)); Notify(nameof(BytesLabel)); } }
        public long TotalBytes { get => total; private set { total = value; Notify(nameof(TotalBytes)); Notify(nameof(BytesLabel)); } }
        public bool IsIndeterminate { get => indeterminate; private set { indeterminate = value; Notify(nameof(IsIndeterminate)); } }
        public string BytesLabel => TotalBytes > 0 ? Format(BytesDownloaded) + " / " + Format(TotalBytes) : (BytesDownloaded > 0 ? Format(BytesDownloaded) : "等待进度");
        public string PercentLabel => IsIndeterminate ? "正在处理" : Percent.ToString("F1") + "%";
        public bool CanRetry => Status == "失败" || Status == "已取消";
        public bool IsRunning => Status == "排队中" || Status == "下载中";
        public event PropertyChangedEventHandler PropertyChanged;
        void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public DownloadJob(string kind, string title,
            Func<CancellationToken, IProgress<string>, IProgress<DownloadBytes>, Task> work)
        {
            Kind = kind; Title = title; this.work = work ?? throw new ArgumentNullException(nameof(work));
        }

        public static string Format(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KiB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MiB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GiB";
        }

        public void Cancel()
        {
            lock (gate) cancellation?.Cancel();
        }

        internal async Task RunAsync(SemaphoreSlim slots)
        {
            CancellationTokenSource source;
            lock (gate)
            {
                if (IsRunning) throw new InvalidOperationException("任务已经开始");
                cancellation = new CancellationTokenSource();
                source = cancellation;
            }
            Reset();
            Status = "排队中";
            bool entered = false;
            try
            {
                await slots.WaitAsync(source.Token);
                entered = true;
                source.Token.ThrowIfCancellationRequested();
                Status = "下载中";
                var stage = new Progress<string>(value => Detail = value ?? "");
                var bytes = new Progress<DownloadBytes>(UpdateBytes);
                await work(source.Token, stage, bytes);
                source.Token.ThrowIfCancellationRequested();
                Status = "已完成";
                if (!IsIndeterminate) Percent = 100;
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested)
            {
                Status = "已取消";
            }
            catch (Exception ex)
            {
                Error = ex.GetBaseException().Message;
                Detail = "失败：" + Error;
                Status = "失败";
            }
            finally
            {
                if (entered) slots.Release();
                lock (gate)
                {
                    if (ReferenceEquals(cancellation, source)) cancellation = null;
                }
                source.Dispose();
            }
        }

        void Reset()
        {
            Percent = 0; BytesDownloaded = 0; TotalBytes = 0;
            IsIndeterminate = true; Speed = ""; Detail = ""; Error = "";
            lastSample = DateTime.UtcNow; lastBytes = 0;
        }

        void UpdateBytes(DownloadBytes info)
        {
            long done = Math.Max(0, info.Completed);
            long size = Math.Max(0, info.Total);
            BytesDownloaded = done;
            TotalBytes = size;
            IsIndeterminate = size == 0;
            if (size > 0) Percent = Math.Max(0, Math.Min(100, 100.0 * done / size));
            var elapsed = (DateTime.UtcNow - lastSample).TotalSeconds;
            if (elapsed >= 0.5)
            {
                // CmlLib may switch to a new file; avoid reporting negative throughput.
                var delta = Math.Max(0, done - lastBytes);
                Speed = Format((long)(delta / elapsed)) + "/s";
                lastSample = DateTime.UtcNow; lastBytes = done;
            }
        }
    }

    public sealed class DownloadBytes
    {
        public long Completed { get; }
        public long Total { get; }
        public DownloadBytes(long completed, long total) { Completed = completed; Total = total; }
    }

    /// <summary>Bounded in-process task queue. Failed/cancelled jobs can be retried deliberately.</summary>
    public sealed class DownloadTaskCenter
    {
        readonly SemaphoreSlim slots;
        public ObservableCollection<DownloadJob> Jobs { get; } = new ObservableCollection<DownloadJob>();
        public DownloadTaskCenter(int parallelTasks = 3)
        {
            slots = new SemaphoreSlim(Math.Max(1, Math.Min(6, parallelTasks)));
        }
        public DownloadJob Enqueue(string kind, string name,
            Func<CancellationToken, IProgress<string>, IProgress<DownloadBytes>, Task> work)
        {
            if (!Application.Current.Dispatcher.CheckAccess())
                throw new InvalidOperationException("请在 UI 线程创建下载任务");
            var job = new DownloadJob(kind, name, work);
            Jobs.Insert(0, job);
            // Limit memory usage for long sessions; keep active tasks.
            foreach (var old in Jobs.Skip(100).Where(x => !x.IsRunning).ToList())
                Jobs.Remove(old);
            _ = job.RunAsync(slots);
            return job;
        }
        public void Retry(DownloadJob job)
        {
            if (job == null || !job.CanRetry) return;
            _ = job.RunAsync(slots);
        }
        public void Cancel(DownloadJob job) => job?.Cancel();
    }
}