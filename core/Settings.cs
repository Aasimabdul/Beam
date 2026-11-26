using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Beam
{
    /// <summary>How the picture is fitted into the window.</summary>
    public enum AspectMode { Fit, Fill, Wide, Crop }

    public static class Aspect
    {
        public static readonly AspectMode[] All = { AspectMode.Fit, AspectMode.Fill, AspectMode.Wide, AspectMode.Crop };

        public static string Label(AspectMode m)
        {
            switch (m)
            {
                case AspectMode.Fill: return "Fill";
                case AspectMode.Wide: return "16:9";
                case AspectMode.Crop: return "Crop";
                default: return "Fit";
            }
        }

        public static string Hint(AspectMode m)
        {
            switch (m)
            {
                case AspectMode.Fill: return "Stretch to the whole window";
                case AspectMode.Wide: return "Force a 16:9 picture";
                case AspectMode.Crop: return "Zoom to fill, trim the edges";
                default: return "Whole picture, bars if needed";
            }
        }

        public static AspectMode Next(AspectMode m)
        {
            return All[(Array.IndexOf(All, m) + 1) % All.Length];
        }
    }

    /// <summary>The few settings Beam keeps in %APPDATA%\Beam\settings.ini: a stable device id, the visible name, the HTTP port and the picture mode.</summary>
    public sealed class Settings
    {
        private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Beam");
        private static readonly string FilePath = Path.Combine(Dir, "settings.ini");
        private readonly object sync = new object();

        public string Uuid = "";
        public string Name = "";
        public int Port;
        public AspectMode Mode = AspectMode.Fit;
        public bool Fullscreen = true;
        public int Volume = 100;
        public bool Welcomed;

        public static string LogPath { get { return Path.Combine(Dir, "beam.log"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        int n;
                        switch (k)
                        {
                            case "uuid": s.Uuid = v; break;
                            case "name": s.Name = v; break;
                            case "port": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) && n >= 0 && n < 65536) s.Port = n; break;
                            case "mode":
                                AspectMode m;
                                if (Enum.TryParse(v, true, out m)) s.Mode = m;
                                break;
                            case "fullscreen": s.Fullscreen = v != "0"; break;
                            case "volume": if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.Volume = Math.Max(0, Math.Min(100, n)); break;
                            case "welcomed": s.Welcomed = v == "1"; break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // unreadable settings: start from defaults
            }
            if (string.IsNullOrEmpty(s.Uuid)) { s.Uuid = Guid.NewGuid().ToString(); s.Save(); }
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = "Beam (" + Environment.MachineName + ")";
            return s;
        }

        public void Save()
        {
            lock (sync)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllLines(FilePath, new[]
                    {
                        "uuid=" + Uuid,
                        "name=" + Name,
                        "port=" + Port.ToString(CultureInfo.InvariantCulture),
                        "mode=" + Mode,
                        "fullscreen=" + (Fullscreen ? "1" : "0"),
                        "volume=" + Volume.ToString(CultureInfo.InvariantCulture),
                        "welcomed=" + (Welcomed ? "1" : "0"),
                    });
                }
                catch (Exception)
                {
                    // a read-only profile just means settings are not remembered
                }
            }
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                File.AppendAllText(LogPath, DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
            }
            catch (Exception)
            {
                // logging is best effort
            }
        }
    }
}
