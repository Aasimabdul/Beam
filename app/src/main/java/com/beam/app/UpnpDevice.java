package com.beam.app;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.Iterator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * The MediaRenderer: answers description / control / event requests and keeps the transport state.
 * It talks to the real player through [Player]; the player reports back with the on... methods.
 * Plain Java so the whole protocol can be tested on a JVM.
 */
public final class UpnpDevice implements MiniHttp.Handler {

    /** The playback side. Methods must return quickly (they are called from HTTP threads). */
    public interface Player {
        void load(String uri, String meta, String kind, String title);
        void play();
        void pause();
        void stop();
        void seek(long ms);
        long positionMs();
        long durationMs();
        void setVolume(int percent);
        int volume();
        void setMute(boolean mute);
        boolean muted();
    }

    private static final class Fault extends Exception {
        final int code;
        Fault(int code, String text) {
            super(text);
            this.code = code;
        }
    }

    private static final class Sub {
        final String service;
        final List<String> callbacks;
        long expiry;
        int seq;
        Sub(String service, List<String> callbacks, long expiry) {
            this.service = service;
            this.callbacks = callbacks;
            this.expiry = expiry;
        }
    }

    private static final Pattern CALLBACK = Pattern.compile("<([^>]+)>");

    private final String uuid;
    private volatile String name;
    private final Player player;
    private final Map<String, Sub> subs = new HashMap<>();
    private final ScheduledThreadPoolExecutor notifier;

    private String state = "NO_MEDIA_PRESENT";
    private String uri = "";
    private String meta = "";
    private String nextUri = "";
    private String nextMeta = "";

    public UpnpDevice(String uuid, String name, Player player) {
        this.uuid = uuid;
        this.name = name;
        this.player = player;
        this.notifier = new ScheduledThreadPoolExecutor(1, new ThreadFactory() {
            @Override public Thread newThread(Runnable r) {
                Thread t = new Thread(null, r, "beam-notify", 128 * 1024);
                t.setDaemon(true);
                return t;
            }
        });
        notifier.setRemoveOnCancelPolicy(true);
    }

    public String uuid() { return uuid; }
    public void setName(String n) { name = n; }
    public synchronized String state() { return state; }
    public void shutdown() { notifier.shutdownNow(); }

    // ------------------------------------------------------------------ HTTP routing

    @Override
    public MiniHttp.Response handle(MiniHttp.Request r) {
        String p = r.path;
        boolean read = r.method.equals("GET") || r.method.equals("HEAD");
        if (read && p.equals("/desc.xml")) return MiniHttp.Response.xml(200, Upnp.deviceXml(uuid, name));
        if (read && p.startsWith("/scpd/") && p.endsWith(".xml")) {
            String x = Upnp.scpd(p.substring(6, p.length() - 4));
            return x == null ? null : MiniHttp.Response.xml(200, x);
        }
        if (p.startsWith("/ctl/") && r.method.equals("POST")) return control(p.substring(5), r);
        if (p.startsWith("/evt/")) return event(p.substring(5), r);
        return null;
    }

    private static boolean isService(String s) {
        for (String n : Upnp.SERVICES) if (n.equals(s)) return true;
        return false;
    }

    private MiniHttp.Response fault(int code, String text) {
        return MiniHttp.Response.xml(500, Upnp.soapFault(code, text));
    }

    private MiniHttp.Response control(String svc, MiniHttp.Request r) {
        if (!isService(svc)) return null;
        Upnp.Soap s = Upnp.parseSoap(r.bodyText());
        if (s == null) return fault(401, "Invalid Action");
        try {
            String out;
            switch (svc) {
                case "AVTransport": out = avTransport(s); break;
                case "RenderingControl": out = rendering(s); break;
                default: out = connection(s); break;
            }
            if (out == null) return fault(401, "Invalid Action");
            return MiniHttp.Response.xml(200, out);
        } catch (Fault f) {
            return fault(f.code, f.getMessage());
        }
    }

    // ------------------------------------------------------------------ AVTransport

    private String actions() {
        switch (state) {
            case "PLAYING": return "Pause,Stop,Seek";
            case "PAUSED_PLAYBACK": return "Play,Stop,Seek";
            case "STOPPED": return "Play,Seek";
            default: return "";
        }
    }

