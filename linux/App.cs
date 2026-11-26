using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace Beam
{
    /// <summary>Wires the DLNA core to mpv: HTTP server, SSDP announcements, network-change handling.</summary>
    public sealed class App
    {
        public const string Version = "0.1.0";
        public readonly Settings Cfg;
        public readonly MpvPlayer Player;
        public UpnpDevice Device { get; private set; }
        public int Port { get; private set; }
        public int UiPort;
        public volatile bool Ready;
        public volatile string Error = "";
        public readonly ManualResetEvent Quit = new ManualResetEvent(false);

        private MiniHttp http;
        private SsdpServer ssdp;
        private Timer netTimer;
        private bool started;

        public App(Settings cfg)
        {
            Cfg = cfg;
            Player = new MpvPlayer(cfg);
        }

        public void Start()
        {
            Device = new UpnpDevice(Cfg.Uuid, Cfg.Name, Player);
            Player.Device = Device;
            http = new MiniHttp(Device);
            try { Port = http.Start(Cfg.Port); }
            catch (Exception) { Port = http.Start(0); }
            if (Port != Cfg.Port) { Cfg.Port = Port; Cfg.Save(); }
            started = true;
            NetworkChange.NetworkAddressChanged += OnNetwork;
            ssdp = new SsdpServer(Cfg.Uuid, Port);
            ssdp.Start();
            Ready = true;
        }

        public void Rename(string name)
        {
            Cfg.Name = name;
            Cfg.Save();
            if (Device != null) Device.SetName(name);
            Task.Run(new Action(RestartSsdp));
        }

        private void OnNetwork(object sender, EventArgs e)
        {
            if (!started) return;
            lock (this)
            {
                if (netTimer != null) netTimer.Dispose();
                netTimer = new Timer(_ => RestartSsdp(), null, 3000, Timeout.Infinite);
            }
        }

        private void RestartSsdp()
        {
            try { if (ssdp != null) ssdp.Restart(); }
            catch (Exception ex) { Settings.Log("SSDP restart failed: " + ex.Message); }
        }

        public void Stop()
        {
            started = false;
            NetworkChange.NetworkAddressChanged -= OnNetwork;
            lock (this) { if (netTimer != null) { netTimer.Dispose(); netTimer = null; } }
            try { if (ssdp != null) ssdp.Shutdown(); } catch (Exception) { }
            try { if (http != null) http.Stop(); } catch (Exception) { }
            try { Player.Shutdown(); } catch (Exception) { }
            try { if (Device != null) Device.Shutdown(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ autostart (freedesktop)

        private static string AutostartFile
        {
            get
            {
                string cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
                if (string.IsNullOrEmpty(cfg)) cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return Path.Combine(cfg, "autostart", "beam.desktop");
            }
        }

        public static bool AutostartOn() { return File.Exists(AutostartFile); }

        public static void SetAutostart(bool on)
        {
            try
            {
                if (on)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile));
                    string exe = Environment.ProcessPath ?? "beam";
                    File.WriteAllText(AutostartFile, "[Desktop Entry]\nType=Application\nName=Beam\nComment=DLNA cast receiver\nExec=\"" + exe + "\" --no-open\nTerminal=false\nX-GNOME-Autostart-enabled=true\n");
                }
                else if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
            }
            catch (Exception ex) { Settings.Log("autostart toggle failed: " + ex.Message); }
        }
    }
}
