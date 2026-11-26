using System;
using System.Net.NetworkInformation;
using System.Threading;
using Microsoft.Win32;

namespace Beam
{
    /// <summary>Ties the DLNA core to the player: HTTP server, SSDP announcements and network-change handling.</summary>
    public sealed class Receiver
    {
        public readonly Settings Cfg;
        public readonly PlayerWindow Player;
        public UpnpDevice Device { get; private set; }
        public int Port { get; private set; }

        private MiniHttp http;
        private SsdpServer ssdp;
        private Timer netTimer;
        private bool started;

        public Receiver(Settings cfg, PlayerWindow player)
        {
            Cfg = cfg;
            Player = player;
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
            SystemEvents.PowerModeChanged += OnPower;
            ssdp = new SsdpServer(Cfg.Uuid, Port);
            ssdp.Start();
        }

        public void Rename(string name)
        {
            Cfg.Name = name;
            Cfg.Save();
            if (Device != null) Device.SetName(name);
            RestartSsdp();
        }

        private void OnNetwork(object sender, EventArgs e) { Debounce(); }

        private void OnPower(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) Debounce();
        }

        private void Debounce()
        {
            if (!started) return;
            lock (this)
            {
                if (netTimer != null) netTimer.Dispose();
                netTimer = new Timer(delegate { RestartSsdp(); }, null, 3000, Timeout.Infinite);
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
            SystemEvents.PowerModeChanged -= OnPower;
            lock (this) { if (netTimer != null) { netTimer.Dispose(); netTimer = null; } }
            try { if (ssdp != null) ssdp.Shutdown(); } catch (Exception) { }
            try { if (http != null) http.Stop(); } catch (Exception) { }
            try { if (Device != null) Device.Shutdown(); } catch (Exception) { }
        }
    }
}
