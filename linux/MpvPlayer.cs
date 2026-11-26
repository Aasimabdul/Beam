using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Beam
{
    /// <summary>
    /// Plays through the system's mpv. Nothing is running while idle: mpv starts on a cast and quits when it ends.
    /// Control goes over mpv's JSON IPC socket; commands sent before it is up are queued.
    /// </summary>
    public sealed class MpvPlayer : IPlayer
    {
        private readonly Settings cfg;
        private readonly object sync = new object();
        private Process proc;
        private Socket sock;
        private int gen;
        private readonly List<string> pending = new List<string>();
        private bool connected;
        private long pos, dur;
        private int vol = 100;
        private bool mute;
        private string kind = "video";
        private string title = "";
        private bool desiredPaused = true;
        private bool settled;
        private long lastDur;

        public UpnpDevice Device;

        public MpvPlayer(Settings cfg)
        {
            this.cfg = cfg;
            vol = cfg.Volume;
        }

        public static string FindMpv()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string d in path.Split(':'))
            {
                if (d.Length == 0) continue;
                string f = Path.Combine(d, "mpv");
                if (File.Exists(f)) return f;
            }
            return null;
        }

        public bool Available { get { return FindMpv() != null; } }

        // ------------------------------------------------------------------ IPlayer

        public void Load(string uri, string meta, string k, string t)
        {
            lock (sync)
            {
                Kill();
                Interlocked.Exchange(ref pos, 0);
                Interlocked.Exchange(ref dur, 0);
                lastDur = 0;
                kind = k;
                title = t;
                desiredPaused = true;
                settled = false;
                string mpv = FindMpv();
                Uri u;
                if (mpv == null || !Uri.TryCreate(uri, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                {
                    int g0 = gen;
                    ThreadPool.QueueUserWorkItem(_ => { if (g0 == gen && Device != null) Device.OnPlayerState("STOPPED"); });
                    return;
                }
                string socket = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "beam-mpv-" + Environment.ProcessId + ".sock");
                try { File.Delete(socket); } catch (Exception) { }
                string input = Path.Combine(Path.GetTempPath(), "beam-input-" + Environment.ProcessId + ".conf");
                File.WriteAllText(input, "v script-message beam-next\n1 script-message beam-mode 0\n2 script-message beam-mode 1\n3 script-message beam-mode 2\n4 script-message beam-mode 3\n");

                var psi = new ProcessStartInfo(mpv) { UseShellExecute = false, RedirectStandardInput = false };
                foreach (string a in new[]
                {
                    "--no-config", "--no-terminal", "--input-ipc-server=" + socket, "--input-conf=" + input, "--pause", "--force-window=yes",
                    "--title=Beam - " + (t.Length > 0 ? t : "Now playing"), "--image-display-duration=inf", "--keep-open=no", "--ytdl=no",
                    "--osc=yes", "--volume=" + vol, "--mute=" + (mute ? "yes" : "no"),
                })
                    psi.ArgumentList.Add(a);
                if (cfg.Fullscreen && k != "audio") psi.ArgumentList.Add("--fs");
                foreach (string a in ModeArgs(cfg.Mode)) psi.ArgumentList.Add(a);
                string extra = Environment.GetEnvironmentVariable("BEAM_MPV_ARGS");
                if (!string.IsNullOrWhiteSpace(extra)) foreach (string a in extra.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(uri);

                int g = ++gen;
                connected = false;
                pending.Clear();
                try
                {
                    proc = Process.Start(psi);
                    proc.EnableRaisingEvents = true;
                    Process me = proc;
                    me.Exited += (s, e) => OnExit(g);
                }
                catch (Exception ex)
                {
                    Settings.Log("mpv start failed: " + ex.Message);
                    ThreadPool.QueueUserWorkItem(_ => { if (g == gen && Device != null) Device.OnPlayerState("STOPPED"); });
                    return;
                }
                var t2 = new Thread(() => Connect(g, socket)) { IsBackground = true, Name = "beam-mpv" };
                t2.Start();
            }
        }

        public void Play()
        {
            lock (sync)
            {
                desiredPaused = false;
                settled = false;
                Send("{\"command\":[\"set_property\",\"pause\",false]}");
            }
        }

        public void Pause()
        {
            lock (sync)
            {
                desiredPaused = true;
                settled = false;
                Send("{\"command\":[\"set_property\",\"pause\",true]}");
            }
        }

        public void Stop()
        {
            lock (sync) { Kill(); }
        }

        public void Seek(long ms)
        {
            lock (sync)
            {
                Interlocked.Exchange(ref pos, ms);
                Send("{\"command\":[\"seek\"," + (ms / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ",\"absolute\"]}");
            }
        }

        public long PositionMs { get { return Interlocked.Read(ref pos); } }
        public long DurationMs { get { return Interlocked.Read(ref dur); } }

        public void SetVolume(int percent)
        {
            lock (sync)
            {
                vol = percent;
                mute = false;
                cfg.Volume = vol;
                Send("{\"command\":[\"set_property\",\"volume\"," + vol + "]}");
                Send("{\"command\":[\"set_property\",\"mute\",false]}");
            }
        }

        public int Volume { get { return vol; } }

        public void SetMute(bool m)
        {
            lock (sync)
            {
                mute = m;
                Send("{\"command\":[\"set_property\",\"mute\"," + (m ? "true" : "false") + "]}");
            }
        }

        public bool Muted { get { return mute; } }

        // ------------------------------------------------------------------ picture mode

        public static string[] ModeArgs(AspectMode m)
        {
            switch (m)
            {
                case AspectMode.Fill: return new[] { "--keepaspect=no", "--panscan=0", "--video-aspect-override=-1" };
                case AspectMode.Wide: return new[] { "--keepaspect=yes", "--panscan=0", "--video-aspect-override=16:9" };
                case AspectMode.Crop: return new[] { "--keepaspect=yes", "--panscan=1", "--video-aspect-override=-1" };
                default: return new[] { "--keepaspect=yes", "--panscan=0", "--video-aspect-override=-1" };
            }
        }

        /// <summary>Changes the picture mode now (also for a cast already playing).</summary>
        public void SetMode(AspectMode m, bool announce)
        {
            lock (sync)
            {
                cfg.Mode = m;
                cfg.Save();
                switch (m)
                {
                    case AspectMode.Fill: Prop("keepaspect", "no"); Prop("panscan", "0"); Prop("video-aspect-override", "-1"); break;
                    case AspectMode.Wide: Prop("keepaspect", "yes"); Prop("panscan", "0"); Prop("video-aspect-override", "16:9"); break;
                    case AspectMode.Crop: Prop("keepaspect", "yes"); Prop("panscan", "1"); Prop("video-aspect-override", "-1"); break;
                    default: Prop("keepaspect", "yes"); Prop("panscan", "0"); Prop("video-aspect-override", "-1"); break;
                }
                if (announce) Send("{\"command\":[\"show-text\",\"Picture: " + Aspect.Label(m) + "\",1500]}");
            }
        }

        private void Prop(string name, string value)
        {
            Send("{\"command\":[\"set_property\",\"" + name + "\",\"" + value + "\"]}");
        }

        // ------------------------------------------------------------------ process and socket

        private void Kill()
        {
            gen++;
            connected = false;
            pending.Clear();
            Socket s = sock;
            sock = null;
            Process p = proc;
            proc = null;
            if (s != null)
            {
                try { s.Send(Encoding.UTF8.GetBytes("{\"command\":[\"quit\"]}\n")); } catch (Exception) { }
                try { s.Close(); } catch (Exception) { }
            }
            if (p != null)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { if (!p.WaitForExit(1500)) p.Kill(); } catch (Exception) { }
                    try { p.Dispose(); } catch (Exception) { }
                });
            }
        }

        private void Send(string json)
        {
            // caller holds sync
            if (proc == null) return;
            if (!connected || sock == null) { if (pending.Count < 64) pending.Add(json); return; }
            try { sock.Send(Encoding.UTF8.GetBytes(json + "\n")); }
            catch (Exception) { }
        }

        private void Connect(int g, string path)
        {
            Socket s = null;
            for (int i = 0; i < 160 && s == null; i++)
            {
                lock (sync) { if (g != gen) return; }
                if (File.Exists(path))
                {
                    try
                    {
                        var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        c.Connect(new UnixDomainSocketEndPoint(path));
                        s = c;
                    }
                    catch (Exception) { }
                }
                if (s == null) Thread.Sleep(50);
            }
            if (s == null)
            {
                Settings.Log("mpv IPC never came up");
                return;
            }
            lock (sync)
            {
                if (g != gen) { s.Close(); return; }
                sock = s;
                connected = true;
                foreach (string cmd in new[]
                {
                    "{\"command\":[\"observe_property\",1,\"time-pos\"]}", "{\"command\":[\"observe_property\",2,\"duration\"]}",
                    "{\"command\":[\"observe_property\",3,\"pause\"]}",
                })
                    s.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
                foreach (string cmd in pending) s.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
                pending.Clear();
            }
            Read(g, s);
        }

        private void Read(int g, Socket s)
        {
            var buf = new byte[4096];
            var line = new StringBuilder();
            try
            {
                while (true)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) return;
                    line.Append(Encoding.UTF8.GetString(buf, 0, n));
                    int nl;
                    while ((nl = IndexOfNl(line)) >= 0)
                    {
                        string one = line.ToString(0, nl);
                        line.Remove(0, nl + 1);
                        if (one.Length > 0) Handle(g, one);
                    }
                }
            }
            catch (Exception) { }
        }

        private static int IndexOfNl(StringBuilder b)
        {
            for (int i = 0; i < b.Length; i++) if (b[i] == '\n') return i;
            return -1;
        }

        private void Handle(int g, string json)
        {
            try
            {
                using (JsonDocument d = JsonDocument.Parse(json))
                {
                    JsonElement root = d.RootElement;
                    JsonElement ev;
                    if (!root.TryGetProperty("event", out ev)) return;
                    string name = ev.GetString();
                    if (name == "property-change")
                    {
                        string prop = root.GetProperty("name").GetString();
                        JsonElement data;
                        if (!root.TryGetProperty("data", out data)) return;
                        if (prop == "time-pos" && data.ValueKind == JsonValueKind.Number)
                            Interlocked.Exchange(ref pos, (long)(data.GetDouble() * 1000));
                        else if (prop == "duration" && data.ValueKind == JsonValueKind.Number)
                        {
                            long ms = (long)(data.GetDouble() * 1000);
                            Interlocked.Exchange(ref dur, ms);
                            if (ms != lastDur)
                            {
                                lastDur = ms;
                                if (g == gen && Device != null) Device.OnMediaReady();
                            }
                        }
                        else if (prop == "pause" && (data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False))
                        {
                            bool paused = data.GetBoolean();
                            lock (sync)
                            {
                                if (g != gen) return;
                                if (!settled)
                                {
                                    if (paused == desiredPaused) settled = true;
                                    return;
                                }
                            }
                            if (kind != "image" && Device != null) Device.OnPlayerState(paused ? "PAUSED_PLAYBACK" : "PLAYING");
                        }
                    }
                    else if (name == "client-message")
                    {
                        JsonElement args;
                        if (!root.TryGetProperty("args", out args) || args.ValueKind != JsonValueKind.Array) return;
                        string a0 = args.GetArrayLength() > 0 ? args[0].GetString() : "";
                        if (a0 == "beam-next") SetMode(Aspect.Next(cfg.Mode), true);
                        else if (a0 == "beam-mode" && args.GetArrayLength() > 1)
                        {
                            int m;
                            if (int.TryParse(args[1].GetString(), out m) && m >= 0 && m < Aspect.All.Length) SetMode(Aspect.All[m], true);
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        private void OnExit(int g)
        {
            lock (sync)
            {
                if (g != gen) return; // we stopped it ourselves
                gen++;
                connected = false;
                Socket s = sock;
                sock = null;
                proc = null;
                if (s != null) { try { s.Close(); } catch (Exception) { } }
                Interlocked.Exchange(ref pos, 0);
            }
            if (Device != null) Device.OnPlayerState("STOPPED");
        }

        public void Shutdown()
        {
            lock (sync) { Kill(); }
        }
    }
}
