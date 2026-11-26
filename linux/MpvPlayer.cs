using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Beam.Linux;

internal sealed class MpvPlayer : IPlayer, IDisposable
{
    private readonly string socketPath = Path.Combine(Path.GetTempPath(), "beam-mpv-" + Environment.ProcessId + ".sock");
    private Process? process;
    private int volume = 80;
    private bool muted;
    private readonly object gate = new();

    public long PositionMs => 0;
    public long DurationMs => 0;
    public int Volume => volume;
    public bool Muted => muted;

    public void Start()
    {
        if (process is { HasExited: false }) return;
        if (!File.Exists("/usr/bin/mpv") && !CommandExists("mpv"))
            throw new InvalidOperationException("mpv is required. Install it with your distro package manager (for example: sudo apt install mpv).");
        try { File.Delete(socketPath); } catch { }
        var start = new ProcessStartInfo("mpv")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--idle=yes");
        start.ArgumentList.Add("--force-window=yes");
        start.ArgumentList.Add("--input-terminal=no");
        start.ArgumentList.Add("--no-config");
        start.ArgumentList.Add("--input-ipc-server=" + socketPath);
        process = Process.Start(start) ?? throw new InvalidOperationException("Could not start mpv.");
    }

    private static bool CommandExists(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Any(p => File.Exists(Path.Combine(p, name)));
    }

    private void Send(params object[] command)
    {
        lock (gate)
        {
            if (process is not { HasExited: false }) Start();
            var payload = JsonSerializer.Serialize(new { command });
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var bytes = Encoding.UTF8.GetBytes(payload + "\n");
            socket.Send(bytes);
        }
    }

    public void Load(string uri, string meta, string kind, string title) => Send("loadfile", uri, "replace");
    public void Play() => Send("set_property", "pause", false);
    public void Pause() => Send("set_property", "pause", true);
    public void Stop() => Send("stop");
    public void Seek(long ms) => Send("seek", ms / 1000.0, "absolute");
    public void SetVolume(int percent) { volume = Math.Clamp(percent, 0, 100); Send("set_property", "volume", volume); }
    public void SetMute(bool mute) { muted = mute; Send("set_property", "mute", mute); }

    public void Dispose()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
        process?.Dispose();
        try { File.Delete(socketPath); } catch { }
    }
}
