using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Beam
{
    /// <summary>UPnP / DLNA MediaRenderer documents: device and service descriptions, SOAP, DIDL helpers. No UI or OS dependencies (testable anywhere).</summary>
    public static class Upnp
    {
        public const string Device = "urn:schemas-upnp-org:device:MediaRenderer:1";
        public static readonly string[] Services = { "AVTransport", "RenderingControl", "ConnectionManager" };

        public static string ServiceType(string s) { return "urn:schemas-upnp-org:service:" + s + ":1"; }

        /// <summary>Protocols this renderer says it can play (what Windows' built-in media stack handles).</summary>
        public const string Sink =
            "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/x-ms-wmv:*,http-get:*:video/x-msvideo:*,"
            + "http-get:*:video/3gpp:*,http-get:*:video/mpeg:*,http-get:*:video/quicktime:*,http-get:*:audio/mpeg:*,"
            + "http-get:*:audio/mp4:*,http-get:*:audio/aac:*,http-get:*:audio/flac:*,http-get:*:audio/x-ms-wma:*,"
            + "http-get:*:audio/wav:*,http-get:*:audio/x-wav:*,http-get:*:audio/L16:*,"
            + "http-get:*:image/jpeg:*,http-get:*:image/png:*,http-get:*:image/gif:*,http-get:*:image/bmp:*";

        // ------------------------------------------------------------------ escaping

        public static string Esc(string s)
        {
            if (s == null) return "";
            var b = new StringBuilder(s.Length + 16);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': b.Append("&amp;"); break;
                    case '<': b.Append("&lt;"); break;
                    case '>': b.Append("&gt;"); break;
                    case '"': b.Append("&quot;"); break;
                    case '\'': b.Append("&apos;"); break;
                    default:
                        if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r') b.Append(c);
                        break;
                }
            }
            return b.ToString();
        }

        public static string Unesc(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
        }

        // ------------------------------------------------------------------ device description

        public static string DeviceXml(string uuid, string name)
        {
            var b = new StringBuilder(1024);
            b.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
                .Append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\" xmlns:dlna=\"urn:schemas-dlna-org:device-1-0\">")
                .Append("<specVersion><major>1</major><minor>0</minor></specVersion><device>")
                .Append("<dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>")
                .Append("<deviceType>").Append(Device).Append("</deviceType>")
                .Append("<friendlyName>").Append(Esc(name)).Append("</friendlyName>")
                .Append("<manufacturer>Beam</manufacturer><modelName>Beam Receiver</modelName><modelNumber>1</modelNumber>")
                .Append("<UDN>uuid:").Append(uuid).Append("</UDN><serviceList>");
            foreach (string s in Services)
            {
                b.Append("<service><serviceType>").Append(ServiceType(s)).Append("</serviceType>")
                    .Append("<serviceId>urn:upnp-org:serviceId:").Append(s).Append("</serviceId>")
                    .Append("<SCPDURL>/scpd/").Append(s).Append(".xml</SCPDURL>")
                    .Append("<controlURL>/ctl/").Append(s).Append("</controlURL>")
                    .Append("<eventSubURL>/evt/").Append(s).Append("</eventSubURL></service>");
            }
            return b.Append("</serviceList></device></root>").ToString();
        }

        // ------------------------------------------------------------------ service descriptions (generated from compact tables)

        private const string I = "InstanceID=A_ARG_TYPE_InstanceID";

        private static readonly string[] AvtActions =
        {
            "SetAVTransportURI|" + I + ",CurrentURI=AVTransportURI,CurrentURIMetaData=AVTransportURIMetaData|",
            "SetNextAVTransportURI|" + I + ",NextURI=NextAVTransportURI,NextURIMetaData=NextAVTransportURIMetaData|",
            "GetMediaInfo|" + I + "|NrTracks=NumberOfTracks,MediaDuration=CurrentMediaDuration,CurrentURI=AVTransportURI,"
                + "CurrentURIMetaData=AVTransportURIMetaData,NextURI=NextAVTransportURI,NextURIMetaData=NextAVTransportURIMetaData,"
                + "PlayMedium=PlaybackStorageMedium,RecordMedium=RecordStorageMedium,WriteStatus=RecordMediumWriteStatus",
            "GetTransportInfo|" + I + "|CurrentTransportState=TransportState,CurrentTransportStatus=TransportStatus,CurrentSpeed=TransportPlaySpeed",
            "GetPositionInfo|" + I + "|Track=CurrentTrack,TrackDuration=CurrentTrackDuration,TrackMetaData=CurrentTrackMetaData,"
                + "TrackURI=CurrentTrackURI,RelTime=RelativeTimePosition,AbsTime=AbsoluteTimePosition,"
                + "RelCount=RelativeCounterPosition,AbsCount=AbsoluteCounterPosition",
            "GetDeviceCapabilities|" + I + "|PlayMedia=PossiblePlaybackStorageMedia,RecMedia=PossibleRecordStorageMedia,RecQualityModes=PossibleRecordQualityModes",
            "GetTransportSettings|" + I + "|PlayMode=CurrentPlayMode,RecQualityMode=CurrentRecordQualityMode",
            "Stop|" + I + "|",
            "Play|" + I + ",Speed=TransportPlaySpeed|",
            "Pause|" + I + "|",
            "Seek|" + I + ",Unit=A_ARG_TYPE_SeekMode,Target=A_ARG_TYPE_SeekTarget|",
            "Next|" + I + "|",
            "Previous|" + I + "|",
            "GetCurrentTransportActions|" + I + "|Actions=CurrentTransportActions",
        };

        private static readonly string[] AvtVars =
        {
            "TransportState:string:STOPPED/PLAYING/PAUSED_PLAYBACK/TRANSITIONING/NO_MEDIA_PRESENT",
            "TransportStatus:string:OK/ERROR_OCCURRED", "PlaybackStorageMedium:string", "RecordStorageMedium:string",
            "PossiblePlaybackStorageMedia:string", "PossibleRecordStorageMedia:string", "CurrentPlayMode:string:NORMAL",
            "TransportPlaySpeed:string:1", "RecordMediumWriteStatus:string", "CurrentRecordQualityMode:string",
            "PossibleRecordQualityModes:string", "NumberOfTracks:ui4", "CurrentTrack:ui4", "CurrentTrackDuration:string",
            "CurrentMediaDuration:string", "CurrentTrackMetaData:string", "CurrentTrackURI:string", "AVTransportURI:string",
            "AVTransportURIMetaData:string", "NextAVTransportURI:string", "NextAVTransportURIMetaData:string",
            "RelativeTimePosition:string", "AbsoluteTimePosition:string", "RelativeCounterPosition:i4",
            "AbsoluteCounterPosition:ui4", "CurrentTransportActions:string", "LastChange:string:!",
            "A_ARG_TYPE_SeekMode:string:REL_TIME/ABS_TIME/TRACK_NR", "A_ARG_TYPE_SeekTarget:string", "A_ARG_TYPE_InstanceID:ui4",
        };

        private const string C = "Channel=A_ARG_TYPE_Channel";

        private static readonly string[] RcsActions =
        {
            "ListPresets|" + I + "|CurrentPresetNameList=PresetNameList",
            "SelectPreset|" + I + ",PresetName=A_ARG_TYPE_PresetName|",
            "GetMute|" + I + "," + C + "|CurrentMute=Mute",
            "SetMute|" + I + "," + C + ",DesiredMute=Mute|",
            "GetVolume|" + I + "," + C + "|CurrentVolume=Volume",
            "SetVolume|" + I + "," + C + ",DesiredVolume=Volume|",
        };

        private static readonly string[] RcsVars =
        {
            "PresetNameList:string", "LastChange:string:!", "Mute:boolean", "Volume:ui2:@0-100",
            "A_ARG_TYPE_Channel:string:Master", "A_ARG_TYPE_InstanceID:ui4", "A_ARG_TYPE_PresetName:string:FactoryDefaults",
        };

        private static readonly string[] CmsActions =
        {
            "GetProtocolInfo||Source=SourceProtocolInfo,Sink=SinkProtocolInfo",
            "GetCurrentConnectionIDs||ConnectionIDs=CurrentConnectionIDs",
            "GetCurrentConnectionInfo|ConnectionID=A_ARG_TYPE_ConnectionID|RcsID=A_ARG_TYPE_RcsID,AVTransportID=A_ARG_TYPE_AVTransportID,"
                + "ProtocolInfo=A_ARG_TYPE_ProtocolInfo,PeerConnectionManager=A_ARG_TYPE_ConnectionManager,"
                + "PeerConnectionID=A_ARG_TYPE_ConnectionID,Direction=A_ARG_TYPE_Direction,Status=A_ARG_TYPE_ConnectionStatus",
        };

        private static readonly string[] CmsVars =
        {
            "SourceProtocolInfo:string:!", "SinkProtocolInfo:string:!", "CurrentConnectionIDs:string:!",
            "A_ARG_TYPE_ConnectionStatus:string:OK/ContentFormatMismatch/InsufficientBandwidth/UnreliableChannel/Unknown",
            "A_ARG_TYPE_ConnectionManager:string", "A_ARG_TYPE_Direction:string:Input/Output", "A_ARG_TYPE_ProtocolInfo:string",
            "A_ARG_TYPE_ConnectionID:i4", "A_ARG_TYPE_AVTransportID:i4", "A_ARG_TYPE_RcsID:i4",
        };

        public static string Scpd(string service)
        {
            string[] actions, vars;
            switch (service)
            {
                case "AVTransport": actions = AvtActions; vars = AvtVars; break;
                case "RenderingControl": actions = RcsActions; vars = RcsVars; break;
                case "ConnectionManager": actions = CmsActions; vars = CmsVars; break;
                default: return null;
            }
            var b = new StringBuilder(4096);
            b.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">")
                .Append("<specVersion><major>1</major><minor>0</minor></specVersion><actionList>");
            foreach (string a in actions)
            {
                string[] p = a.Split('|');
                b.Append("<action><name>").Append(p[0]).Append("</name>");
                if (p[1].Length > 0 || p[2].Length > 0)
                {
                    b.Append("<argumentList>");
                    Args(b, p[1], "in");
                    Args(b, p[2], "out");
                    b.Append("</argumentList>");
                }
                b.Append("</action>");
            }
            b.Append("</actionList><serviceStateTable>");
            foreach (string v in vars)
            {
                string[] p = v.Split(':');
                bool ev = p.Length > 2 && p[2] == "!";
                b.Append("<stateVariable sendEvents=\"").Append(ev ? "yes" : "no").Append("\"><name>").Append(p[0])
                    .Append("</name><dataType>").Append(p[1]).Append("</dataType>");
                if (p.Length > 2 && p[2].StartsWith("@", StringComparison.Ordinal))
                {
                    string[] r = p[2].Substring(1).Split('-');
                    b.Append("<allowedValueRange><minimum>").Append(r[0]).Append("</minimum><maximum>").Append(r[1])
                        .Append("</maximum><step>1</step></allowedValueRange>");
                }
                else if (p.Length > 2 && !ev)
                {
                    b.Append("<allowedValueList>");
                    foreach (string a in p[2].Split('/')) b.Append("<allowedValue>").Append(a).Append("</allowedValue>");
                    b.Append("</allowedValueList>");
                }
                b.Append("</stateVariable>");
            }
            return b.Append("</serviceStateTable></scpd>").ToString();
        }

        private static void Args(StringBuilder b, string list, string dir)
        {
            if (list.Length == 0) return;
            foreach (string a in list.Split(','))
            {
                int eq = a.IndexOf('=');
                b.Append("<argument><name>").Append(a.Substring(0, eq)).Append("</name><direction>").Append(dir)
                    .Append("</direction><relatedStateVariable>").Append(a.Substring(eq + 1)).Append("</relatedStateVariable></argument>");
            }
        }

        // ------------------------------------------------------------------ SOAP

        public sealed class Soap
        {
            public readonly string Action;
            public readonly Dictionary<string, string> Args;
            internal Soap(string action, Dictionary<string, string> args) { Action = action; Args = args; }
            public string Arg(string name) { string v; return Args.TryGetValue(name, out v) ? v : ""; }
        }

        /// <summary>Reads the action name and its arguments out of a SOAP envelope; null if it isn't one.</summary>
        public static Soap ParseSoap(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                XDocument d;
                using (var sr = new StringReader(body))
                using (var xr = XmlReader.Create(sr, settings)) d = XDocument.Load(xr);
                XElement root = d.Root;
                if (root == null || root.Name.LocalName != "Envelope") return null;
                XElement bodyEl = null;
                foreach (XElement c in root.Elements()) if (c.Name.LocalName == "Body") bodyEl = c;
                if (bodyEl == null) return null;
                foreach (XElement act in bodyEl.Elements())
                {
                    var args = new Dictionary<string, string>();
                    foreach (XElement a in act.Elements()) args[a.Name.LocalName] = a.Value;
                    return new Soap(act.Name.LocalName, args);
                }
            }
            catch (Exception)
            {
                return null;
            }
            return null;
        }

        public static string SoapResponse(string service, string action, params string[] nameValue)
        {
            var b = new StringBuilder(512);
            b.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
                .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
                .Append("<s:Body><u:").Append(action).Append("Response xmlns:u=\"").Append(ServiceType(service)).Append("\">");
            for (int i = 0; i + 1 < nameValue.Length; i += 2)
                b.Append('<').Append(nameValue[i]).Append('>').Append(Esc(nameValue[i + 1])).Append("</").Append(nameValue[i]).Append('>');
            return b.Append("</u:").Append(action).Append("Response></s:Body></s:Envelope>").ToString();
        }

        public static string SoapFault(int code, string text)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
                + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
                + "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>"
                + "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode><errorDescription>"
                + Esc(text) + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        }

        // ------------------------------------------------------------------ DIDL-Lite and time helpers

        private static readonly Regex TitleRx = new Regex("<(?:\\w+:)?title[^>]*>([^<]*)</(?:\\w+:)?title>", RegexOptions.IgnoreCase);
        private static readonly Regex ClassRx = new Regex("<(?:\\w+:)?class[^>]*>([^<]*)</(?:\\w+:)?class>", RegexOptions.IgnoreCase);
        private static readonly Regex ProtoRx = new Regex("protocolInfo\\s*=\\s*\"[^\"]*?:[^\"]*?:([^:\"]+/[^:\"]+):", RegexOptions.IgnoreCase);

        /// <summary>The title in DIDL-Lite metadata, or "" when there is none.</summary>
        public static string DidlTitle(string didl)
        {
            if (didl == null) return "";
            Match m = TitleRx.Match(didl);
            return m.Success ? Unesc(m.Groups[1].Value).Trim() : "";
        }

        /// <summary>"video", "audio" or "image": from the DIDL class, then the declared MIME type, then the file extension.</summary>
        public static string MediaKind(string didl, string uri)
        {
            if (didl != null)
            {
                Match m = ClassRx.Match(didl);
                if (m.Success)
                {
                    string c = m.Groups[1].Value.ToLowerInvariant();
                    if (c.Contains("imageitem") || c.Contains("photo")) return "image";
                    if (c.Contains("audioitem") || c.Contains("musictrack")) return "audio";
                    if (c.Contains("videoitem") || c.Contains("movie")) return "video";
                }
                Match p = ProtoRx.Match(didl);
                if (p.Success)
                {
                    string mime = p.Groups[1].Value.ToLowerInvariant();
                    if (mime.StartsWith("image/", StringComparison.Ordinal)) return "image";
                    if (mime.StartsWith("audio/", StringComparison.Ordinal)) return "audio";
                    if (mime.StartsWith("video/", StringComparison.Ordinal)) return "video";
                }
            }
            string u = (uri ?? "").ToLowerInvariant();
            int q = u.IndexOf('?');
            if (q >= 0) u = u.Substring(0, q);
            foreach (string e in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" }) if (u.EndsWith(e, StringComparison.Ordinal)) return "image";
            foreach (string e in new[] { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav", ".wma" }) if (u.EndsWith(e, StringComparison.Ordinal)) return "audio";
            return "video";
        }

        /// <summary>Milliseconds as H:MM:SS (the UPnP time format).</summary>
        public static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            long s = ms / 1000;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", s / 3600, (s / 60) % 60, s % 60);
        }

        /// <summary>"H:MM:SS" or "H:MM:SS.fff" to milliseconds; -1 when it can't be read.</summary>
        public static long ParseTime(string t)
        {
            if (t == null) return -1;
            string[] p = t.Trim().Split(':');
            if (p.Length != 3) return -1;
            long h, m;
            double s;
            if (!long.TryParse(p[0].Replace("+", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return -1;
            if (!long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return -1;
            if (!double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) return -1;
            return (long)((h * 3600 + m * 60 + s) * 1000);
        }
    }
}
