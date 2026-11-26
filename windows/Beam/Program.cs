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
            {
                if (!created)
                {
                    MessageBox.Show("Beam is already running. Look for its icon near the clock.", "Beam");
                    return 0;
                }

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
                var tray = new Tray(rx, player, app);

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

                app.Run();
                rx.Stop();
                tray.Dispose();
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
