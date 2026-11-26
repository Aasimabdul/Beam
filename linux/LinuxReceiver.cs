using System.Net.Sockets;

namespace Beam.Linux;

internal sealed class LinuxReceiver : IDisposable
{
    private readonly MpvPlayer player;
    private MiniHttp? http;
    private SsdpServer? ssdp;
    public UpnpDevice? Device { get; private set; }
    public int Port { get; private set; }
    public bool IsRunning { get; private set; }
    public string Name { get; set; } = Environment.MachineName + " Beam";
    public string Uuid { get; } = Guid.NewGuid().ToString();

    public LinuxReceiver(MpvPlayer player) => this.player = player;

    public void Start()
    {
        if (IsRunning) return;
        player.Start();
        Device = new UpnpDevice(Uuid, Name, player);
        http = new MiniHttp(Device);
        try { Port = http.Start(49152); }
        catch (SocketException) { Port = http.Start(0); }
        ssdp = new SsdpServer(Uuid, Port);
        try { ssdp.Start(); }
        catch
        {
            http.Stop();
            Device.Shutdown();
            throw;
        }
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
        try { ssdp?.Shutdown(); } catch { }
        try { http?.Stop(); } catch { }
        try { Device?.Shutdown(); } catch { }
        ssdp = null; http = null; Device = null;
        player.Stop();
    }

    public void Dispose() { Stop(); player.Dispose(); }
}
