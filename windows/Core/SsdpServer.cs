using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Beam
{
    /// <summary>Announces the renderer on the LAN (SSDP) and answers M-SEARCH. Call Restart() when the network changes.</summary>
    public sealed class SsdpServer
    {
        private readonly string uuid;
        private readonly int httpPort;
        private readonly Random rnd = new Random();
        private readonly object sync = new object();
        private Socket socket;
        private volatile bool running;
        private Timer refresh;
        private bool disposed;

        public SsdpServer(string uuid, int httpPort)
        {
            this.uuid = uuid;
            this.httpPort = httpPort;
        }

        public void Start()
        {
            lock (sync)
            {
                if (running || disposed) return;
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    try { s.IOControl(-1744830452, new byte[] { 0 }, null); } catch (Exception) { /* Windows only: ignore ICMP resets */ }
                    s.Bind(new IPEndPoint(IPAddress.Any, SsdpMsg.Port));
                    s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                    IPAddress group = IPAddress.Parse(SsdpMsg.Group);
                    bool joined = false;
                    foreach (LanAddr a in Lan.Addresses())
                    {
                        try
                        {
                            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, a.Ip));
                            joined = true;
                        }
                        catch (SocketException)
                        {
                            // this adapter can't do multicast; try the others
                        }
                    }
                    if (!joined) s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group));
                }
                catch (Exception)
                {
                    s.Close();
                    throw;
                }
                socket = s;
                running = true;
                var t = new Thread(() => ReadLoop(s)) { IsBackground = true, Name = "beam-ssdp" };
                t.Start();
                // announce three times (UDP can be lost), then refresh well before max-age runs out
                for (int i = 0; i < 3; i++) Later(i * 400, () => Announce(true));
                refresh = new Timer(_ => Announce(true), null, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
            }
        }

        /// <summary>Leaves the network politely (byebye) and closes the socket.</summary>
        public void Stop()
        {
            lock (sync)
            {
                if (!running) return;
                Announce(false);
                Close();
            }
        }

        public void Restart()
        {
            lock (sync)
            {
                if (running) Close();
                Start();
            }
        }

        public void Shutdown()
        {
            Stop();
            lock (sync) { disposed = true; }
        }

        private void Close()
        {
            running = false;
            if (refresh != null) { refresh.Dispose(); refresh = null; }
            if (socket != null) { try { socket.Close(); } catch (Exception) { } socket = null; }
        }

        private static void Later(int ms, Action a)
        {
            if (ms <= 0) { ThreadPool.QueueUserWorkItem(_ => a()); return; }
            Timer t = null;
            t = new Timer(_ => { try { a(); } finally { t.Dispose(); } }, null, ms, Timeout.Infinite);
        }

        private void ReadLoop(Socket s)
        {
            var buf = new byte[2048];
            while (running)
            {
                try
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(buf, ref from);
                    var peer = (IPEndPoint)from;
                    string text = Encoding.UTF8.GetString(buf, 0, n);
                    SsdpMsg.Search q = SsdpMsg.ParseSearch(text);
                    if (q == null) continue;
                    List<string> answers = SsdpMsg.Answers(uuid, q.St);
                    if (answers.Count == 0) continue;
                    string ip = Lan.LocalIpFor(peer.Address);
                    if (ip == null) continue;
                    string location = "http://" + ip + ":" + httpPort + "/desc.xml";
                    int delay = rnd.Next(Math.Max(1, Math.Min(q.Mx, 2)) * 1000);
                    foreach (string nt in answers)
                    {
                        string msg = SsdpMsg.SearchResponse(uuid, nt, location);
                        Later(delay, () => Send(msg, peer, null));
                    }
                }
                catch (ObjectDisposedException) { return; }
                catch (Exception)
                {
                    if (!running) return;
                    Thread.Sleep(50);
                }
            }
        }

        private void Announce(bool alive)
        {
            var group = new IPEndPoint(IPAddress.Parse(SsdpMsg.Group), SsdpMsg.Port);
            foreach (LanAddr a in Lan.Addresses())
            {
                string location = "http://" + a.Ip + ":" + httpPort + "/desc.xml";
                foreach (string nt in SsdpMsg.Targets(uuid))
                    Send(alive ? SsdpMsg.Alive(uuid, nt, location) : SsdpMsg.ByeBye(uuid, nt), group, a.Ip);
            }
        }

        private void Send(string msg, IPEndPoint to, IPAddress via)
        {
            lock (sync)
            {
                Socket s = socket;
                if (s == null) return;
                try
                {
                    if (via != null) s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, via.GetAddressBytes());
                    byte[] b = Encoding.UTF8.GetBytes(msg);
                    s.SendTo(b, to);
                }
                catch (Exception)
                {
                    // network went away; the next Restart() fixes it
                }
            }
        }
    }
}
