using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Beam.Linux;

internal sealed class MainWindow : Window
{
    private readonly LinuxReceiver receiver;
    private readonly TextBlock status;
    private readonly TextBlock deviceName;
    private readonly Button toggle;
    private readonly TextBox nameBox;

    public MainWindow()
    {
        Title = "Beam";
        Width = 520;
        Height = 430;
        MinWidth = 420;
        MinHeight = 360;
        Background = Brush.Parse("#0B1020");
        Foreground = Brush.Parse("#EAF2FF");
        var player = new MpvPlayer();
        receiver = new LinuxReceiver(player);

        status = new TextBlock { Text = "Receiver is off", FontSize = 16, Foreground = Brush.Parse("#9CAAC4") };
        deviceName = new TextBlock { Text = receiver.Name, FontSize = 22, FontWeight = FontWeight.SemiBold };
        toggle = new Button { Content = "Start receiver", Padding = new Thickness(18, 12), HorizontalAlignment = HorizontalAlignment.Stretch };
        toggle.Click += (_, _) => ToggleReceiver();
        nameBox = new TextBox { Text = receiver.Name, Watermark = "Device name" };
        var rename = new Button { Content = "Apply name" };
        rename.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                receiver.Name = name;
                deviceName.Text = name;
                if (receiver.IsRunning) { receiver.Stop(); ToggleReceiver(); }
            }
        };

        var hint = new TextBlock
        {
            Text = "Keep Beam and your sender on the same Wi-Fi. Open VLC or a DLNA sender app and choose this receiver. Playback uses mpv.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush.Parse("#A9B7D0"),
            LineHeight = 22
        };
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "BEAM  /  LINUX", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#67D8FF") });
        panel.Children.Add(deviceName);
        panel.Children.Add(status);
        panel.Children.Add(toggle);
        panel.Children.Add(new TextBlock { Text = "Receiver name", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(nameBox);
        panel.Children.Add(rename);
        panel.Children.Add(new Border { Height = 1, Background = Brush.Parse("#26334E"), Margin = new Thickness(0, 4) });
        panel.Children.Add(hint);
        Content = new ScrollViewer { Content = panel };
        Closed += (_, _) => receiver.Dispose();
    }

    private void ToggleReceiver()
    {
        try
        {
            if (receiver.IsRunning)
            {
                receiver.Stop();
                status.Text = "Receiver is off";
                toggle.Content = "Start receiver";
            }
            else
            {
                receiver.Start();
                status.Text = $"Ready to cast · HTTP port {receiver.Port}";
                toggle.Content = "Stop receiver";
            }
        }
        catch (Exception ex)
        {
            status.Text = "Could not start: " + ex.Message;
        }
    }
}
