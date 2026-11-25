package com.beam.app;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/** Finds the phone's LAN addresses (plain Java). */
public final class Net {
    private Net() { }

    /** Up, non-loopback interfaces that have an IPv4 address and can do multicast. */
    public static List<NetworkInterface> lanInterfaces() {
        List<NetworkInterface> out = new ArrayList<>();
        try {
            for (NetworkInterface ni : Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback() || ni.isPointToPoint() || !ni.supportsMulticast()) continue;
                if (ipv4(ni) != null) out.add(ni);
            }
        } catch (Exception e) {
            // none
        }
        return out;
    }

    public static InterfaceAddress ipv4(NetworkInterface ni) {
        for (InterfaceAddress a : ni.getInterfaceAddresses()) {
            if (a.getAddress() instanceof Inet4Address) return a;
        }
        return null;
    }

    /** The local IPv4 address on the same subnet as [remote], else the first LAN address, else null. */
    public static String localIpFor(InetAddress remote) {
        String first = null;
        for (NetworkInterface ni : lanInterfaces()) {
            InterfaceAddress ia = ipv4(ni);
            if (ia == null) continue;
            if (first == null) first = ia.getAddress().getHostAddress();
            if (remote != null && sameSubnet(ia.getAddress().getAddress(), remote.getAddress(), ia.getNetworkPrefixLength())) {
                return ia.getAddress().getHostAddress();
            }
        }
        return first;
    }

    static boolean sameSubnet(byte[] a, byte[] b, int prefix) {
        if (a.length != b.length) return false;
        int bits = prefix;
        for (int i = 0; i < a.length && bits > 0; i++) {
            int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (b[i] & mask)) return false;
            bits -= 8;
        }
        return true;
    }
}
