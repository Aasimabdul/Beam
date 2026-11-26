using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Beam
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\Beam.Receiver.Single", out created))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Beam.Receiver.Show"))
            {
                if (!created)
                {
                    // Beam is already running: just bring its window forward.
                    showEvent.Set();
                    return 0;
                }

                bool trayOnly = Array.IndexOf(args, "--tray") >= 0;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    Settings.Log("ui error: " + e.Exception);
                    e.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Settings.Log("fatal: " + e.ExceptionObject);
                };

                Settings cfg = Settings.Load();
                var player = new PlayerWindow(cfg);
                var rx = new Receiver(cfg, player);
                var home = new HomeWindow(rx, player);
                var tray = new Tray(rx, player, home, app);

                var waiter = new Thread(delegate()
                {
                    try
                    {
                        while (showEvent.WaitOne())
                            app.Dispatcher.BeginInvoke(new Action(delegate { home.Open(false); }));
                    }
                    catch (Exception) { /* shutting down */ }
                }) { IsBackground = true, Name = "beam-show" };
                waiter.Start();

                Task.Run(delegate
                {
                    try
                    {
                        rx.Start();
                        tray.Ready();
                    }
                    catch (Exception ex)
                    {
                        tray.Error(ex.Message);
                    }
                });

                if (!trayOnly) home.Open(false);

                app.Run();
                rx.Stop();
                tray.Dispose();
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
