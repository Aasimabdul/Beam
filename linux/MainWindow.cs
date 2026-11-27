using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Beam.Linux;

internal sealed class MainWindow : Window
{
    private static readonly IBrush BackgroundBrush = Brush.Parse("#080D19");
    private static readonly IBrush PanelBrush = Brush.Parse("#101A2B");
    private static readonly IBrush BorderBrush = Brush.Parse("#24344D");
    private static readonly IBrush AccentBrush = Brush.Parse("#52D6FF");
    private static readonly IBrush MutedBrush = Brush.Parse("#93A4BE");

    private readonly MpvPlayer player;
    private readonly LinuxReceiver receiver;
    private readonly TextBlock status;
    private readonly TextBlock deviceName;
    private readonly TextBlock portLabel;
    private readonly Slider volumeSlider;
    private readonly Slider brightnessSlider;
    private readonly ComboBox aspectPicker;
    private readonly Button muteButton;
    private bool mute;
    private readonly ScrollViewer homePage;
    private readonly ScrollViewer settingsPage;
    private readonly Button homeTab;
    private readonly Button settingsTab;

    public MainWindow()
    {
        Title = "Beam";
        Width = 760;
        Height = 720;
        MinWidth = 560;
        MinHeight = 600;
        Background = BackgroundBrush;
        Foreground = Brush.Parse("#EDF5FF");

        player = new MpvPlayer();
        receiver = new LinuxReceiver(player);

        var renameButton = new Button
        {
            Content = "✎  Rename",
            Padding = new Thickness(14, 8),
            Background = PanelBrush,
            Foreground = AccentBrush
        };
        renameButton.Click += async (_, _) => await ShowRenameDialog();

        deviceName = new TextBlock { Text = receiver.Name, FontSize = 25, FontWeight = FontWeight.SemiBold };
        status = new TextBlock { Text = "Starting receiver…", FontSize = 14, Foreground = MutedBrush };
        portLabel = new TextBlock { Text = "Preparing local network receiver", FontSize = 12, Foreground = MutedBrush };

        var readyDot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(5),
            Background = AccentBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        };
        var readyLabel = new TextBlock { Text = "READY TO RECEIVE", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = AccentBrush };
        var readyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        readyRow.Children.Add(readyDot);
        readyRow.Children.Add(readyLabel);

        var hero = new StackPanel { Spacing = 10 };
        hero.Children.Add(readyRow);
        hero.Children.Add(deviceName);
        hero.Children.Add(status);
        hero.Children.Add(portLabel);