    private String avTransport(Upnp.Soap s) throws Fault {
        final String a = s.action;
        final String svc = "AVTransport";
        switch (a) {
            case "SetAVTransportURI":
                setUri(s.arg("CurrentURI").trim(), s.arg("CurrentURIMetaData"));
                return Upnp.soapResponse(svc, a);
            case "SetNextAVTransportURI":
                synchronized (this) {
                    nextUri = s.arg("NextURI").trim();
                    nextMeta = s.arg("NextURIMetaData");
                }
                return Upnp.soapResponse(svc, a);
            case "GetMediaInfo": {
                String u, m, nu, nm;
                synchronized (this) { u = uri; m = meta; nu = nextUri; nm = nextMeta; }
                return Upnp.soapResponse(svc, a, "NrTracks", u.isEmpty() ? "0" : "1", "MediaDuration", Upnp.fmt(player.durationMs()),
                    "CurrentURI", u, "CurrentURIMetaData", m, "NextURI", nu, "NextURIMetaData", nm,
                    "PlayMedium", "NETWORK", "RecordMedium", "NOT_IMPLEMENTED", "WriteStatus", "NOT_IMPLEMENTED");
            }
            case "GetTransportInfo":
                return Upnp.soapResponse(svc, a, "CurrentTransportState", state(), "CurrentTransportStatus", "OK", "CurrentSpeed", "1");
            case "GetPositionInfo": {
                String u, m;
                synchronized (this) { u = uri; m = meta; }
                String pos = Upnp.fmt(player.positionMs());
                return Upnp.soapResponse(svc, a, "Track", u.isEmpty() ? "0" : "1", "TrackDuration", Upnp.fmt(player.durationMs()),
                    "TrackMetaData", m, "TrackURI", u, "RelTime", pos, "AbsTime", pos,
                    "RelCount", "2147483647", "AbsCount", "2147483647");
            }
            case "GetDeviceCapabilities":
                return Upnp.soapResponse(svc, a, "PlayMedia", "NETWORK", "RecMedia", "NOT_IMPLEMENTED", "RecQualityModes", "NOT_IMPLEMENTED");
            case "GetTransportSettings":
                return Upnp.soapResponse(svc, a, "PlayMode", "NORMAL", "RecQualityMode", "NOT_IMPLEMENTED");
            case "GetCurrentTransportActions":
                synchronized (this) { return Upnp.soapResponse(svc, a, "Actions", actions()); }
            case "Play":
                synchronized (this) {
                    if (uri.isEmpty()) throw new Fault(701, "Transition not available");
                    player.play();
                    changeState("PLAYING");
                }
                return Upnp.soapResponse(svc, a);
            case "Pause":
                synchronized (this) {
                    if (uri.isEmpty()) throw new Fault(701, "Transition not available");
                    player.pause();
                    changeState("PAUSED_PLAYBACK");
                }
                return Upnp.soapResponse(svc, a);
            case "Stop":
                synchronized (this) {
                    player.stop();
                    changeState(uri.isEmpty() ? "NO_MEDIA_PRESENT" : "STOPPED");
                }
                return Upnp.soapResponse(svc, a);
            case "Seek": {
                String unit = s.arg("Unit");
                String target = s.arg("Target");
                if (unit.equals("REL_TIME") || unit.equals("ABS_TIME")) {
                    long ms = Upnp.parseTime(target);
                    if (ms < 0) throw new Fault(711, "Illegal seek target");
                    player.seek(ms);
                } else if (unit.equals("TRACK_NR")) {
                    player.seek(0);
                } else {
                    throw new Fault(710, "Seek mode not supported");
                }
                return Upnp.soapResponse(svc, a);
            }
            case "Next":
            case "Previous":
                throw new Fault(701, "Transition not available");
            default:
                return null;
        }
    }

    private void setUri(String u, String m) {
        synchronized (this) {
            uri = u;
            meta = m;
            if (u.isEmpty()) {
                player.stop();
                state = "NO_MEDIA_PRESENT";
            } else {
                player.load(u, m, Upnp.mediaKind(m, u), Upnp.didlTitle(m));
                state = "STOPPED";
            }
            sendEvent("AVTransport", true);
        }
    }

    // ------------------------------------------------------------------ RenderingControl / ConnectionManager

    private String rendering(Upnp.Soap s) {
        final String svc = "RenderingControl";
        switch (s.action) {
            case "ListPresets": return Upnp.soapResponse(svc, s.action, "CurrentPresetNameList", "FactoryDefaults");
            case "SelectPreset": return Upnp.soapResponse(svc, s.action);
            case "GetVolume": return Upnp.soapResponse(svc, s.action, "CurrentVolume", String.valueOf(player.volume()));
            case "SetVolume": {
                int v;
                try { v = Integer.parseInt(s.arg("DesiredVolume").trim()); } catch (NumberFormatException e) { v = 0; }
                player.setVolume(Math.max(0, Math.min(100, v)));
                synchronized (this) { sendEvent("RenderingControl", false); }
                return Upnp.soapResponse(svc, s.action);
            }
            case "GetMute": return Upnp.soapResponse(svc, s.action, "CurrentMute", player.muted() ? "1" : "0");
            case "SetMute": {
                String m = s.arg("DesiredMute").trim();
                player.setMute(m.equals("1") || m.equalsIgnoreCase("true"));
                synchronized (this) { sendEvent("RenderingControl", false); }
                return Upnp.soapResponse(svc, s.action);
            }
            default: return null;
        }
    }

