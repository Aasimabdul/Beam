package com.beam.app;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

/** SSDP message building and parsing (plain Java). */
public final class SsdpMsg {
    private SsdpMsg() { }

    public static final String GROUP = "239.255.255.250";
    public static final int PORT = 1900;
    public static final String SERVER = "Android UPnP/1.0 Beam/1";
    public static final int MAX_AGE = 1800;

    /** Every notification type the device announces. */
    public static List<String> targets(String uuid) {
        List<String> l = new ArrayList<>();
        l.add("upnp:rootdevice");
        l.add("uuid:" + uuid);
        l.add(Upnp.DEVICE);
        for (String s : Upnp.SERVICES) l.add(Upnp.serviceType(s));
        return l;
    }

    public static String usn(String uuid, String nt) {
        return nt.equals("uuid:" + uuid) ? nt : "uuid:" + uuid + "::" + nt;
    }

    public static String alive(String uuid, String nt, String location) {
        return "NOTIFY * HTTP/1.1\r\nHOST: " + GROUP + ":" + PORT + "\r\nCACHE-CONTROL: max-age=" + MAX_AGE
            + "\r\nLOCATION: " + location + "\r\nNT: " + nt + "\r\nNTS: ssdp:alive\r\nSERVER: " + SERVER
            + "\r\nUSN: " + usn(uuid, nt) + "\r\n\r\n";
    }

    public static String byebye(String uuid, String nt) {
        return "NOTIFY * HTTP/1.1\r\nHOST: " + GROUP + ":" + PORT + "\r\nNT: " + nt
            + "\r\nNTS: ssdp:byebye\r\nUSN: " + usn(uuid, nt) + "\r\n\r\n";
    }

    public static String searchResponse(String uuid, String st, String location) {
        return "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=" + MAX_AGE + "\r\nEXT:\r\nLOCATION: " + location
            + "\r\nSERVER: " + SERVER + "\r\nST: " + st + "\r\nUSN: " + usn(uuid, st) + "\r\n\r\n";
    }

    /** A parsed M-SEARCH request. */
    public static final class Search {
        public final String st;
        public final int mx;
        Search(String st, int mx) { this.st = st; this.mx = mx; }
    }

    public static String header(String packet, String name) {
        String key = name.toLowerCase(Locale.ROOT) + ":";
        for (String line : packet.split("\r\n|\n")) {
            if (line.toLowerCase(Locale.ROOT).startsWith(key)) return line.substring(key.length()).trim();
        }
        return null;
    }

    /** Returns the search target when [packet] is an ssdp:discover M-SEARCH, otherwise null. */
    public static Search parseSearch(String packet) {
        if (packet == null || !packet.regionMatches(true, 0, "M-SEARCH", 0, 8)) return null;
        String man = header(packet, "MAN");
        String st = header(packet, "ST");
        if (man == null || st == null || !man.replace("\"", "").equalsIgnoreCase("ssdp:discover")) return null;
        int mx = 1;
        try {
            String m = header(packet, "MX");
            if (m != null) mx = Math.max(1, Math.min(5, Integer.parseInt(m)));
        } catch (NumberFormatException e) {
            mx = 1;
        }
        return new Search(st, mx);
    }

    /** The notification types that should answer a search for [st] (empty if none). */
    public static List<String> answers(String uuid, String st) {
        List<String> all = targets(uuid);
        List<String> out = new ArrayList<>();
        if (st.equalsIgnoreCase("ssdp:all")) return all;
        for (String t : all) if (t.equalsIgnoreCase(st)) out.add(t);
        return out;
    }
}
