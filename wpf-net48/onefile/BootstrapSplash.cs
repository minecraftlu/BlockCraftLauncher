using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using BlockCraftLauncher.Wpf;

namespace BlockCraftLauncher.OneFile
{
    // Own the only startup window from embedded-payload validation until the
    // extracted main window renders. Update and automated modes do not enter.
    internal sealed class BootstrapSplash : IDisposable
    {
        readonly EventWaitHandle ready;
        volatile Dispatcher dispatcher;
        volatile bool stopped;
        internal string Token { get; }

        BootstrapSplash()
        {
            Token = Guid.NewGuid().ToString("N");
            ready = new EventWaitHandle(false, EventResetMode.ManualReset, StartupLoadingWindow.EventPrefix + Token);
            try
            {
                var thread = new Thread(Show) { IsBackground = true, Name = "BlockCraft startup window" };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }
            catch { ready.Dispose(); throw; }
        }

        internal static BootstrapSplash TryStart()
        {
            try { return new BootstrapSplash(); }
            catch (Exception ex) { Trace.WriteLine(ex); return null; }
        }

        void Show()
        {
            try
            {
                var current = Dispatcher.CurrentDispatcher;
                dispatcher = current;
                if (stopped) return;
                var window = new StartupLoadingWindow();
                window.Closed += (s, e) => current.BeginInvokeShutdown(DispatcherPriority.Send);
                window.Show();
                if (!stopped) Dispatcher.Run();
                window.Close();
            }
            catch (Exception ex) { Trace.WriteLine(ex); }
        }

        internal void WaitUntilRendered(Process child)
        {
            var watch = Stopwatch.StartNew();
            while (!child.HasExited && watch.Elapsed < TimeSpan.FromSeconds(60))
                if (ready.WaitOne(150)) return;
        }

        internal void Hide()
        {
            stopped = true;
            var current = dispatcher;
            if (current != null && !current.HasShutdownStarted)
                current.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        public void Dispose()
        {
            Hide();
            ready.Dispose();
        }
    }
}