    private String connection(Upnp.Soap s) {
        final String svc = "ConnectionManager";
        switch (s.action) {
            case "GetProtocolInfo": return Upnp.soapResponse(svc, s.action, "Source", "", "Sink", Upnp.SINK);
            case "GetCurrentConnectionIDs": return Upnp.soapResponse(svc, s.action, "ConnectionIDs", "0");
            case "GetCurrentConnectionInfo":
                return Upnp.soapResponse(svc, s.action, "RcsID", "0", "AVTransportID", "0", "ProtocolInfo", "",
                    "PeerConnectionManager", "", "PeerConnectionID", "-1", "Direction", "Input", "Status", "OK");
            default: return null;
        }
    }

    // ------------------------------------------------------------------ reports from the player

    private void changeState(String s) {
        if (s.equals(state)) return;
        state = s;
        sendEvent("AVTransport", false);
    }

    /** The player's state changed by itself (finished, paused by the user on the TV, error...). */
    public synchronized void onPlayerState(String s) {
        changeState(s);
    }

    /** Duration became known: tell subscribers the full state again. */
    public synchronized void onMediaReady() {
        sendEvent("AVTransport", true);
    }

    // ------------------------------------------------------------------ eventing (GENA)

    private MiniHttp.Response event(String svc, MiniHttp.Request r) {
        if (!isService(svc)) return null;
        long now = System.currentTimeMillis();
        switch (r.method) {
            case "SUBSCRIBE": {
                long seconds = 1800;
                String t = r.header("timeout");
                if (t != null && t.toLowerCase(Locale.ROOT).startsWith("second-")) {
                    try { seconds = Math.min(1800, Math.max(60, Long.parseLong(t.substring(7).trim()))); } catch (NumberFormatException e) { /* default */ }
                }
                String sid = r.header("sid");
                synchronized (this) {
                    purge(now);
                    if (sid != null) {
                        Sub s = subs.get(sid);
                        if (s == null || !s.service.equals(svc)) return MiniHttp.Response.empty(412, "Precondition Failed");
                        s.expiry = now + seconds * 1000;
                        return subscribed(sid, seconds);
                    }
                    String cb = r.header("callback");
                    if (cb == null || !"upnp:event".equalsIgnoreCase(r.header("nt"))) return MiniHttp.Response.empty(412, "Precondition Failed");
                    List<String> urls = new ArrayList<>();
                    Matcher m = CALLBACK.matcher(cb);
                    while (m.find()) if (m.group(1).startsWith("http://")) urls.add(m.group(1));
                    if (urls.isEmpty() || subs.size() >= 16) return MiniHttp.Response.empty(412, "Precondition Failed");
                    final String id = "uuid:" + UUID.randomUUID();
                    final Sub sub = new Sub(svc, urls, now + seconds * 1000);
                    subs.put(id, sub);
                    // the first event must arrive after this reply has been sent
                    try {
                        notifier.schedule(new Runnable() {
                            @Override public void run() { sendInitial(id, sub); }
                        }, 250, TimeUnit.MILLISECONDS);
                    } catch (RuntimeException e) { /* shut down */ }
                    return subscribed(id, seconds);
                }
            }
            case "UNSUBSCRIBE": {
                String sid = r.header("sid");
                synchronized (this) {
                    if (sid != null && subs.remove(sid) != null) return MiniHttp.Response.empty(200, "OK");
                }
                return MiniHttp.Response.empty(412, "Precondition Failed");
            }
            default:
                return MiniHttp.Response.empty(405, "Method Not Allowed");
        }
    }

    private MiniHttp.Response subscribed(String sid, long seconds) {
        MiniHttp.Response r = MiniHttp.Response.empty(200, "OK");
        r.headers.put("SID", sid);
        r.headers.put("TIMEOUT", "Second-" + seconds);
        return r;
    }

    private void purge(long now) {
        for (Iterator<Sub> it = subs.values().iterator(); it.hasNext(); ) {
            if (it.next().expiry < now) it.remove();
        }
    }

    private static String v(String n, String val) {
        return "<" + n + " val=\"" + Upnp.esc(val) + "\"/>";
    }

