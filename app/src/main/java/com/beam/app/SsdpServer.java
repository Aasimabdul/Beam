package com.beam.app;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.MulticastSocket;
import java.net.NetworkInterface;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.Random;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.TimeUnit;

/** Announces the renderer on the LAN (SSDP) and answers M-SEARCH. Call restart() when the network changes. */
public final class SsdpServer {
    private final String uuid;
    private final int httpPort;
    private final Random rnd = new Random();
    private final ScheduledThreadPoolExecutor timer;
    private MulticastSocket socket;
    private Thread reader;
    private volatile boolean running;
    private ScheduledFuture<?> refresh;

    public SsdpServer(String uuid, int httpPort) {
        this.uuid = uuid;
        this.httpPort = httpPort;
        timer = new ScheduledThreadPoolExecutor(1, new ThreadFactory() {
            @Override public Thread newThread(Runnable r) {
                Thread t = new Thread(null, r, "beam-ssdp-timer", 128 * 1024);
                t.setDaemon(true);
                return t;
            }
        });
        timer.setRemoveOnCancelPolicy(true);
    }

    public synchronized void start() throws IOException {
        if (running) return;
        MulticastSocket s = new MulticastSocket(null);
        s.setReuseAddress(true);
        s.bind(new InetSocketAddress(SsdpMsg.PORT));
        s.setTimeToLive(2);
        InetAddress group = InetAddress.getByName(SsdpMsg.GROUP);
        boolean joined = false;
        for (NetworkInterface ni : Net.lanInterfaces()) {
            try {
                s.joinGroup(new InetSocketAddress(group, SsdpMsg.PORT), ni);
                joined = true;
            } catch (IOException e) {
                // this interface can't do multicast; try the others
            }
        }
        if (!joined) {
            try { s.joinGroup(group); } catch (IOException e) { s.close(); throw e; }
        }
        socket = s;
        running = true;
        reader = new Thread(null, new Runnable() {
            @Override public void run() { readLoop(s); }
        }, "beam-ssdp", 128 * 1024);
        reader.setDaemon(true);
        reader.start();
        // announce three times (UDP can be lost), then refresh well before max-age runs out
        for (int i = 0; i < 3; i++) timer.schedule(new Runnable() {
            @Override public void run() { announce(true); }
        }, i * 400L, TimeUnit.MILLISECONDS);
        refresh = timer.scheduleWithFixedDelay(new Runnable() {
            @Override public void run() { announce(true); }
        }, 15, 15, TimeUnit.MINUTES);
    }

    /** Leaves the network politely (byebye) and closes the socket. */
    public synchronized void stop() {
        if (!running) return;
        announce(false);
        running = false;
        if (socket != null) socket.close();
        socket = null;
        if (refresh != null) refresh.cancel(false);
        refresh = null;
    }

    public synchronized void restart() throws IOException {
        if (running) {
            running = false;
            if (socket != null) socket.close();
            socket = null;
            if (refresh != null) refresh.cancel(false);
            refresh = null;
        }
        start();
    }

    public void shutdown() {
        stop();
        timer.shutdownNow();
    }

    private void readLoop(MulticastSocket s) {
        byte[] buf = new byte[2048];
        while (running && !s.isClosed()) {
            try {
                DatagramPacket p = new DatagramPacket(buf, buf.length);
                s.receive(p);
                String text = new String(p.getData(), 0, p.getLength(), StandardCharsets.UTF_8);
                final SsdpMsg.Search q = SsdpMsg.parseSearch(text);
                if (q == null) continue;
                final List<String> answers = SsdpMsg.answers(uuid, q.st);
                if (answers.isEmpty()) continue;
                final InetAddress to = p.getAddress();
                final int port = p.getPort();
                final String ip = Net.localIpFor(to);
                if (ip == null) continue;
                final String location = "http://" + ip + ":" + httpPort + "/desc.xml";
                int delay = rnd.nextInt(Math.max(1, Math.min(q.mx, 2)) * 1000);
                for (final String nt : answers) {
                    timer.schedule(new Runnable() {
                        @Override public void run() { send(SsdpMsg.searchResponse(uuid, nt, location), to, port, null); }
                    }, delay, TimeUnit.MILLISECONDS);
                }
            } catch (IOException e) {
                if (!running || s.isClosed()) return;
            } catch (RuntimeException e) {
                if (!running || s.isClosed()) return;
            }
        }
    }

    private void announce(boolean alive) {
        InetAddress group;
        try {
            group = InetAddress.getByName(SsdpMsg.GROUP);
        } catch (IOException e) {
            return;
        }
        for (NetworkInterface ni : Net.lanInterfaces()) {
            java.net.InterfaceAddress ia = Net.ipv4(ni);
            if (ia == null) continue;
            String location = "http://" + ia.getAddress().getHostAddress() + ":" + httpPort + "/desc.xml";
            for (String nt : SsdpMsg.targets(uuid)) {
                send(alive ? SsdpMsg.alive(uuid, nt, location) : SsdpMsg.byebye(uuid, nt), group, SsdpMsg.PORT, ni);
            }
        }
    }

    private synchronized void send(String msg, InetAddress to, int port, NetworkInterface via) {
        MulticastSocket s = socket;
        if (s == null) return;
        try {
            if (via != null) s.setNetworkInterface(via);
            byte[] b = msg.getBytes(StandardCharsets.UTF_8);
            s.send(new DatagramPacket(b, b.length, to, port));
        } catch (IOException e) {
            // network went away; the next restart() fixes it
        }
    }
}
