using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Beam
{
    /// <summary>The playback side. Methods must return quickly (they are called from HTTP threads).</summary>
    public interface IPlayer
    {
        void Load(string uri, string meta, string kind, string title);
        void Play();
        void Pause();
        void Stop();
        void Seek(long ms);
        long PositionMs { get; }
        long DurationMs { get; }
        void SetVolume(int percent);
        int Volume { get; }
        void SetMute(bool mute);
        bool Muted { get; }
    }

    /// <summary>
    /// The MediaRenderer: answers description / control / event requests and keeps the transport state.
    /// It talks to the real player through IPlayer; the player reports back with the On... methods.
    /// </summary>
    public sealed class UpnpDevice : IHttpHandler
    {
        private sealed class Fault : Exception
        {
            public readonly int Code;
            public Fault(int code, string text) : base(text) { Code = code; }
        }

        private sealed class Sub
        {
            public string Service;
            public List<string> Callbacks;
            public long Expiry;
            public int Seq;
        }

        private static readonly Regex CallbackRx = new Regex("<([^>]+)>");

        private readonly string uuid;
        private volatile string name;
        private readonly IPlayer player;
        private readonly Dictionary<string, Sub> subs = new Dictionary<string, Sub>();
        private readonly object sync = new object();
        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private volatile bool shut;

        private string state = "NO_MEDIA_PRESENT";
        private string uri = "";
        private string meta = "";
        private string nextUri = "";
        private string nextMeta = "";

        public UpnpDevice(string uuid, string name, IPlayer player)
        {
            this.uuid = uuid;
            this.name = name;
            this.player = player;
            var t = new Thread(() =>
            {
                foreach (Action a in queue.GetConsumingEnumerable())
                {
                    try { a(); } catch (Exception) { /* a bad subscriber never stops the others */ }
                }
            }) { IsBackground = true, Name = "beam-notify" };
            t.Start();
        }

        public string Uuid { get { return uuid; } }
        public void SetName(string n) { name = n; }
        public string State { get { lock (sync) return state; } }

        public void Shutdown()
        {
            shut = true;
            try { queue.CompleteAdding(); } catch (Exception) { }
        }

        private void Post(Action a)
        {
            try { if (!shut) queue.Add(a); } catch (InvalidOperationException) { /* shut down */ }
        }

        private static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        // ------------------------------------------------------------------ HTTP routing

        public MiniHttp.Response Handle(MiniHttp.Request r)
        {
            string p = r.Path;
            bool read = r.Method == "GET" || r.Method == "HEAD";
            if (read && p == "/desc.xml") return MiniHttp.Response.Xml(200, Upnp.DeviceXml(uuid, name));
            if (read && p.StartsWith("/scpd/", StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.Ordinal))
            {
                string x = Upnp.Scpd(p.Substring(6, p.Length - 10));
                return x == null ? null : MiniHttp.Response.Xml(200, x);
            }
            if (p.StartsWith("/ctl/", StringComparison.Ordinal) && r.Method == "POST") return Control(p.Substring(5), r);
            if (p.StartsWith("/evt/", StringComparison.Ordinal)) return Event(p.Substring(5), r);
            return null;
        }

        private static bool IsService(string s) { return Array.IndexOf(Upnp.Services, s) >= 0; }

        private static MiniHttp.Response FaultResponse(int code, string text)
        {
            return MiniHttp.Response.Xml(500, Upnp.SoapFault(code, text));
        }

        private MiniHttp.Response Control(string svc, MiniHttp.Request r)
        {
            if (!IsService(svc)) return null;
            Upnp.Soap s = Upnp.ParseSoap(r.BodyText());
            if (s == null) return FaultResponse(401, "Invalid Action");
            try
            {
                string outp;
                switch (svc)
                {
                    case "AVTransport": outp = AvTransport(s); break;
                    case "RenderingControl": outp = Rendering(s); break;
                    default: outp = Connection(s); break;
                }
                if (outp == null) return FaultResponse(401, "Invalid Action");
                return MiniHttp.Response.Xml(200, outp);
            }
            catch (Fault f)
            {
                return FaultResponse(f.Code, f.Message);
            }
        }

        // ------------------------------------------------------------------ AVTransport

        private string Actions()
        {
            switch (state)
            {
                case "PLAYING": return "Pause,Stop,Seek";
                case "PAUSED_PLAYBACK": return "Play,Stop,Seek";
                case "STOPPED": return "Play,Seek";
                default: return "";
            }
        }

        private string AvTransport(Upnp.Soap s)
        {
            string a = s.Action;
            const string svc = "AVTransport";
            switch (a)
            {
                case "SetAVTransportURI":
                    SetUri(s.Arg("CurrentURI").Trim(), s.Arg("CurrentURIMetaData"));
                    return Upnp.SoapResponse(svc, a);
                case "SetNextAVTransportURI":
                    lock (sync)
                    {
                        nextUri = s.Arg("NextURI").Trim();
                        nextMeta = s.Arg("NextURIMetaData");
                    }
                    return Upnp.SoapResponse(svc, a);
                case "GetMediaInfo":
                {
                    string u, m, nu, nm;
                    lock (sync) { u = uri; m = meta; nu = nextUri; nm = nextMeta; }
                    return Upnp.SoapResponse(svc, a, "NrTracks", u.Length == 0 ? "0" : "1", "MediaDuration", Upnp.Fmt(player.DurationMs),
                        "CurrentURI", u, "CurrentURIMetaData", m, "NextURI", nu, "NextURIMetaData", nm,
                        "PlayMedium", "NETWORK", "RecordMedium", "NOT_IMPLEMENTED", "WriteStatus", "NOT_IMPLEMENTED");
                }
                case "GetTransportInfo":
                    return Upnp.SoapResponse(svc, a, "CurrentTransportState", State, "CurrentTransportStatus", "OK", "CurrentSpeed", "1");
                case "GetPositionInfo":
                {
                    string u, m;
                    lock (sync) { u = uri; m = meta; }
                    string pos = Upnp.Fmt(player.PositionMs);
                    return Upnp.SoapResponse(svc, a, "Track", u.Length == 0 ? "0" : "1", "TrackDuration", Upnp.Fmt(player.DurationMs),
                        "TrackMetaData", m, "TrackURI", u, "RelTime", pos, "AbsTime", pos,
                        "RelCount", "2147483647", "AbsCount", "2147483647");
                }
                case "GetDeviceCapabilities":
                    return Upnp.SoapResponse(svc, a, "PlayMedia", "NETWORK", "RecMedia", "NOT_IMPLEMENTED", "RecQualityModes", "NOT_IMPLEMENTED");
                case "GetTransportSettings":
                    return Upnp.SoapResponse(svc, a, "PlayMode", "NORMAL", "RecQualityMode", "NOT_IMPLEMENTED");
                case "GetCurrentTransportActions":
                    lock (sync) { return Upnp.SoapResponse(svc, a, "Actions", Actions()); }
                case "Play":
                    lock (sync)
                    {
                        if (uri.Length == 0) throw new Fault(701, "Transition not available");
                        player.Play();
                        ChangeState("PLAYING");
                    }
                    return Upnp.SoapResponse(svc, a);
                case "Pause":
                    lock (sync)
                    {
                        if (uri.Length == 0) throw new Fault(701, "Transition not available");
                        player.Pause();
                        ChangeState("PAUSED_PLAYBACK");
                    }
                    return Upnp.SoapResponse(svc, a);
                case "Stop":
                    lock (sync)
                    {
                        player.Stop();
                        ChangeState(uri.Length == 0 ? "NO_MEDIA_PRESENT" : "STOPPED");
                    }
                    return Upnp.SoapResponse(svc, a);
                case "Seek":
                {
                    string unit = s.Arg("Unit");
                    string target = s.Arg("Target");
                    if (unit == "REL_TIME" || unit == "ABS_TIME")
                    {
                        long ms = Upnp.ParseTime(target);
                        if (ms < 0) throw new Fault(711, "Illegal seek target");
                        player.Seek(ms);
                    }
                    else if (unit == "TRACK_NR") player.Seek(0);
                    else throw new Fault(710, "Seek mode not supported");
                    return Upnp.SoapResponse(svc, a);
                }
                case "Next":
                case "Previous":
                    throw new Fault(701, "Transition not available");
                default:
                    return null;
            }
        }

        private void SetUri(string u, string m)
        {
            lock (sync)
            {
                uri = u;
                meta = m;
                if (u.Length == 0)
                {
                    player.Stop();
                    state = "NO_MEDIA_PRESENT";
                }
                else
                {
                    player.Load(u, m, Upnp.MediaKind(m, u), Upnp.DidlTitle(m));
                    state = "STOPPED";
                }
                SendEvent("AVTransport", true);
            }
        }

        // ------------------------------------------------------------------ RenderingControl / ConnectionManager

        private string Rendering(Upnp.Soap s)
        {
            const string svc = "RenderingControl";
            switch (s.Action)
            {
                case "ListPresets": return Upnp.SoapResponse(svc, s.Action, "CurrentPresetNameList", "FactoryDefaults");
                case "SelectPreset": return Upnp.SoapResponse(svc, s.Action);
                case "GetVolume": return Upnp.SoapResponse(svc, s.Action, "CurrentVolume", player.Volume.ToString());
                case "SetVolume":
                {
                    int v;
                    if (!int.TryParse(s.Arg("DesiredVolume").Trim(), out v)) v = 0;
                    player.SetVolume(Math.Max(0, Math.Min(100, v)));
                    lock (sync) { SendEvent("RenderingControl", false); }
                    return Upnp.SoapResponse(svc, s.Action);
                }
                case "GetMute": return Upnp.SoapResponse(svc, s.Action, "CurrentMute", player.Muted ? "1" : "0");
                case "SetMute":
                {
                    string m = s.Arg("DesiredMute").Trim();
                    player.SetMute(m == "1" || string.Equals(m, "true", StringComparison.OrdinalIgnoreCase));
                    lock (sync) { SendEvent("RenderingControl", false); }
                    return Upnp.SoapResponse(svc, s.Action);
                }
                default: return null;
            }
        }

        private string Connection(Upnp.Soap s)
        {
            const string svc = "ConnectionManager";
            switch (s.Action)
            {
                case "GetProtocolInfo": return Upnp.SoapResponse(svc, s.Action, "Source", "", "Sink", Upnp.Sink);
                case "GetCurrentConnectionIDs": return Upnp.SoapResponse(svc, s.Action, "ConnectionIDs", "0");
                case "GetCurrentConnectionInfo":
                    return Upnp.SoapResponse(svc, s.Action, "RcsID", "0", "AVTransportID", "0", "ProtocolInfo", "",
                        "PeerConnectionManager", "", "PeerConnectionID", "-1", "Direction", "Input", "Status", "OK");
                default: return null;
            }
        }

        // ------------------------------------------------------------------ reports from the player

        private void ChangeState(string s)
        {
            if (s == state) return;
            state = s;
            SendEvent("AVTransport", false);
        }

        /// <summary>The player's state changed by itself (finished, paused on the PC, error...).</summary>
        public void OnPlayerState(string s)
        {
            lock (sync) { ChangeState(s); }
        }

        /// <summary>Volume or mute was changed on the PC itself: tell subscribers.</summary>
        public void OnVolumeChanged()
        {
            lock (sync) { SendEvent("RenderingControl", false); }
        }

        /// <summary>Duration became known: tell subscribers the full state again.</summary>
        public void OnMediaReady()
        {
            lock (sync) { SendEvent("AVTransport", true); }
        }

        // ------------------------------------------------------------------ eventing (GENA)

        private MiniHttp.Response Event(string svc, MiniHttp.Request r)
        {
            if (!IsService(svc)) return null;
            long now = Now();
            switch (r.Method)
            {
                case "SUBSCRIBE":
                {
                    long seconds = 1800;
                    string t = r.Header("timeout");
                    long parsed;
                    if (t != null && t.ToLowerInvariant().StartsWith("second-", StringComparison.Ordinal)
                        && long.TryParse(t.Substring(7).Trim(), out parsed))
                        seconds = Math.Min(1800, Math.Max(60, parsed));
                    string sid = r.Header("sid");
                    lock (sync)
                    {
                        Purge(now);
                        if (sid != null)
                        {
                            Sub existing;
                            if (!subs.TryGetValue(sid, out existing) || existing.Service != svc) return MiniHttp.Response.Empty(412, "Precondition Failed");
                            existing.Expiry = now + seconds * 1000;
                            return Subscribed(sid, seconds);
                        }
                        string cb = r.Header("callback");
                        if (cb == null || !string.Equals(r.Header("nt"), "upnp:event", StringComparison.OrdinalIgnoreCase))
                            return MiniHttp.Response.Empty(412, "Precondition Failed");
                        var urls = new List<string>();
                        foreach (Match m in CallbackRx.Matches(cb))
                            if (m.Groups[1].Value.StartsWith("http://", StringComparison.Ordinal)) urls.Add(m.Groups[1].Value);
                        if (urls.Count == 0 || subs.Count >= 16) return MiniHttp.Response.Empty(412, "Precondition Failed");
                        string id = "uuid:" + Guid.NewGuid();
                        var sub = new Sub { Service = svc, Callbacks = urls, Expiry = now + seconds * 1000 };
                        subs[id] = sub;
                        // the first event must arrive after this reply has been sent
                        Task.Delay(250).ContinueWith(_ => Post(() => SendInitial(id, sub)));
                        return Subscribed(id, seconds);
                    }
                }
                case "UNSUBSCRIBE":
                {
                    string sid = r.Header("sid");
                    lock (sync)
                    {
                        if (sid != null && subs.Remove(sid)) return MiniHttp.Response.Empty(200, "OK");
                    }
                    return MiniHttp.Response.Empty(412, "Precondition Failed");
                }
                default:
                    return MiniHttp.Response.Empty(405, "Method Not Allowed");
            }
        }

        private static MiniHttp.Response Subscribed(string sid, long seconds)
        {
            MiniHttp.Response r = MiniHttp.Response.Empty(200, "OK");
            r.Headers["SID"] = sid;
            r.Headers["TIMEOUT"] = "Second-" + seconds;
            return r;
        }

        private void Purge(long now)
        {
            List<string> dead = null;
            foreach (KeyValuePair<string, Sub> e in subs)
                if (e.Value.Expiry < now) (dead ?? (dead = new List<string>())).Add(e.Key);
            if (dead != null) foreach (string k in dead) subs.Remove(k);
        }

        private static string V(string n, string val) { return "<" + n + " val=\"" + Upnp.Esc(val) + "\"/>"; }

        /// <summary>The evented "LastChange" (or property set) for svc. Caller holds the lock.</summary>
        private string PropertySet(string svc, bool full)
        {
            var b = new StringBuilder(512);
            b.Append("<?xml version=\"1.0\"?><e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">");
            switch (svc)
            {
                case "AVTransport":
                {
                    var e = new StringBuilder(512);
                    e.Append("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\"><InstanceID val=\"0\">")
                        .Append(V("TransportState", state)).Append(V("TransportStatus", "OK"))
                        .Append(V("CurrentTransportActions", Actions()));
                    if (full)
                    {
                        string dur = Upnp.Fmt(player.DurationMs);
                        e.Append(V("NumberOfTracks", uri.Length == 0 ? "0" : "1")).Append(V("CurrentTrack", uri.Length == 0 ? "0" : "1"))
                            .Append(V("AVTransportURI", uri)).Append(V("AVTransportURIMetaData", meta))
                            .Append(V("CurrentTrackURI", uri)).Append(V("CurrentTrackMetaData", meta))
                            .Append(V("CurrentTrackDuration", dur)).Append(V("CurrentMediaDuration", dur))
                            .Append(V("PlaybackStorageMedium", "NETWORK")).Append(V("CurrentPlayMode", "NORMAL"))
                            .Append(V("TransportPlaySpeed", "1"));
                    }
                    e.Append("</InstanceID></Event>");
                    b.Append("<e:property><LastChange>").Append(Upnp.Esc(e.ToString())).Append("</LastChange></e:property>");
                    break;
                }
                case "RenderingControl":
                {
                    string e = "<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/RCS/\"><InstanceID val=\"0\">"
                        + "<Volume channel=\"Master\" val=\"" + player.Volume + "\"/>"
                        + "<Mute channel=\"Master\" val=\"" + (player.Muted ? "1" : "0") + "\"/></InstanceID></Event>";
                    b.Append("<e:property><LastChange>").Append(Upnp.Esc(e)).Append("</LastChange></e:property>");
                    break;
                }
                default:
                    b.Append("<e:property><SourceProtocolInfo></SourceProtocolInfo></e:property>")
                        .Append("<e:property><SinkProtocolInfo>").Append(Upnp.Esc(Upnp.Sink)).Append("</SinkProtocolInfo></e:property>")
                        .Append("<e:property><CurrentConnectionIDs>0</CurrentConnectionIDs></e:property>");
                    break;
            }
            return b.Append("</e:propertyset>").ToString();
        }

        private void SendInitial(string sid, Sub sub)
        {
            string body;
            int seq;
            lock (sync)
            {
                if (!subs.ContainsKey(sid)) return;
                body = PropertySet(sub.Service, true);
                seq = sub.Seq++;
            }
            if (!Deliver(sub, sid, seq, body)) lock (sync) { subs.Remove(sid); }
        }

        /// <summary>Queues one event to every subscriber of svc. Caller holds the lock.</summary>
        private void SendEvent(string svc, bool full)
        {
            if (subs.Count == 0) return;
            Purge(Now());
            string body = PropertySet(svc, full);
            foreach (KeyValuePair<string, Sub> e in new List<KeyValuePair<string, Sub>>(subs))
            {
                Sub sub = e.Value;
                if (sub.Service != svc) continue;
                string sid = e.Key;
                int seq = sub.Seq++;
                Post(() =>
                {
                    if (!Deliver(sub, sid, seq, body)) lock (sync) { subs.Remove(sid); }
                });
            }
        }

        private static bool Deliver(Sub sub, string sid, int seq, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body);
            foreach (string cb in sub.Callbacks)
            {
                try
                {
                    var u = new Uri(cb);
                    string path = string.IsNullOrEmpty(u.PathAndQuery) ? "/" : u.PathAndQuery;
                    using (var c = new TcpClient())
                    {
                        if (!c.ConnectAsync(u.Host, u.Port).Wait(3000)) continue;
                        c.ReceiveTimeout = 3000;
                        c.SendTimeout = 3000;
                        string head = "NOTIFY " + path + " HTTP/1.1\r\nHOST: " + u.Host + ":" + u.Port
                            + "\r\nCONTENT-TYPE: text/xml; charset=\"utf-8\"\r\nNT: upnp:event\r\nNTS: upnp:propchange\r\nSID: " + sid
                            + "\r\nSEQ: " + seq + "\r\nCONTENT-LENGTH: " + payload.Length + "\r\nCONNECTION: close\r\n\r\n";
                        NetworkStream ns = c.GetStream();
                        byte[] h = Encoding.ASCII.GetBytes(head);
                        ns.Write(h, 0, h.Length);
                        ns.Write(payload, 0, payload.Length);
                        ns.Flush();
                        var buf = new byte[32];
                        if (ns.Read(buf, 0, buf.Length) > 0) return true;
                    }
                }
                catch (Exception)
                {
                    // try the next callback URL
                }
            }
            return false;
        }
    }
}
