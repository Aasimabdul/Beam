using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace Beam.Tests
{
    internal sealed class FakePlayer : IPlayer
    {
        public readonly List<string> Log = new List<string>();
        public int Vol = 50;
        public bool Mute;
        public void Load(string uri, string meta, string kind, string title) { Log.Add("load " + uri + " " + kind + " " + title); }
        public void Play() { Log.Add("play"); }
        public void Pause() { Log.Add("pause"); }
        public void Stop() { Log.Add("stop"); }
        public void Seek(long ms) { Log.Add("seek " + ms); }
        public long PositionMs { get { return 65000; } }
        public long DurationMs { get { return 3600000; } }
        public void SetVolume(int p) { Vol = p; }
        public int Volume { get { return Vol; } }
        public void SetMute(bool m) { Mute = m; }
        public bool Muted { get { return Mute; } }
        public bool Has(string s) { return Log.Any(l => l.Contains(s)); }
    }

    internal static class Program
    {
        private static int checks, fails;

        private static void Ok(bool c, string what)
        {
            checks++;
            if (!c) { fails++; Console.WriteLine("FAIL: " + what); }
        }

        private static XDocument Parse(string s) { return XDocument.Parse(s); }

        private static string Raw(int port, string req)
        {
            using (var c = new TcpClient("127.0.0.1", port))
            {
                c.ReceiveTimeout = 4000;
                NetworkStream ns = c.GetStream();
                byte[] b = Encoding.Latin1.GetBytes(req);
                ns.Write(b, 0, b.Length);
                var ms = new MemoryStream();
                var buf = new byte[4096];
                try
                {
                    int n;
                    while ((n = ns.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                }
                catch (IOException) { }
                return Encoding.Latin1.GetString(ms.ToArray());
            }
        }

        private static string SoapBody(string svc, string action, string args)
        {
            return "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:"
                + action + " xmlns:u=\"urn:schemas-upnp-org:service:" + svc + ":1\">" + args + "</u:" + action + "></s:Body></s:Envelope>";
        }

        private static string[] Post(int port, string svc, string action, string args)
        {
            string body = SoapBody(svc, action, args);
            string r = Raw(port, "POST /ctl/" + svc + " HTTP/1.1\r\nHost: x\r\nContent-Type: text/xml\r\nSOAPACTION: \"urn:schemas-upnp-org:service:"
                + svc + ":1#" + action + "\"\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
            string code = r.Length > 12 ? r.Substring(9, 3) : "";
            int i = r.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return new[] { code, i < 0 ? "" : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(r.Substring(i + 4))) };
        }

        private static string Tag(string xml, string tag)
        {
            int a = xml.IndexOf("<" + tag + ">", StringComparison.Ordinal);
            if (a < 0) return null;
            a += tag.Length + 2;
            int b = xml.IndexOf("</" + tag + ">", a, StringComparison.Ordinal);
            return b < 0 ? null : xml.Substring(a, b - a).Replace("&amp;", "&");
        }

        private static int Main()
        {
            string uuid = Guid.NewGuid().ToString();

            // ---- XML validity and SCPD consistency
            XDocument dev = Parse(Upnp.DeviceXml(uuid, "Living & Room <TV>"));
            Ok(dev.Descendants().Count(e => e.Name.LocalName == "service") == 3, "3 services in description");
            Ok(dev.Descendants().First(e => e.Name.LocalName == "friendlyName").Value == "Living & Room <TV>", "name escaped");
            foreach (string s in Upnp.Services)
            {
                XDocument d = Parse(Upnp.Scpd(s));
                var vars = new HashSet<string>(d.Descendants().Where(e => e.Name.LocalName == "stateVariable").Select(e => e.Elements().First(x => x.Name.LocalName == "name").Value));
                foreach (XElement rel in d.Descendants().Where(e => e.Name.LocalName == "relatedStateVariable"))
                    Ok(vars.Contains(rel.Value), s + ": undefined related var " + rel.Value);
                Ok(d.Descendants().Count(e => e.Name.LocalName == "action") > 2, s + " has actions");
            }
            Ok(Upnp.Scpd("Nope") == null, "unknown scpd null");
            XDocument av = Parse(Upnp.Scpd("AVTransport"));
            var names = new HashSet<string>(av.Descendants().Where(e => e.Name.LocalName == "action").Select(e => e.Elements().First().Value));
            foreach (string n in new[] { "SetAVTransportURI", "Play", "Pause", "Stop", "Seek", "GetPositionInfo", "GetTransportInfo", "GetMediaInfo" }) Ok(names.Contains(n), "AVT action " + n);

            // ---- SOAP parse, DIDL, time
            string didl = "&lt;DIDL-Lite xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot; xmlns:dc=&quot;http://purl.org/dc/elements/1.1/&quot; xmlns:upnp=&quot;urn:schemas-upnp-org:metadata-1-0/upnp/&quot;&gt;&lt;item id=&quot;1&quot;&gt;&lt;dc:title&gt;Tamil &amp;amp; Movie&lt;/dc:title&gt;&lt;upnp:class&gt;object.item.videoItem&lt;/upnp:class&gt;&lt;res protocolInfo=&quot;http-get:*:video/mp4:*&quot;&gt;http://x/a.mp4&lt;/res&gt;&lt;/item&gt;&lt;/DIDL-Lite&gt;";
            Upnp.Soap sp = Upnp.ParseSoap(SoapBody("AVTransport", "SetAVTransportURI", "<InstanceID>0</InstanceID><CurrentURI>http://x/a.mp4?a=1&amp;b=2</CurrentURI><CurrentURIMetaData>" + didl + "</CurrentURIMetaData>"));
            Ok(sp != null && sp.Action == "SetAVTransportURI", "action parsed");
            Ok(sp.Arg("CurrentURI") == "http://x/a.mp4?a=1&b=2", "uri unescaped: " + sp.Arg("CurrentURI"));
            Ok(Upnp.DidlTitle(sp.Arg("CurrentURIMetaData")) == "Tamil & Movie", "didl title: " + Upnp.DidlTitle(sp.Arg("CurrentURIMetaData")));
            Ok(Upnp.MediaKind(sp.Arg("CurrentURIMetaData"), "http://x/a") == "video", "kind video");
            Upnp.Soap cd = Upnp.ParseSoap(SoapBody("AVTransport", "SetAVTransportURI", "<CurrentURI><![CDATA[http://x/b.jpg]]></CurrentURI><CurrentURIMetaData><![CDATA[<DIDL-Lite><item><upnp:class>object.item.imageItem.photo</upnp:class></item></DIDL-Lite>]]></CurrentURIMetaData>"));
            Ok(cd != null && cd.Arg("CurrentURI") == "http://x/b.jpg", "cdata uri");
            Ok(Upnp.MediaKind(cd.Arg("CurrentURIMetaData"), "x") == "image", "kind image from class");
            Ok(Upnp.MediaKind("", "http://x/song.MP3?x=1") == "audio", "kind audio from ext");
            Ok(Upnp.MediaKind(null, "http://x/stream") == "video", "kind default video");
            Ok(Upnp.ParseSoap("garbage") == null && Upnp.ParseSoap("<a/>") == null && Upnp.ParseSoap("") == null, "bad soap null");
            Ok(Upnp.ParseSoap("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:Play xmlns:u=\"x\"><InstanceID>&e;</InstanceID></u:Play></s:Body></s:Envelope>") == null, "doctype refused");
            Ok(Upnp.Fmt(3725000) == "1:02:05" && Upnp.Fmt(-5) == "0:00:00", "fmt");
            Ok(Upnp.ParseTime("1:02:05") == 3725000L && Upnp.ParseTime("0:00:01.500") == 1500 && Upnp.ParseTime("NOT_IMPLEMENTED") == -1 && Upnp.ParseTime("00:00:10") == 10000, "parseTime");
            Ok(Upnp.ParseTime("+0:00:05") == 5000 && Upnp.ParseTime(null) == -1 && Upnp.ParseTime("a:b:c") == -1, "parseTime edge");
            Ok(Upnp.Esc("a<b>&\"'\u0001") == "a&lt;b&gt;&amp;&quot;&apos;", "esc strips control chars");

            // ---- SSDP messages
            Ok(SsdpMsg.Targets(uuid).Count == 6, "6 targets");
            Ok(SsdpMsg.Usn(uuid, "uuid:" + uuid) == "uuid:" + uuid, "usn uuid");
            Ok(SsdpMsg.Usn(uuid, "upnp:rootdevice") == "uuid:" + uuid + "::upnp:rootdevice", "usn root");
            string ms = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";
            SsdpMsg.Search q = SsdpMsg.ParseSearch(ms);
            Ok(q != null && q.Mx == 3 && q.St == Upnp.Device, "parse search");
            Ok(SsdpMsg.Answers(uuid, q.St).Count == 1, "answers one");
            Ok(SsdpMsg.Answers(uuid, "ssdp:all").Count == 6, "ssdp:all");
            Ok(SsdpMsg.Answers(uuid, "urn:schemas-upnp-org:device:MediaServer:1").Count == 0, "other type none");
            Ok(SsdpMsg.ParseSearch("NOTIFY * HTTP/1.1\r\n\r\n") == null, "notify not search");
            Ok(SsdpMsg.ParseSearch("M-SEARCH * HTTP/1.1\r\nMAN: \"ssdp:other\"\r\nST: ssdp:all\r\n\r\n") == null, "wrong MAN");
            Ok(SsdpMsg.ParseSearch("m-search * HTTP/1.1\r\nman: \"ssdp:discover\"\r\nmx: x\r\nst: ssdp:all\r\n\r\n").Mx == 1, "lowercase + bad MX");
            string resp = SsdpMsg.SearchResponse(uuid, Upnp.Device, "http://1.2.3.4:5/desc.xml");
            Ok(resp.StartsWith("HTTP/1.1 200 OK\r\n") && resp.Contains("LOCATION: http://1.2.3.4:5/desc.xml") && resp.EndsWith("\r\n\r\n"), "response format");
            Ok(SsdpMsg.Alive(uuid, "upnp:rootdevice", "http://h/d").Contains("NTS: ssdp:alive"), "alive");
            Ok(!SsdpMsg.ByeBye(uuid, "upnp:rootdevice").Contains("LOCATION"), "byebye without location");
            string lip = Lan.LocalIpFor(IPAddress.Parse("192.168.1.9"));
            Ok(lip == null || lip.Length > 6, "localIpFor no crash");
            Ok(Lan.SameSubnet(new byte[] { 192, 168, 1, 5 }, new byte[] { 192, 168, 1, 99 }, 24) && !Lan.SameSubnet(new byte[] { 192, 168, 1, 5 }, new byte[] { 192, 168, 2, 99 }, 24)
               && Lan.SameSubnet(new byte[] { 10, 1, 0, 1 }, new byte[] { 10, 1, 7, 7 }, 16), "sameSubnet");

            // ---- HTTP + SOAP end to end
            var fp = new FakePlayer();
            var device = new UpnpDevice(uuid, "Test TV", fp);
            var http = new MiniHttp(device);
            int port = http.Start(0);
            try
            {
                string d = Raw(port, "GET /desc.xml HTTP/1.1\r\nHost: x\r\n\r\n");
                Ok(d.StartsWith("HTTP/1.1 200 OK") && d.Contains("<friendlyName>Test TV</friendlyName>") && d.ToLowerInvariant().Contains("content-length"), "desc.xml served");
                Ok(Raw(port, "HEAD /desc.xml HTTP/1.1\r\n\r\n").IndexOf("<root", StringComparison.Ordinal) < 0, "HEAD has no body");
                Ok(Raw(port, "GET /nothing HTTP/1.1\r\n\r\n").StartsWith("HTTP/1.1 404"), "404");
                Ok(Raw(port, "GET /scpd/AVTransport.xml HTTP/1.1\r\n\r\n").Contains("<scpd"), "scpd served");

                string[] r = Post(port, "AVTransport", "GetTransportInfo", "<InstanceID>0</InstanceID>");
                Ok(r[0] == "200" && Tag(r[1], "CurrentTransportState") == "NO_MEDIA_PRESENT", "initial state NO_MEDIA: " + r[1]);
                r = Post(port, "AVTransport", "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
                Ok(r[0] == "500" && Tag(r[1], "errorCode") == "701", "Play with no media -> 701");
                r = Post(port, "AVTransport", "Bogus", "");
                Ok(r[0] == "500" && Tag(r[1], "errorCode") == "401", "unknown action -> 401");
                r = Post(port, "AVTransport", "SetAVTransportURI", "<InstanceID>0</InstanceID><CurrentURI>http://x/a.mp4?a=1&amp;b=2</CurrentURI><CurrentURIMetaData>" + didl + "</CurrentURIMetaData>");
                Ok(r[0] == "200", "SetAVTransportURI 200");
                Ok(fp.Has("load http://x/a.mp4?a=1&b=2 video Tamil & Movie"), "player loaded: " + string.Join(";", fp.Log));
                r = Post(port, "AVTransport", "GetTransportInfo", "<InstanceID>0</InstanceID>");
                Ok(Tag(r[1], "CurrentTransportState") == "STOPPED", "STOPPED after set uri");
                r = Post(port, "AVTransport", "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
                Ok(r[0] == "200" && fp.Has("play"), "play");
                r = Post(port, "AVTransport", "GetTransportInfo", "<InstanceID>0</InstanceID>");
                Ok(Tag(r[1], "CurrentTransportState") == "PLAYING", "PLAYING");
                r = Post(port, "AVTransport", "GetPositionInfo", "<InstanceID>0</InstanceID>");
                Ok(Tag(r[1], "RelTime") == "0:01:05" && Tag(r[1], "TrackDuration") == "1:00:00" && Tag(r[1], "TrackURI") == "http://x/a.mp4?a=1&b=2", "position info: " + r[1]);
                r = Post(port, "AVTransport", "Seek", "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>0:10:00</Target>");
                Ok(r[0] == "200" && fp.Has("seek 600000"), "seek");
                r = Post(port, "AVTransport", "Seek", "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>abc</Target>");
                Ok(r[0] == "500" && Tag(r[1], "errorCode") == "711", "bad seek target");
                r = Post(port, "AVTransport", "Seek", "<InstanceID>0</InstanceID><Unit>X</Unit><Target>1</Target>");
                Ok(Tag(r[1], "errorCode") == "710", "bad seek unit");
                r = Post(port, "AVTransport", "Pause", "<InstanceID>0</InstanceID>");
                Ok(r[0] == "200" && device.State == "PAUSED_PLAYBACK", "pause");
                r = Post(port, "AVTransport", "GetCurrentTransportActions", "<InstanceID>0</InstanceID>");
                Ok(Tag(r[1], "Actions") == "Play,Stop,Seek", "actions when paused");
                r = Post(port, "AVTransport", "Stop", "<InstanceID>0</InstanceID>");
                Ok(r[0] == "200" && device.State == "STOPPED", "stop");
                r = Post(port, "AVTransport", "GetMediaInfo", "<InstanceID>0</InstanceID>");
                Ok(Tag(r[1], "NrTracks") == "1" && Tag(r[1], "PlayMedium") == "NETWORK", "media info");

                r = Post(port, "RenderingControl", "SetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>250</DesiredVolume>");
                Ok(r[0] == "200" && fp.Vol == 100, "volume clamped to 100");
                r = Post(port, "RenderingControl", "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
                Ok(Tag(r[1], "CurrentVolume") == "100", "get volume");
                Post(port, "RenderingControl", "SetMute", "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>1</DesiredMute>");
                r = Post(port, "RenderingControl", "GetMute", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
                Ok(Tag(r[1], "CurrentMute") == "1", "mute");
                r = Post(port, "ConnectionManager", "GetProtocolInfo", "");
                Ok(Tag(r[1], "Sink").Contains("video/mp4"), "protocol info");
                r = Post(port, "ConnectionManager", "GetCurrentConnectionInfo", "<ConnectionID>0</ConnectionID>");
                Ok(Tag(r[1], "Direction") == "Input", "connection info");
                r = Post(port, "Nope", "Play", "");
                Ok(r[0] == "404", "unknown service 404");

                // chunked body + Expect: 100-continue
                string body = SoapBody("RenderingControl", "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
                string chunked = Raw(port, "POST /ctl/RenderingControl HTTP/1.1\r\nHost: x\r\nTransfer-Encoding: chunked\r\nSOAPACTION: \"x#GetVolume\"\r\n\r\n"
                    + body.Length.ToString("x") + "\r\n" + body + "\r\n0\r\n\r\n");
                Ok(chunked.Contains("<CurrentVolume>100</CurrentVolume>"), "chunked request");
                string exp = Raw(port, "POST /ctl/RenderingControl HTTP/1.1\r\nHost: x\r\nExpect: 100-continue\r\nContent-Length: " + body.Length + "\r\n\r\n" + body);
                Ok(exp.StartsWith("HTTP/1.1 100 Continue") && exp.Contains("<CurrentVolume>"), "expect 100-continue");
                Ok(Raw(port, "POST /ctl/RenderingControl HTTP/1.1\r\nContent-Length: 99999999\r\n\r\n").Length == 0, "oversized body dropped");
                Ok(Raw(port, "\r\n\r\n").Length == 0 && Raw(port, "garbage\r\n\r\n").Length == 0, "garbage requests ignored");
                r = Post(port, "RenderingControl", "GetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel>");
                Ok(r[0] == "200", "server still alive after garbage");

                // ---- eventing
                var got = new BlockingCollection<string>();
                var cb = new TcpListener(IPAddress.Loopback, 0);
                cb.Start();
                var t = new Thread(() =>
                {
                    while (true)
                    {
                        try
                        {
                            using (TcpClient c = cb.AcceptTcpClient())
                            {
                                c.ReceiveTimeout = 3000;
                                NetworkStream ns = c.GetStream();
                                var bo = new MemoryStream();
                                int len = -1, endHead = -1, ch;
                                while ((ch = ns.ReadByte()) >= 0)
                                {
                                    bo.WriteByte((byte)ch);
                                    string h = Encoding.Latin1.GetString(bo.ToArray());
                                    if (endHead < 0 && h.EndsWith("\r\n\r\n"))
                                    {
                                        endHead = h.Length;
                                        int i = h.ToLowerInvariant().IndexOf("content-length:", StringComparison.Ordinal);
                                        len = int.Parse(h.Substring(i + 15, h.IndexOf("\r\n", i, StringComparison.Ordinal) - i - 15).Trim());
                                    }
                                    if (endHead >= 0 && bo.Length >= endHead + len) break;
                                }
                                byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
                                ns.Write(ok, 0, ok.Length);
                                got.Add(Encoding.UTF8.GetString(bo.ToArray()));
                            }
                        }
                        catch (Exception) { if (!cb.Server.IsBound) return; }
                    }
                }) { IsBackground = true };
                t.Start();
                string cbUrl = "http://127.0.0.1:" + ((IPEndPoint)cb.LocalEndpoint).Port + "/cb";
                string sub = Raw(port, "SUBSCRIBE /evt/AVTransport HTTP/1.1\r\nHost: x\r\nCALLBACK: <" + cbUrl + ">\r\nNT: upnp:event\r\nTIMEOUT: Second-300\r\n\r\n");
                Ok(sub.StartsWith("HTTP/1.1 200") && sub.Contains("SID: uuid:") && sub.Contains("TIMEOUT: Second-300"), "subscribe: " + sub);
                int si = sub.IndexOf("SID: ", StringComparison.Ordinal) + 5;
                string sid = sub.Substring(si, sub.IndexOf("\r\n", si, StringComparison.Ordinal) - si);
                string n0; got.TryTake(out n0, 3000);
                Ok(n0 != null && n0.StartsWith("NOTIFY /cb") && n0.Contains("SEQ: 0") && n0.Contains("SID: " + sid), "initial event: " + n0);
                Ok(n0 != null && n0.Contains("LastChange") && n0.Contains("TransportState"), "initial has LastChange");
                Ok(n0 != null && Parse(n0.Substring(n0.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)) != null, "event body is valid XML");
                Post(port, "AVTransport", "SetAVTransportURI", "<InstanceID>0</InstanceID><CurrentURI>http://x/new.mp4</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>");
                string n1; got.TryTake(out n1, 3000);
                Ok(n1 != null && n1.Contains("SEQ: 1") && n1.Contains("http://x/new.mp4"), "event on new uri: " + n1);
                Post(port, "AVTransport", "Play", "<InstanceID>0</InstanceID><Speed>1</Speed>");
                string n2; got.TryTake(out n2, 3000);
                Ok(n2 != null && n2.Contains("SEQ: 2") && n2.Contains("PLAYING"), "event on play");
                device.OnPlayerState("STOPPED");
                string n3; got.TryTake(out n3, 3000);
                Ok(n3 != null && n3.Contains("STOPPED") && n3.Contains("SEQ: 3"), "event on player-side stop");
                string renew = Raw(port, "SUBSCRIBE /evt/AVTransport HTTP/1.1\r\nSID: " + sid + "\r\nTIMEOUT: Second-300\r\n\r\n");
                Ok(renew.StartsWith("HTTP/1.1 200") && renew.Contains("SID: " + sid), "renew");
                Ok(Raw(port, "SUBSCRIBE /evt/AVTransport HTTP/1.1\r\nSID: uuid:nope\r\n\r\n").StartsWith("HTTP/1.1 412"), "renew unknown sid");
                Ok(Raw(port, "SUBSCRIBE /evt/AVTransport HTTP/1.1\r\n\r\n").StartsWith("HTTP/1.1 412"), "subscribe without callback");
                Ok(Raw(port, "UNSUBSCRIBE /evt/AVTransport HTTP/1.1\r\nSID: " + sid + "\r\n\r\n").StartsWith("HTTP/1.1 200"), "unsubscribe");
                device.OnPlayerState("PLAYING");
                string none; Ok(!got.TryTake(out none, 700), "no events after unsubscribe");
                Ok(Raw(port, "UNSUBSCRIBE /evt/AVTransport HTTP/1.1\r\nSID: " + sid + "\r\n\r\n").StartsWith("HTTP/1.1 412"), "unsubscribe twice");
                string dead = Raw(port, "SUBSCRIBE /evt/RenderingControl HTTP/1.1\r\nCALLBACK: <http://127.0.0.1:1/x>\r\nNT: upnp:event\r\n\r\n");
                Ok(dead.StartsWith("HTTP/1.1 200"), "subscribe dead callback accepted");
                Thread.Sleep(800);
                r = Post(port, "RenderingControl", "SetVolume", "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>10</DesiredVolume>");
                Ok(r[0] == "200", "still serving after dead subscriber");
                cb.Stop();
            }
            finally
            {
                http.Stop();
                device.Shutdown();
            }

            // ---- live SSDP (needs a multicast-capable interface; skipped when the sandbox has none)
            if (Environment.GetEnvironmentVariable("BEAM_SKIP_LIVE") == null && Lan.Addresses().Count > 0)
            {
                var ss = new SsdpServer(uuid, 4321);
                try
                {
                    ss.Start();
                    using (var c = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
                    {
                        c.Client.ReceiveTimeout = 4000;
                        byte[] m = Encoding.ASCII.GetBytes(ms);
                        c.Send(m, m.Length, new IPEndPoint(IPAddress.Parse(SsdpMsg.Group), SsdpMsg.Port));
                        string text = null;
                        try
                        {
                            for (int i = 0; i < 10 && text == null; i++)
                            {
                                var from = new IPEndPoint(IPAddress.Any, 0);
                                byte[] bytes = c.Receive(ref from);
                                string x = Encoding.ASCII.GetString(bytes);
                                if (x.StartsWith("HTTP/1.1 200")) text = x;
                            }
                            Ok(text != null && text.Contains("ST: " + Upnp.Device) && text.Contains(":4321/desc.xml"), "live SSDP reply: " + text);
                        }
                        catch (SocketException) { Console.WriteLine("SKIP live SSDP (no reply in sandbox multicast)"); }
                    }
                    ss.Restart();
                    Ok(true, "restart does not throw");
                }
                catch (Exception e) { Console.WriteLine("SKIP live SSDP: " + e.Message); }
                finally { ss.Shutdown(); }
            }
            else Console.WriteLine("SKIP live SSDP (disabled or no multicast interface)");

            Console.WriteLine(checks + " checks, " + fails + " failed");
            return fails == 0 ? 0 : 1;
        }
    }
}
