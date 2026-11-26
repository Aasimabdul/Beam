#!/bin/bash
# Smoke test for a built Linux Beam binary: start it, cast a clip through the DLNA control interface, check the transport follows.
# usage: smoke-linux.sh /path/to/beam
set -u
BEAM=$1
W=$(mktemp -d)
export HOME=$W/home; mkdir -p "$HOME"
export BEAM_MPV_ARGS="--vo=null --ao=null --force-window=no"
fail=0
ok(){ if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: got '$2' want '$3'"; fail=1; fi; }

ffmpeg -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=25 -t 30 -pix_fmt yuv420p -c:v libx264 -preset ultrafast -movflags +faststart "$W/t.mp4"
ffmpeg -loglevel error -y -f lavfi -i testsrc=size=320x240:rate=25 -t 2 -pix_fmt yuv420p -c:v libx264 -preset ultrafast -movflags +faststart "$W/s.mp4"
python3 - "$W" > "$W/http.log" 2>&1 <<'PY' &
import sys, os, http.server, re
os.chdir(sys.argv[1])
class H(http.server.SimpleHTTPRequestHandler):
    def send_head(self):
        p = self.translate_path(self.path)
        if not os.path.isfile(p): return super().send_head()
        size = os.path.getsize(p); m = re.match(r'bytes=(\d+)-(\d*)', self.headers.get('Range', ''))
        f = open(p, 'rb')
        if m:
            a = int(m.group(1)); b = int(m.group(2)) if m.group(2) else size - 1; f.seek(a)
            self.send_response(206); self.send_header('Content-Range', f'bytes {a}-{b}/{size}'); self.send_header('Content-Length', str(b - a + 1)); self._left = b - a + 1
        else:
            self.send_response(200); self.send_header('Content-Length', str(size)); self._left = size
        self.send_header('Content-Type', 'video/mp4'); self.send_header('Accept-Ranges', 'bytes'); self.end_headers(); return f
    def copyfile(self, src, dst):
        left = self._left
        while left > 0:
            d = src.read(min(65536, left))
            if not d: break
            dst.write(d); left -= len(d)
    def log_message(self, *a): pass
http.server.ThreadingHTTPServer(('127.0.0.1', 8099), H).serve_forever()
PY
HTTP_PID=$!
"$BEAM" --no-open > "$W/beam.log" 2>&1 &
BEAM_PID=$!
cleanup(){ kill $BEAM_PID $HTTP_PID 2>/dev/null; pkill -f "mpv.*beam-mpv" 2>/dev/null; rm -rf "$W"; }
trap cleanup EXIT

for i in $(seq 1 30); do curl -sf http://127.0.0.1:49200/api/state >/dev/null && break; sleep 0.3; done
S=$(curl -s http://127.0.0.1:49200/api/state); echo "state: $S"
for i in $(seq 1 30); do echo "$S" | grep -q '"ready":true' && break; sleep 0.3; S=$(curl -s http://127.0.0.1:49200/api/state); done
ok "ready" "$(echo "$S" | grep -c '"ready":true')" "1"
PORT=$(echo "$S" | sed -n 's/.*"port":\([0-9]*\).*/\1/p')
soap(){ curl -s -X POST http://127.0.0.1:$PORT/ctl/$1 -H 'Content-Type: text/xml' -H "SOAPACTION: \"urn:schemas-upnp-org:service:$1:1#$2\"" --data "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:$2 xmlns:u=\"urn:schemas-upnp-org:service:$1:1\">$3</u:$2></s:Body></s:Envelope>"; }
tag(){ sed -n "s:.*<$1>\([^<]*\)</$1>.*:\1:p"; }
st(){ soap AVTransport GetTransportInfo '<InstanceID>0</InstanceID>' | tag CurrentTransportState; }

ok "description served" "$(curl -s http://127.0.0.1:$PORT/desc.xml | grep -c MediaRenderer)" "1"
ok "initial state" "$(st)" "NO_MEDIA_PRESENT"
soap AVTransport SetAVTransportURI '<InstanceID>0</InstanceID><CurrentURI>http://127.0.0.1:8099/t.mp4</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>' >/dev/null
ok "after SetAVTransportURI" "$(st)" "STOPPED"
if command -v mpv >/dev/null; then
  soap AVTransport Play '<InstanceID>0</InstanceID><Speed>1</Speed>' >/dev/null
  sleep 3
  ok "playing" "$(st)" "PLAYING"
  P=$(soap AVTransport GetPositionInfo '<InstanceID>0</InstanceID>' | tag RelTime)
  ok "position advances" "$([ "$P" != "0:00:00" ] && echo yes || echo no)" "yes"
  soap AVTransport Pause '<InstanceID>0</InstanceID>' >/dev/null; sleep 1
  ok "paused" "$(st)" "PAUSED_PLAYBACK"
  curl -s -X POST http://127.0.0.1:49200/api/mode -H 'X-Beam: 1' -d 'value=2' >/dev/null
  ok "mode saved" "$(curl -s http://127.0.0.1:49200/api/state | grep -c '"mode":2')" "1"
  soap AVTransport Stop '<InstanceID>0</InstanceID>' >/dev/null; sleep 1
  ok "stopped" "$(st)" "STOPPED"
  soap AVTransport SetAVTransportURI '<InstanceID>0</InstanceID><CurrentURI>http://127.0.0.1:8099/s.mp4</CurrentURI><CurrentURIMetaData></CurrentURIMetaData>' >/dev/null
  soap AVTransport Play '<InstanceID>0</InstanceID><Speed>1</Speed>' >/dev/null; sleep 5
  ok "stops by itself at the end" "$(st)" "STOPPED"
else
  echo "mpv not installed: playback part skipped"
fi
ok "UI refuses POST without header" "$(curl -s -o /dev/null -w '%{http_code}' -X POST http://127.0.0.1:49200/api/quit -d 'value=1')" "404"
ok "UI refuses foreign Host" "$(curl -s -o /dev/null -w '%{http_code}' -H 'Host: evil.example' http://127.0.0.1:49200/api/state)" "403"
curl -s -X POST http://127.0.0.1:49200/api/quit -H 'X-Beam: 1' -d 'value=1' >/dev/null
for i in $(seq 1 20); do kill -0 $BEAM_PID 2>/dev/null || break; sleep 0.3; done
ok "quits on request" "$(kill -0 $BEAM_PID 2>/dev/null && echo running || echo gone)" "gone"
[ $fail = 0 ] && echo "SMOKE PASSED" || { echo "SMOKE FAILED"; cat "$W/beam.log"; }
exit $fail
