using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Beam
{
    public static class Program
    {
        private const int DefaultUiPort = 49200;

        public static int Main(string[] args)
        {
            bool noOpen = Array.IndexOf(args, "--no-open") >= 0;
            if (Array.IndexOf(args, "--version") >= 0) { Console.WriteLine("Beam " + App.Version); return 0; }
            if (Array.IndexOf(args, "--help") >= 0 || Array.IndexOf(args, "-h") >= 0)
            {
                Console.WriteLine("Beam " + App.Version + " - DLNA cast receiver\nusage: beam [--no-open]\n  --no-open   do not open the settings page in the browser");
                return 0;
            }

            Settings cfg = Settings.Load();
            var app = new App(cfg);
            var ui = new MiniHttp(new WebUi(app));
            try
            {
                app.UiPort = ui.Start(DefaultUiPort, true);
            }
            catch (SocketException)
            {
                if (WebUi.IsBeamRunning(DefaultUiPort))
                {
                    // already running: just show its page
                    if (!noOpen) Open("http://127.0.0.1:" + DefaultUiPort + "/");
                    Console.WriteLine("Beam is already running: http://127.0.0.1:" + DefaultUiPort + "/");
                    return 0;
                }
                app.UiPort = ui.Start(0, true);
            }

            string url = "http://127.0.0.1:" + app.UiPort + "/";
            Task.Run(() =>
            {
                try { app.Start(); }
                catch (Exception ex)
                {
                    app.Error = ex.Message;
                    Settings.Log("start failed: " + ex);
                }
            });
            Console.WriteLine("Beam " + App.Version + " is running. Settings: " + url);
            if (!app.Player.Available) Console.WriteLine("mpv not found. Install it to play: sudo apt install mpv");
            if (!noOpen) Open(url);

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; app.Quit.Set(); };
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; app.Quit.Set(); });
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; app.Quit.Set(); });
            app.Quit.WaitOne();

            app.Stop();
            ui.Stop();
            return 0;
        }

        private static void Open(string url)
        {
            try
            {
                var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                psi.ArgumentList.Add(url);
                Process.Start(psi);
            }
            catch (Exception) { /* no browser launcher: the URL is printed above */ }
        }
    }
}