        var castCard = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(24),
            Child = hero
        };

        var stepsTitle = new TextBlock { Text = "Cast to Beam", FontSize = 17, FontWeight = FontWeight.SemiBold };
        var stepsBody = new TextBlock
        {
            Text = "Open a DLNA-compatible app on your phone or PC, tap its cast / renderer icon, then select the Beam device shown above. Keep both devices on the same Wi-Fi.",
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Foreground = MutedBrush
        };
        var stepsCard = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(22),
            Child = new StackPanel { Spacing = 10, Children = { stepsTitle, stepsBody } }
        };

        var controlsTitle = new TextBlock { Text = "Playback controls", FontSize = 17, FontWeight = FontWeight.SemiBold };
        var controlsNote = new TextBlock { Text = "These controls apply when media is playing.", FontSize = 12, Foreground = MutedBrush };
        aspectPicker = new ComboBox
        {
            ItemsSource = new[] { "Auto", "4:3", "16:9", "21:9" },
            SelectedIndex = 0,
            MinWidth = 125,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        aspectPicker.SelectionChanged += (_, _) =>
        {
            if (aspectPicker.SelectedItem is string ratio) player.SetAspectRatio(ratio);
        };

        volumeSlider = new Slider { Minimum = 0, Maximum = 100, Value = 80, MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        volumeSlider.ValueChanged += (_, e) => player.SetVolume((int)e.NewValue);
        muteButton = new Button { Content = "♫", Width = 44, Height = 38, Background = BackgroundBrush, Foreground = AccentBrush };
        muteButton.Click += (_, _) =>
        {
            mute = !mute;
            player.SetMute(mute);
            muteButton.Content = mute ? "×" : "♫";
        };

        brightnessSlider = new Slider { Minimum = -100, Maximum = 100, Value = 0, MinWidth = 140, VerticalAlignment = VerticalAlignment.Center };
        brightnessSlider.ValueChanged += (_, e) => player.SetBrightness((int)e.NewValue);

        var aspectRow = MakeControlRow("▣", "Aspect ratio", aspectPicker);
        var volumeRow = MakeControlRow("♫", "Sound", volumeSlider, muteButton);
        var brightnessRow = MakeControlRow("☼", "Brightness", brightnessSlider);

        var controlsPanel = new StackPanel { Spacing = 14 };
        controlsPanel.Children.Add(controlsTitle);
        controlsPanel.Children.Add(controlsNote);
        controlsPanel.Children.Add(aspectRow);
        controlsPanel.Children.Add(volumeRow);
        controlsPanel.Children.Add(brightnessRow);

        var controlsCard = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(22),
            Child = controlsPanel
        };

        var footer = new TextBlock
        {
            Text = "Local network only  ·  DLNA / UPnP  ·  Powered by mpv",
            FontSize = 11,
            Foreground = MutedBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var renameTitle = new TextBlock { Text = "Name on the network", FontSize = 17, FontWeight = FontWeight.SemiBold };
        var renameNote = new TextBlock { Text = "This is the name other devices see in their cast list.", FontSize = 12, Foreground = MutedBrush };
        var renameCard = new Border
        {
            Background = PanelBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(22),
            Child = new StackPanel { Spacing = 12, Children = { renameTitle, renameNote, renameButton } }
        };

        var homePanel = new StackPanel { Spacing = 16 };
        homePanel.Children.Add(castCard);
        homePanel.Children.Add(stepsCard);
        homePanel.Children.Add(footer);
        homePage = new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = homePanel } };

        var settingsPanel = new StackPanel { Spacing = 16 };
        settingsPanel.Children.Add(renameCard);
        settingsPanel.Children.Add(controlsCard);
        settingsPage = new ScrollViewer { IsVisible = false, Content = new Border { Padding = new Thickness(24), Child = settingsPanel } };

        homeTab = MakeTab("Home", () => ShowPage(false));
        settingsTab = MakeTab("Settings", () => ShowPage(true));
        var tabs = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Background = PanelBrush };
        Grid.SetColumn(settingsTab, 1);
        tabs.Children.Add(homeTab);
        tabs.Children.Add(settingsTab);
        Grid.SetRow(tabs, 1);

        var pages = new Grid();
        pages.Children.Add(homePage);
        pages.Children.Add(settingsPage);
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(pages);
        root.Children.Add(tabs);
        Content = root;
        ShowPage(false);

        Opened += (_, _) => StartReceiverAutomatically();
        Closed += (_, _) => receiver.Dispose();
    }

    private Button MakeTab(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 14),
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Background = PanelBrush,
            CornerRadius = new CornerRadius(0)
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void ShowPage(bool settings)
    {
        settingsPage.IsVisible = settings;
        homePage.IsVisible = !settings;
        homeTab.Foreground = settings ? MutedBrush : AccentBrush;
        settingsTab.Foreground = settings ? AccentBrush : MutedBrush;
    }

    private static StackPanel MakeControlRow(string glyph, string label, Control control, Control? trailing = null)
    {
        var title = new TextBlock
        {
            Text = glyph + "  " + label,
            Width = 125,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush.Parse("#C8D5E8")
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("125,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(title);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        if (trailing != null)
        {
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }
        return new StackPanel { Children = { row } };
    }

    private void StartReceiverAutomatically()
    {
        try
        {
            receiver.Start();
            status.Text = "Your device is visible to compatible cast apps";
            portLabel.Text = $"Listening on local network · HTTP {receiver.Port}";
        }
        catch (Exception ex)
        {
            status.Text = "Could not start receiver";
            portLabel.Text = ex.Message;
        }
    }

    private async Task ShowRenameDialog()
    {
        var input = new TextBox { Text = receiver.Name, MinWidth = 260 };
        var save = new Button { Content = "Save name", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(14, 8) };
        var dialog = new Window
        {
            Title = "Rename Beam device",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            Background = BackgroundBrush,
            Foreground = Brush.Parse("#EDF5FF"),
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock { Text = "Device name", FontWeight = FontWeight.SemiBold },
                        input,
                        save
                    }
                }
            }
        };

        save.Click += (_, _) =>
        {
            var newName = input.Text?.Trim();
            if (string.IsNullOrWhiteSpace(newName)) return;
            receiver.Rename(newName);
            deviceName.Text = receiver.Name;
            dialog.Close();
        };
        await dialog.ShowDialog(this);
    }
}