    /** The evented "LastChange" (or property set) for [svc]. Caller holds the lock. */
    private String propertySet(String svc, boolean full) {
        StringBuilder b = new StringBuilder(512);
        b.append("<?xml version=\"1.0\"?><e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">");
        switch (svc) {
            case "AVTransport": {
                StringBuilder e = new StringBuilder(512);
                e.append("<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/AVT/\"><InstanceID val=\"0\">")
                    .append(v("TransportState", state)).append(v("TransportStatus", "OK"))
                    .append(v("CurrentTransportActions", actions()));
                if (full) {
                    String dur = Upnp.fmt(player.durationMs());
                    e.append(v("NumberOfTracks", uri.isEmpty() ? "0" : "1")).append(v("CurrentTrack", uri.isEmpty() ? "0" : "1"))
                        .append(v("AVTransportURI", uri)).append(v("AVTransportURIMetaData", meta))
                        .append(v("CurrentTrackURI", uri)).append(v("CurrentTrackMetaData", meta))
                        .append(v("CurrentTrackDuration", dur)).append(v("CurrentMediaDuration", dur))
                        .append(v("PlaybackStorageMedium", "NETWORK")).append(v("CurrentPlayMode", "NORMAL"))
                        .append(v("TransportPlaySpeed", "1"));
                }
                e.append("</InstanceID></Event>");
                b.append("<e:property><LastChange>").append(Upnp.esc(e.toString())).append("</LastChange></e:property>");
                break;
            }
            case "RenderingControl": {
                String e = "<Event xmlns=\"urn:schemas-upnp-org:metadata-1-0/RCS/\"><InstanceID val=\"0\">"
                    + "<Volume channel=\"Master\" val=\"" + player.volume() + "\"/>"
                    + "<Mute channel=\"Master\" val=\"" + (player.muted() ? "1" : "0") + "\"/></InstanceID></Event>";
                b.append("<e:property><LastChange>").append(Upnp.esc(e)).append("</LastChange></e:property>");
                break;
            }
            default:
                b.append("<e:property><SourceProtocolInfo></SourceProtocolInfo></e:property>")
                    .append("<e:property><SinkProtocolInfo>").append(Upnp.esc(Upnp.SINK)).append("</SinkProtocolInfo></e:property>")
                    .append("<e:property><CurrentConnectionIDs>0</CurrentConnectionIDs></e:property>");
        }
        return b.append("</e:propertyset>").toString();
    }

    private void sendInitial(String sid, Sub sub) {
        String body;
        int seq;
        synchronized (this) {
            if (!subs.containsKey(sid)) return;
            body = propertySet(sub.service, true);
            seq = sub.seq++;
        }
        if (!deliver(sub, sid, seq, body)) {
            synchronized (this) { subs.remove(sid); }
        }
    }

    /** Queues one event to every subscriber of [svc]. Caller holds the lock. */
    private void sendEvent(String svc, boolean full) {
        if (subs.isEmpty()) return;
        long now = System.currentTimeMillis();
        purge(now);
        final String body = propertySet(svc, full);
        for (Map.Entry<String, Sub> e : new ArrayList<>(subs.entrySet())) {
            final Sub sub = e.getValue();
            if (!sub.service.equals(svc)) continue;
            final String sid = e.getKey();
            final int seq = sub.seq++;
            try {
                notifier.execute(new Runnable() {
                    @Override public void run() {
                        if (!deliver(sub, sid, seq, body)) {
                            synchronized (UpnpDevice.this) { subs.remove(sid); }
                        }
                    }
                });
            } catch (RuntimeException ex) { /* shut down */ }
        }
    }

    private boolean deliver(Sub sub, String sid, int seq, String body) {
        byte[] payload = body.getBytes(StandardCharsets.UTF_8);
        for (String cb : sub.callbacks) {
            try {
                URL u = new URL(cb);
                int port = u.getPort() > 0 ? u.getPort() : 80;
                String path = u.getFile().isEmpty() ? "/" : u.getFile();
                try (Socket s = new Socket()) {
                    s.connect(new InetSocketAddress(u.getHost(), port), 3000);
                    s.setSoTimeout(3000);
                    String head = "NOTIFY " + path + " HTTP/1.1\r\nHOST: " + u.getHost() + ":" + port
                        + "\r\nCONTENT-TYPE: text/xml; charset=\"utf-8\"\r\nNT: upnp:event\r\nNTS: upnp:propchange\r\nSID: " + sid
                        + "\r\nSEQ: " + seq + "\r\nCONTENT-LENGTH: " + payload.length + "\r\nCONNECTION: close\r\n\r\n";
                    OutputStream out = s.getOutputStream();
                    out.write(head.getBytes(StandardCharsets.ISO_8859_1));
                    out.write(payload);
                    out.flush();
                    InputStream in = s.getInputStream();
                    byte[] buf = new byte[32];
                    int n = in.read(buf);
                    if (n > 0) return true;
                }
            } catch (IOException | RuntimeException e) {
                // try the next callback URL
            }
        }
        return false;
    }
}
