package com.beam.app;

import java.io.StringReader;
import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import javax.xml.parsers.DocumentBuilder;
import javax.xml.parsers.DocumentBuilderFactory;
import org.w3c.dom.Document;
import org.w3c.dom.Element;
import org.w3c.dom.Node;
import org.xml.sax.InputSource;

/** UPnP / DLNA MediaRenderer documents: device and service descriptions, SOAP, DIDL helpers. Plain Java (JVM-testable). */
public final class Upnp {
    private Upnp() { }

    public static final String DEVICE = "urn:schemas-upnp-org:device:MediaRenderer:1";
    public static final String[] SERVICES = {"AVTransport", "RenderingControl", "ConnectionManager"};

    public static String serviceType(String s) { return "urn:schemas-upnp-org:service:" + s + ":1"; }

    /** Protocols this renderer says it can play (what Android's MediaPlayer handles on most phones). */
    public static final String SINK =
        "http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/webm:*,http-get:*:video/3gpp:*,"
            + "http-get:*:video/mpeg:*,http-get:*:video/quicktime:*,http-get:*:application/vnd.apple.mpegurl:*,"
            + "http-get:*:application/x-mpegURL:*,http-get:*:audio/mpeg:*,http-get:*:audio/mp4:*,http-get:*:audio/aac:*,"
            + "http-get:*:audio/flac:*,http-get:*:audio/ogg:*,http-get:*:audio/wav:*,http-get:*:audio/x-wav:*,"
            + "http-get:*:audio/L16:*,http-get:*:image/jpeg:*,http-get:*:image/png:*,http-get:*:image/webp:*,http-get:*:image/gif:*";

    // ------------------------------------------------------------------ escaping

