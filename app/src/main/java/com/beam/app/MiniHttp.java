package com.beam.app;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.ThreadFactory;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** A tiny HTTP/1.1 server (one request per connection) for UPnP descriptions, SOAP control and event subscriptions. */
public final class MiniHttp {
    public interface Handler {
        Response handle(Request r) throws Exception;
    }

    public static final class Request {
        public String method = "";
        public String path = "";
        public final Map<String, String> headers = new HashMap<>();
        public byte[] body = new byte[0];
        public InetAddress peer;

        public String header(String name) { return headers.get(name.toLowerCase(Locale.ROOT)); }
        public String bodyText() { return new String(body, StandardCharsets.UTF_8); }
    }

    public static final class Response {
        public int code;
        public String message;
        public final Map<String, String> headers = new LinkedHashMap<>();
        public byte[] body = new byte[0];

        public Response(int code, String message) { this.code = code; this.message = message; }

        public static Response xml(int code, String text) {
            Response r = new Response(code, code == 200 ? "OK" : "Internal Server Error");
            r.headers.put("CONTENT-TYPE", "text/xml; charset=\"utf-8\"");
            r.body = text.getBytes(StandardCharsets.UTF_8);
            return r;
        }

        public static Response empty(int code, String message) { return new Response(code, message); }
    }

    private static final int MAX_BODY = 1024 * 1024;
    private final Handler handler;
    private ServerSocket server;
    private ThreadPoolExecutor pool;
    private volatile boolean running;

    public MiniHttp(Handler handler) { this.handler = handler; }

    /** Starts listening on [port] (0 = any free port) and returns the port in use. */
    public synchronized int start(int port) throws IOException {
        ServerSocket ss = new ServerSocket();
        ss.setReuseAddress(true);
        ss.bind(new InetSocketAddress(port));
        server = ss;
        running = true;
        pool = new ThreadPoolExecutor(0, 6, 20, TimeUnit.SECONDS, new SynchronousQueue<Runnable>(), new ThreadFactory() {
            private int n;
            @Override public synchronized Thread newThread(Runnable r) {
                Thread t = new Thread(null, r, "beam-http-" + (++n), 256 * 1024);
                t.setDaemon(true);
                return t;
            }
        }, new ThreadPoolExecutor.CallerRunsPolicy());
        Thread acceptor = new Thread(null, new Runnable() {
            @Override public void run() { acceptLoop(ss); }
        }, "beam-accept", 128 * 1024);
        acceptor.setDaemon(true);
        acceptor.start();
        return ss.getLocalPort();
    }

    public synchronized void stop() {
        running = false;
        try { if (server != null) server.close(); } catch (IOException e) { /* ignore */ }
        if (pool != null) pool.shutdownNow();
        server = null;
        pool = null;
    }

    private void acceptLoop(ServerSocket ss) {
        while (running) {
            try {
                final Socket s = ss.accept();
                pool.execute(new Runnable() {
                    @Override public void run() { serve(s); }
                });
            } catch (IOException e) {
                if (!running) return;
            } catch (RuntimeException e) {
                return;
            }
        }
    }

    private void serve(Socket s) {
        try {
            s.setSoTimeout(10_000);
            InputStream in = s.getInputStream();
            OutputStream out = s.getOutputStream();
            Request req = read(in, out);
            if (req == null) return;
            req.peer = s.getInetAddress();
            Response res;
            try {
                res = handler.handle(req);
            } catch (Exception e) {
                res = Response.empty(500, "Internal Server Error");
            }
            if (res == null) res = Response.empty(404, "Not Found");
            write(out, res, req.method.equals("HEAD"));
        } catch (IOException e) {
            // client went away
        } finally {
            try { s.close(); } catch (IOException e) { /* ignore */ }
        }
    }

    private static String line(InputStream in) throws IOException {
        ByteArrayOutputStream b = new ByteArrayOutputStream(64);
        int c;
        while ((c = in.read()) >= 0) {
            if (c == '\n') break;
            if (c != '\r') b.write(c);
            if (b.size() > 16 * 1024) throw new IOException("line too long");
        }
        if (c < 0 && b.size() == 0) return null;
        return new String(b.toByteArray(), StandardCharsets.ISO_8859_1);
    }

    static Request read(InputStream in, OutputStream out) throws IOException {
        String first = line(in);
        if (first == null || first.isEmpty()) return null;
        String[] p = first.split(" ");
        if (p.length < 2) return null;
        Request r = new Request();
        r.method = p[0].toUpperCase(Locale.ROOT);
        r.path = p[1];
        int q = r.path.indexOf('?');
        if (q >= 0) r.path = r.path.substring(0, q);
        String h;
        int count = 0;
        while ((h = line(in)) != null && !h.isEmpty()) {
            if (++count > 100) throw new IOException("too many headers");
            int c = h.indexOf(':');
            if (c > 0) r.headers.put(h.substring(0, c).trim().toLowerCase(Locale.ROOT), h.substring(c + 1).trim());
        }
        String expect = r.header("expect");
        if (expect != null && expect.toLowerCase(Locale.ROOT).contains("100-continue")) {
            out.write("HTTP/1.1 100 Continue\r\n\r\n".getBytes(StandardCharsets.ISO_8859_1));
            out.flush();
        }
        String te = r.header("transfer-encoding");
        if (te != null && te.toLowerCase(Locale.ROOT).contains("chunked")) {
            ByteArrayOutputStream b = new ByteArrayOutputStream();
            while (true) {
                String sz = line(in);
                if (sz == null) throw new IOException("bad chunk");
                int semi = sz.indexOf(';');
                if (semi >= 0) sz = sz.substring(0, semi);
                int n;
                try { n = Integer.parseInt(sz.trim(), 16); } catch (NumberFormatException e) { throw new IOException("bad chunk size"); }
                if (n == 0) {
                    while ((h = line(in)) != null && !h.isEmpty()) { /* trailers */ }
                    break;
                }
                if (b.size() + n > MAX_BODY) throw new IOException("body too large");
                copy(in, b, n);
                line(in);
            }
            r.body = b.toByteArray();
        } else {
            String cl = r.header("content-length");
            int n = 0;
            if (cl != null) {
                try { n = Integer.parseInt(cl); } catch (NumberFormatException e) { throw new IOException("bad length"); }
            }
            if (n < 0 || n > MAX_BODY) throw new IOException("body too large");
            ByteArrayOutputStream b = new ByteArrayOutputStream(Math.max(16, n));
            copy(in, b, n);
            r.body = b.toByteArray();
        }
        return r;
    }

    private static void copy(InputStream in, ByteArrayOutputStream out, int n) throws IOException {
        byte[] buf = new byte[4096];
        while (n > 0) {
            int r = in.read(buf, 0, Math.min(buf.length, n));
            if (r < 0) throw new IOException("short body");
            out.write(buf, 0, r);
            n -= r;
        }
    }

    private static void write(OutputStream out, Response r, boolean headOnly) throws IOException {
        StringBuilder b = new StringBuilder(256);
        b.append("HTTP/1.1 ").append(r.code).append(' ').append(r.message).append("\r\n");
        b.append("SERVER: ").append(SsdpMsg.SERVER).append("\r\n");
        for (Map.Entry<String, String> e : r.headers.entrySet()) b.append(e.getKey()).append(": ").append(e.getValue()).append("\r\n");
        b.append("CONTENT-LENGTH: ").append(r.body.length).append("\r\nCONNECTION: close\r\n\r\n");
        out.write(b.toString().getBytes(StandardCharsets.ISO_8859_1));
        if (!headOnly) out.write(r.body);
        out.flush();
    }
}
