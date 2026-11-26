using System;
using System.Collections.Generic;

namespace Beam
{
    /// <summary>SSDP message building and parsing.</summary>
    public static class SsdpMsg
    {
        public const string Group = "239.255.255.250";
        public const int Port = 1900;
        public const string Server = "Windows UPnP/1.0 Beam/1";
        public const int MaxAge = 1800;

        /// <summary>Every notification type the device announces.</summary>
        public static List<string> Targets(string uuid)
        {
            var l = new List<string> { "upnp:rootdevice", "uuid:" + uuid, Upnp.Device };
            foreach (string s in Upnp.Services) l.Add(Upnp.ServiceType(s));
            return l;
        }

        public static string Usn(string uuid, string nt)
        {
            return nt == "uuid:" + uuid ? nt : "uuid:" + uuid + "::" + nt;
        }

        public static string Alive(string uuid, string nt, string location)
        {
            return "NOTIFY * HTTP/1.1\r\nHOST: " + Group + ":" + Port + "\r\nCACHE-CONTROL: max-age=" + MaxAge
                + "\r\nLOCATION: " + location + "\r\nNT: " + nt + "\r\nNTS: ssdp:alive\r\nSERVER: " + Server
                + "\r\nUSN: " + Usn(uuid, nt) + "\r\n\r\n";
        }

        public static string ByeBye(string uuid, string nt)
        {
            return "NOTIFY * HTTP/1.1\r\nHOST: " + Group + ":" + Port + "\r\nNT: " + nt
                + "\r\nNTS: ssdp:byebye\r\nUSN: " + Usn(uuid, nt) + "\r\n\r\n";
        }

        public static string SearchResponse(string uuid, string st, string location)
        {
            return "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=" + MaxAge + "\r\nEXT:\r\nLOCATION: " + location
                + "\r\nSERVER: " + Server + "\r\nST: " + st + "\r\nUSN: " + Usn(uuid, st) + "\r\n\r\n";
        }

        /// <summary>A parsed M-SEARCH request.</summary>
        public sealed class Search
        {
            public readonly string St;
            public readonly int Mx;
            internal Search(string st, int mx) { St = st; Mx = mx; }
        }

        public static string Header(string packet, string name)
        {
            string key = name.ToLowerInvariant() + ":";
            foreach (string line in packet.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (line.ToLowerInvariant().StartsWith(key, StringComparison.Ordinal)) return line.Substring(key.Length).Trim();
            }
            return null;
        }

        /// <summary>Returns the search target when the packet is an ssdp:discover M-SEARCH, otherwise null.</summary>
        public static Search ParseSearch(string packet)
        {
            if (packet == null || !packet.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase)) return null;
            string man = Header(packet, "MAN");
            string st = Header(packet, "ST");
            if (man == null || st == null || !string.Equals(man.Replace("\"", ""), "ssdp:discover", StringComparison.OrdinalIgnoreCase)) return null;
            int mx = 1;
            string m = Header(packet, "MX");
            int parsed;
            if (m != null && int.TryParse(m, out parsed)) mx = Math.Max(1, Math.Min(5, parsed));
            return new Search(st, mx);
        }

        /// <summary>The notification types that should answer a search for st (empty if none).</summary>
        public static List<string> Answers(string uuid, string st)
        {
            List<string> all = Targets(uuid);
            if (string.Equals(st, "ssdp:all", StringComparison.OrdinalIgnoreCase)) return all;
            var outp = new List<string>();
            foreach (string t in all) if (string.Equals(t, st, StringComparison.OrdinalIgnoreCase)) outp.Add(t);
            return outp;
        }
    }
}