    public static String esc(String s) {
        if (s == null) return "";
        StringBuilder b = new StringBuilder(s.length() + 16);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '&': b.append("&amp;"); break;
                case '<': b.append("&lt;"); break;
                case '>': b.append("&gt;"); break;
                case '"': b.append("&quot;"); break;
                case '\'': b.append("&apos;"); break;
                default:
                    if (c >= 0x20 || c == '\t' || c == '\n' || c == '\r') b.append(c);
            }
        }
        return b.toString();
    }

    // ------------------------------------------------------------------ device description

    public static String deviceXml(String uuid, String name) {
        StringBuilder b = new StringBuilder(1024);
        b.append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .append("<root xmlns=\"urn:schemas-upnp-org:device-1-0\" xmlns:dlna=\"urn:schemas-dlna-org:device-1-0\">")
            .append("<specVersion><major>1</major><minor>0</minor></specVersion><device>")
            .append("<dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>")
            .append("<deviceType>").append(DEVICE).append("</deviceType>")
            .append("<friendlyName>").append(esc(name)).append("</friendlyName>")
            .append("<manufacturer>Beam</manufacturer><modelName>Beam Receiver</modelName><modelNumber>1</modelNumber>")
            .append("<UDN>uuid:").append(uuid).append("</UDN><serviceList>");
        for (String s : SERVICES) {
            b.append("<service><serviceType>").append(serviceType(s)).append("</serviceType>")
                .append("<serviceId>urn:upnp-org:serviceId:").append(s).append("</serviceId>")
                .append("<SCPDURL>/scpd/").append(s).append(".xml</SCPDURL>")
                .append("<controlURL>/ctl/").append(s).append("</controlURL>")
                .append("<eventSubURL>/evt/").append(s).append("</eventSubURL></service>");
        }
        return b.append("</serviceList></device></root>").toString();
    }

    // ------------------------------------------------------------------ service descriptions (generated from compact tables)

    private static final String I = "InstanceID=A_ARG_TYPE_InstanceID";

    private static final String[] AVT_ACTIONS = {
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

    private static final String[] AVT_VARS = {
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

    private static final String C = "Channel=A_ARG_TYPE_Channel";

    private static final String[] RCS_ACTIONS = {
        "ListPresets|" + I + "|CurrentPresetNameList=PresetNameList",
        "SelectPreset|" + I + ",PresetName=A_ARG_TYPE_PresetName|",
        "GetMute|" + I + "," + C + "|CurrentMute=Mute",
        "SetMute|" + I + "," + C + ",DesiredMute=Mute|",
        "GetVolume|" + I + "," + C + "|CurrentVolume=Volume",
        "SetVolume|" + I + "," + C + ",DesiredVolume=Volume|",
    };

    private static final String[] RCS_VARS = {
        "PresetNameList:string", "LastChange:string:!", "Mute:boolean", "Volume:ui2:@0-100",
        "A_ARG_TYPE_Channel:string:Master", "A_ARG_TYPE_InstanceID:ui4", "A_ARG_TYPE_PresetName:string:FactoryDefaults",
    };

    private static final String[] CMS_ACTIONS = {
        "GetProtocolInfo||Source=SourceProtocolInfo,Sink=SinkProtocolInfo",
        "GetCurrentConnectionIDs||ConnectionIDs=CurrentConnectionIDs",
        "GetCurrentConnectionInfo|ConnectionID=A_ARG_TYPE_ConnectionID|RcsID=A_ARG_TYPE_RcsID,AVTransportID=A_ARG_TYPE_AVTransportID,"
            + "ProtocolInfo=A_ARG_TYPE_ProtocolInfo,PeerConnectionManager=A_ARG_TYPE_ConnectionManager,"
            + "PeerConnectionID=A_ARG_TYPE_ConnectionID,Direction=A_ARG_TYPE_Direction,Status=A_ARG_TYPE_ConnectionStatus",
    };

    private static final String[] CMS_VARS = {
        "SourceProtocolInfo:string:!", "SinkProtocolInfo:string:!", "CurrentConnectionIDs:string:!",
        "A_ARG_TYPE_ConnectionStatus:string:OK/ContentFormatMismatch/InsufficientBandwidth/UnreliableChannel/Unknown",
        "A_ARG_TYPE_ConnectionManager:string", "A_ARG_TYPE_Direction:string:Input/Output", "A_ARG_TYPE_ProtocolInfo:string",
        "A_ARG_TYPE_ConnectionID:i4", "A_ARG_TYPE_AVTransportID:i4", "A_ARG_TYPE_RcsID:i4",
    };

    public static String scpd(String service) {
        String[] actions, vars;
        switch (service) {
            case "AVTransport": actions = AVT_ACTIONS; vars = AVT_VARS; break;
            case "RenderingControl": actions = RCS_ACTIONS; vars = RCS_VARS; break;
            case "ConnectionManager": actions = CMS_ACTIONS; vars = CMS_VARS; break;
            default: return null;
        }
        StringBuilder b = new StringBuilder(4096);
        b.append("<?xml version=\"1.0\" encoding=\"utf-8\"?><scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">")
            .append("<specVersion><major>1</major><minor>0</minor></specVersion><actionList>");
        for (String a : actions) {
            String[] p = a.split("\\|", -1);
            b.append("<action><name>").append(p[0]).append("</name>");
            if (!p[1].isEmpty() || !p[2].isEmpty()) {
                b.append("<argumentList>");
                args(b, p[1], "in");
                args(b, p[2], "out");
                b.append("</argumentList>");
            }
            b.append("</action>");
        }
        b.append("</actionList><serviceStateTable>");
        for (String v : vars) {
            String[] p = v.split(":");
            boolean ev = p.length > 2 && p[2].equals("!");
            b.append("<stateVariable sendEvents=\"").append(ev ? "yes" : "no").append("\"><name>").append(p[0])
                .append("</name><dataType>").append(p[1]).append("</dataType>");
            if (p.length > 2 && p[2].startsWith("@")) {
                String[] r = p[2].substring(1).split("-");
                b.append("<allowedValueRange><minimum>").append(r[0]).append("</minimum><maximum>").append(r[1])
                    .append("</maximum><step>1</step></allowedValueRange>");
            } else if (p.length > 2 && !ev) {
                b.append("<allowedValueList>");
                for (String a : p[2].split("/")) b.append("<allowedValue>").append(a).append("</allowedValue>");
                b.append("</allowedValueList>");
            }
            b.append("</stateVariable>");
        }
        return b.append("</serviceStateTable></scpd>").toString();
    }

    private static void args(StringBuilder b, String list, String dir) {
        if (list.isEmpty()) return;
        for (String a : list.split(",")) {
            int eq = a.indexOf('=');
            String name = a.substring(0, eq);
            String rel = a.substring(eq + 1);
            b.append("<argument><name>").append(name).append("</name><direction>").append(dir)
                .append("</direction><relatedStateVariable>").append(rel).append("</relatedStateVariable></argument>");
        }
    }

    // ------------------------------------------------------------------ SOAP

    public static final class Soap {
        public final String action;
        public final Map<String, String> args;
        Soap(String a, Map<String, String> m) { action = a; args = m; }
        public String arg(String name) { String v = args.get(name); return v == null ? "" : v; }
    }

    private static DocumentBuilder builder() throws Exception {
        DocumentBuilderFactory f = DocumentBuilderFactory.newInstance();
        f.setNamespaceAware(true);
        try { f.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true); } catch (Exception e) { /* not on Android */ }
        try { f.setExpandEntityReferences(false); } catch (Exception e) { /* ignore */ }
        return f.newDocumentBuilder();
    }

    private static String local(Node n) {
        String l = n.getLocalName();
        if (l != null) return l;
        String s = n.getNodeName();
        int c = s.indexOf(':');
        return c < 0 ? s : s.substring(c + 1);
    }

    /** Reads the action name and its arguments out of a SOAP envelope; null if it isn't one. */
    public static Soap parseSoap(String body) {
        try {
            Document d = builder().parse(new InputSource(new StringReader(body)));
            Element root = d.getDocumentElement();
            if (root == null || !local(root).equals("Envelope")) return null;
            Node bodyNode = null;
            for (Node c = root.getFirstChild(); c != null; c = c.getNextSibling()) {
                if (c.getNodeType() == Node.ELEMENT_NODE && local(c).equals("Body")) bodyNode = c;
            }
            if (bodyNode == null) return null;
            for (Node c = bodyNode.getFirstChild(); c != null; c = c.getNextSibling()) {
                if (c.getNodeType() != Node.ELEMENT_NODE) continue;
                Map<String, String> args = new LinkedHashMap<>();
                for (Node a = c.getFirstChild(); a != null; a = a.getNextSibling()) {
                    if (a.getNodeType() == Node.ELEMENT_NODE) args.put(local(a), a.getTextContent());
                }
                return new Soap(local(c), args);
            }
        } catch (Exception e) {
            return null;
        }
        return null;
    }

    public static String soapResponse(String service, String action, String... nameValue) {
        StringBuilder b = new StringBuilder(512);
        b.append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
            .append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
            .append("<s:Body><u:").append(action).append("Response xmlns:u=\"").append(serviceType(service)).append("\">");
        for (int i = 0; i + 1 < nameValue.length; i += 2) {
            b.append('<').append(nameValue[i]).append('>').append(esc(nameValue[i + 1])).append("</").append(nameValue[i]).append('>');
        }
        return b.append("</u:").append(action).append("Response></s:Body></s:Envelope>").toString();
    }

    public static String soapFault(int code, String text) {
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
            + "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>"
            + "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + code + "</errorCode><errorDescription>"
            + esc(text) + "</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
    }

    // ------------------------------------------------------------------ DIDL-Lite and time helpers

    private static final Pattern TITLE = Pattern.compile("<(?:\\w+:)?title[^>]*>([^<]*)</(?:\\w+:)?title>", Pattern.CASE_INSENSITIVE);
    private static final Pattern CLASS = Pattern.compile("<(?:\\w+:)?class[^>]*>([^<]*)</(?:\\w+:)?class>", Pattern.CASE_INSENSITIVE);
    private static final Pattern PROTO = Pattern.compile("protocolInfo\\s*=\\s*\"[^\"]*?:[^\"]*?:([^:\"]+/[^:\"]+):", Pattern.CASE_INSENSITIVE);

    /** The title in DIDL-Lite metadata, or "" when there is none. */
    public static String didlTitle(String didl) {
        if (didl == null) return "";
        Matcher m = TITLE.matcher(didl);
        return m.find() ? unesc(m.group(1)).trim() : "";
    }

    /** "video", "audio" or "image": from the DIDL class, then the declared MIME type, then the file extension. */
    public static String mediaKind(String didl, String uri) {
        if (didl != null) {
            Matcher m = CLASS.matcher(didl);
            if (m.find()) {
                String c = m.group(1).toLowerCase(Locale.ROOT);
                if (c.contains("imageitem") || c.contains("photo")) return "image";
                if (c.contains("audioitem") || c.contains("musictrack")) return "audio";
                if (c.contains("videoitem") || c.contains("movie")) return "video";
            }
            Matcher p = PROTO.matcher(didl);
            if (p.find()) {
                String mime = p.group(1).toLowerCase(Locale.ROOT);
                if (mime.startsWith("image/")) return "image";
                if (mime.startsWith("audio/")) return "audio";
                if (mime.startsWith("video/")) return "video";
            }
        }
        String u = uri == null ? "" : uri.toLowerCase(Locale.ROOT);
        int q = u.indexOf('?');
        if (q >= 0) u = u.substring(0, q);
        for (String e : new String[]{".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"}) if (u.endsWith(e)) return "image";
        for (String e : new String[]{".mp3", ".m4a", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav"}) if (u.endsWith(e)) return "audio";
        return "video";
    }

    public static String unesc(String s) {
        return s.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&apos;", "'").replace("&amp;", "&");
    }

    /** Milliseconds as H:MM:SS (the UPnP time format). */
    public static String fmt(long ms) {
        if (ms < 0) ms = 0;
        long s = ms / 1000;
        return String.format(Locale.US, "%d:%02d:%02d", s / 3600, (s / 60) % 60, s % 60);
    }

    /** "H:MM:SS" or "H:MM:SS.fff" to milliseconds; -1 when it can't be read. */
    public static long parseTime(String t) {
        if (t == null) return -1;
        String[] p = t.trim().split(":");
        if (p.length != 3) return -1;
        try {
            long h = Long.parseLong(p[0].replace("+", ""));
            long m = Long.parseLong(p[1]);
            double s = Double.parseDouble(p[2]);
            return (long) ((h * 3600 + m * 60 + s) * 1000);
        } catch (NumberFormatException e) {
            return -1;
        }
    }

    public static List<String> serviceNames() {
        List<String> l = new ArrayList<>();
        for (String s : SERVICES) l.add(s);
        return l;
    }
}
