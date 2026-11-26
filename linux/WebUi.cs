using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;

namespace Beam
{
    /// <summary>The Linux window: a small page served on 127.0.0.1 only, with Home and Settings tabs.</summary>
    public sealed class WebUi : IHttpHandler
    {
        private readonly App app;

        public WebUi(App app) { this.app = app; }

        private static string J(string s)
        {
            var b = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }

        private string State()
        {
            string ip = Lan.LocalIpFor(null) ?? "";
            string playing = app.Device == null ? "STOPPED" : app.Device.State;
            return "{\"app\":\"beam\",\"version\":" + J(App.Version) + ",\"ready\":" + (app.Ready ? "true" : "false")
                + ",\"error\":" + J(app.Error) + ",\"name\":" + J(app.Cfg.Name) + ",\"ip\":" + J(ip) + ",\"port\":" + app.Port
                + ",\"mode\":" + (int)app.Cfg.Mode + ",\"fullscreen\":" + (app.Cfg.Fullscreen ? "true" : "false")
                + ",\"autostart\":" + (App.AutostartOn() ? "true" : "false") + ",\"mpv\":" + (app.Player.Available ? "true" : "false")
                + ",\"state\":" + J(playing) + "}";
        }

        private static MiniHttp.Response Text(int code, string type, string body)
        {
            var r = new MiniHttp.Response(code, code == 200 ? "OK" : "Error");
            r.Headers["Content-Type"] = type;
            r.Headers["Cache-Control"] = "no-store";
            r.Body = Encoding.UTF8.GetBytes(body);
            return r;
        }

        private bool HostOk(MiniHttp.Request r)
        {
            string h = r.Header("host");
            if (h == null) return false;
            return h == "127.0.0.1:" + app.UiPort || h == "localhost:" + app.UiPort;
        }

        private static string Form(string body, string key)
        {
            foreach (string pair in body.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == key)
                {
                    try { return Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')); }
                    catch (Exception) { return ""; }
                }
            }
            return "";
        }

        public MiniHttp.Response Handle(MiniHttp.Request r)
        {
            if (!HostOk(r)) return Text(403, "text/plain", "forbidden");
            if (r.Method == "GET" && (r.Path == "/" || r.Path == "/index.html")) return Text(200, "text/html; charset=utf-8", Page.Html);
            if (r.Method == "GET" && r.Path == "/api/state") return Text(200, "application/json", State());
            if (r.Method != "POST" || r.Header("x-beam") != "1") return Text(404, "text/plain", "not found");

            string body = r.BodyText();
            switch (r.Path)
            {
                case "/api/name":
                {
                    string n = Form(body, "value").Trim();
                    if (n.Length == 0) return Text(400, "text/plain", "empty name");
                    if (n.Length > 40) n = n.Substring(0, 40);
                    app.Rename(n);
                    break;
                }
                case "/api/mode":
                {
                    int m;
                    if (!int.TryParse(Form(body, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out m) || m < 0 || m >= Aspect.All.Length)
                        return Text(400, "text/plain", "bad mode");
                    app.Player.SetMode(Aspect.All[m], true);
                    break;
                }
                case "/api/fullscreen":
                    app.Cfg.Fullscreen = Form(body, "value") == "1";
                    app.Cfg.Save();
                    break;
                case "/api/autostart":
                    App.SetAutostart(Form(body, "value") == "1");
                    break;
                case "/api/quit":
                    app.Quit.Set();
                    break;
                default:
                    return Text(404, "text/plain", "not found");
            }
            return Text(200, "application/json", State());
        }

        /// <summary>True when something on this port answers like Beam's own page (a second launch just opens it).</summary>
        public static bool IsBeamRunning(int port)
        {
            try
            {
                using (var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                {
                    string s = c.GetStringAsync("http://127.0.0.1:" + port + "/api/state").GetAwaiter().GetResult();
                    return s.Contains("\"app\":\"beam\"");
                }
            }
            catch (Exception) { return false; }
        }
    }
}
