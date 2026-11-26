using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Beam
{
    public interface IHttpHandler
    {
        MiniHttp.Response Handle(MiniHttp.Request r);
    }

    /// <summary>A tiny HTTP/1.1 server (one request per connection) for UPnP descriptions, SOAP control and event subscriptions.</summary>
    public sealed class MiniHttp
    {
        public sealed class Request
        {
            public string Method = "";
            public string Path = "";
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
            public byte[] Body = new byte[0];
            public IPAddress Peer;

            public string Header(string name) { string v; return Headers.TryGetValue(name.ToLowerInvariant(), out v) ? v : null; }
            public string BodyText() { return Encoding.UTF8.GetString(Body); }
        }

        public sealed class Response
        {
            public int Code;
            public string Message;
            public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
            public byte[] Body = new byte[0];

            public Response(int code, string message) { Code = code; Message = message; }

            public static Response Xml(int code, string text)
            {
                var r = new Response(code, code == 200 ? "OK" : "Internal Server Error");
                r.Headers["CONTENT-TYPE"] = "text/xml; charset=\"utf-8\"";
                r.Body = Encoding.UTF8.GetBytes(text);
                return r;
            }

            public static Response Empty(int code, string message) { return new Response(code, message); }
        }

        private const int MaxBody = 1024 * 1024;
        private const int MaxConnections = 16;
        private readonly IHttpHandler handler;
        private TcpListener listener;
        private volatile bool running;
        private int active;

        public MiniHttp(IHttpHandler handler) { this.handler = handler; }

        /// <summary>Starts listening on port (0 = any free port) and returns the port in use.</summary>
        public int Start(int port)
        {
            lock (this)
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                l.Start(16);
                listener = l;
                running = true;
                var t = new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "beam-accept" };
                t.Start();
                return ((IPEndPoint)l.LocalEndpoint).Port;
            }
        }

        public void Stop()
        {
            lock (this)
            {
                running = false;
                try { if (listener != null) listener.Stop(); } catch (Exception) { }
                listener = null;
            }
        }

        private void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch (Exception) { if (!running) return; Thread.Sleep(50); continue; }
                if (Interlocked.Increment(ref active) > MaxConnections)
                {
                    Interlocked.Decrement(ref active);
                    try { c.Close(); } catch (Exception) { }
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Serve(c); }
                    finally { Interlocked.Decrement(ref active); }
                });
            }
        }

        private void Serve(TcpClient c)
        {
            try
            {
                c.ReceiveTimeout = 10000;
                c.SendTimeout = 10000;
                NetworkStream ns = c.GetStream();
                var input = new BufferedStream(ns, 4096);
                Request req = Read(input, ns);
                if (req == null) return;
                var ep = c.Client.RemoteEndPoint as IPEndPoint;
                req.Peer = ep == null ? null : ep.Address;
                Response res;
                try { res = handler.Handle(req); }
                catch (Exception) { res = Response.Empty(500, "Internal Server Error"); }
                if (res == null) res = Response.Empty(404, "Not Found");
                Write(ns, res, req.Method == "HEAD");
            }
            catch (Exception)
            {
                // client went away or sent junk
            }
            finally
            {
                try { c.Close(); } catch (Exception) { }
            }
        }

        private static string Line(Stream input)
        {
            var b = new MemoryStream(64);
            int c;
            while ((c = input.ReadByte()) >= 0)
            {
                if (c == '\n') break;
                if (c != '\r') b.WriteByte((byte)c);
                if (b.Length > 16 * 1024) throw new IOException("line too long");
            }
            if (c < 0 && b.Length == 0) return null;
            return Encoding.GetEncoding("iso-8859-1").GetString(b.ToArray());
        }

        public static Request Read(Stream input, Stream output)
        {
            string first = Line(input);
            if (string.IsNullOrEmpty(first)) return null;
            string[] p = first.Split(' ');
            if (p.Length < 2) return null;
            var r = new Request { Method = p[0].ToUpperInvariant(), Path = p[1] };
            int q = r.Path.IndexOf('?');
            if (q >= 0) r.Path = r.Path.Substring(0, q);
            string h;
            int count = 0;
            while ((h = Line(input)) != null && h.Length > 0)
            {
                if (++count > 100) throw new IOException("too many headers");
                int c = h.IndexOf(':');
                if (c > 0) r.Headers[h.Substring(0, c).Trim().ToLowerInvariant()] = h.Substring(c + 1).Trim();
            }
            string expect = r.Header("expect");
            if (expect != null && expect.ToLowerInvariant().Contains("100-continue"))
            {
                byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                output.Write(cont, 0, cont.Length);
                output.Flush();
            }
            string te = r.Header("transfer-encoding");
            if (te != null && te.ToLowerInvariant().Contains("chunked"))
            {
                var b = new MemoryStream();
                while (true)
                {
                    string sz = Line(input);
                    if (sz == null) throw new IOException("bad chunk");
                    int semi = sz.IndexOf(';');
                    if (semi >= 0) sz = sz.Substring(0, semi);
                    int n;
                    if (!int.TryParse(sz.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out n) || n < 0)
                        throw new IOException("bad chunk size");
                    if (n == 0)
                    {
                        while ((h = Line(input)) != null && h.Length > 0) { /* trailers */ }
                        break;
                    }
                    if (b.Length + n > MaxBody) throw new IOException("body too large");
                    Copy(input, b, n);
                    Line(input);
                }
                r.Body = b.ToArray();
            }
            else
            {
                string cl = r.Header("content-length");
                int n = 0;
                if (cl != null && !int.TryParse(cl, out n)) throw new IOException("bad length");
                if (n < 0 || n > MaxBody) throw new IOException("body too large");
                var b = new MemoryStream(Math.Max(16, n));
                Copy(input, b, n);
                r.Body = b.ToArray();
            }
            return r;
        }

        private static void Copy(Stream input, Stream output, int n)
        {
            var buf = new byte[4096];
            while (n > 0)
            {
                int r = input.Read(buf, 0, Math.Min(buf.Length, n));
                if (r <= 0) throw new IOException("short body");
                output.Write(buf, 0, r);
                n -= r;
            }
        }

        private static void Write(Stream output, Response r, bool headOnly)
        {
            var b = new StringBuilder(256);
            b.Append("HTTP/1.1 ").Append(r.Code).Append(' ').Append(r.Message).Append("\r\n");
            b.Append("SERVER: ").Append(SsdpMsg.Server).Append("\r\n");
            foreach (KeyValuePair<string, string> e in r.Headers) b.Append(e.Key).Append(": ").Append(e.Value).Append("\r\n");
            b.Append("CONTENT-LENGTH: ").Append(r.Body.Length).Append("\r\nCONNECTION: close\r\n\r\n");
            byte[] head = Encoding.GetEncoding("iso-8859-1").GetBytes(b.ToString());
            output.Write(head, 0, head.Length);
            if (!headOnly) output.Write(r.Body, 0, r.Body.Length);
            output.Flush();
        }
    }
}
