using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Beam
{
    /// <summary>The start screen: "Ready to cast" with how-to steps, plus a Settings page (name, picture mode, full screen, start with Windows).</summary>
    public sealed class HomeWindow : Window
    {
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x3A));
        private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x12, 0x2B, 0x4D));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));
        private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80));
        private const string CastPath = "M21,3H3C1.9,3 1,3.9 1,5v3h2V5h18v14h-7v2h7c1.1,0 2,-0.9 2,-2V5C23,3.9 22.1,3 21,3zM1,18v3h3C4,19.34 2.66,18 1,18zM1,14v2c2.76,0 5,2.24 5,5h2C8,17.13 4.87,14 1,14zM1,10v2c4.97,0 9,4.03 9,9h2C12,14.92 7.07,10 1,10z";

        private readonly Receiver rx;
        private readonly PlayerWindow player;
        public bool Quitting;

        private readonly ScrollViewer homePanel = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly ScrollViewer settingsPanel = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        private readonly Ellipse dot = new Ellipse { Width = 10, Height = 10, Fill = Accent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock title = Text("Starting…", 32, Brushes.White, FontWeights.SemiBold);
        private readonly TextBlock visibleAs = Text("", 15, Muted, FontWeights.Normal);
        private readonly TextBlock ipText = Text("", 12, Muted, FontWeights.Normal);
        private readonly TextBox nameBox = new TextBox { MaxLength = 40, FontSize = 15, Padding = new Thickness(6), MinWidth = 260 };
        private readonly TextBlock savedNote = Text("", 12, Accent, FontWeights.Normal);
        private readonly RadioButton[] modes = new RadioButton[Aspect.All.Length];
        private readonly CheckBox fullBox = new CheckBox { Content = "Open videos and photos full screen", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 6, 0, 0) };
        private readonly CheckBox startBox = new CheckBox { Content = "Start Beam with Windows (in the tray)", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(0, 10, 0, 0) };
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly Border tabHome = new Border { BorderThickness = new Thickness(0, 0, 0, 3), Padding = new Thickness(0, 12, 0, 10), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        private readonly Border tabSettings = new Border { BorderThickness = new Thickness(0, 0, 0, 3), Padding = new Thickness(0, 12, 0, 10), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
        private bool syncing;
        private int tick;

        public HomeWindow(Receiver rx, PlayerWindow player)
        {
            this.rx = rx;
            this.player = player;
            Title = "Beam";
            Width = 540;
            Height = 680;
            MinWidth = 440;
            MinHeight = 520;
            Background = Bg;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");

            homePanel.Content = BuildHome();
            settingsPanel.Content = BuildSettings();
            tabHome.Child = new TextBlock { Text = "Home", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            tabSettings.Child = new TextBlock { Text = "Settings", FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            tabHome.MouseLeftButtonUp += delegate { ShowPage(false); };
            tabSettings.MouseLeftButtonUp += delegate { ShowPage(true); };
            var tabs = new Grid { Background = Card };
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            tabs.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(tabSettings, 1);
            tabs.Children.Add(tabHome);
            tabs.Children.Add(tabSettings);

            var body = new Grid();
            body.Children.Add(homePanel);
            body.Children.Add(settingsPanel);
            var root = new Grid { Background = Bg };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(body, 1);
            root.Children.Add(tabs);
            root.Children.Add(body);
            Content = root;
            UpdateTabs(false);

            timer.Tick += delegate { Refresh(); };
            IsVisibleChanged += delegate
            {
                if (IsVisible) { Refresh(); timer.Start(); } else timer.Stop();
            };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (Quitting) return;
                e.Cancel = true;
                Hide();
            };
            rx.NameChanged += delegate { Dispatcher.BeginInvoke(new Action(Refresh)); };
        }

        public void Open(bool settings)
        {
            ShowPage(settings);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Topmost = true;
            Topmost = false;
            Activate();
        }

        private void ShowPage(bool settings)
        {
            settingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
            homePanel.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
            UpdateTabs(settings);
            if (settings) LoadSettings();
        }

        private void UpdateTabs(bool settings)
        {
            Style(tabHome, !settings);
            Style(tabSettings, settings);
        }

        private static void Style(Border tab, bool active)
        {
            tab.BorderBrush = active ? Accent : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = active ? Accent : Muted;
        }

        // ------------------------------------------------------------------ pieces

        private static TextBlock Text(string text, double size, Brush fg, FontWeight weight)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = fg, FontWeight = weight, TextWrapping = TextWrapping.Wrap };
        }

        private static Border CardOf(UIElement child)
        {
            return new Border { Background = Card, CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 14), Child = child };
        }

        private static StackPanel Step(string number, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            var badge = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Accent, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = number, Foreground = Bg, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            var t = Text(text, 14, Brushes.White, FontWeights.Normal);
            t.Width = 380;
            row.Children.Add(badge);
            row.Children.Add(t);
            return row;
        }

        private Button Flat(string label, Action click)
        {
            var b = new Button { Content = label, Padding = new Thickness(14, 6, 14, 6), FontSize = 14, Cursor = System.Windows.Input.Cursors.Hand };
            b.Click += delegate { click(); };
            return b;
        }

        private UIElement BuildHome()
        {
            var col = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

            col.Children.Add(new Path { Data = Geometry.Parse(CastPath), Fill = Accent, Stretch = Stretch.Uniform, Width = 84, Height = 84, Margin = new Thickness(0, 4, 0, 14), HorizontalAlignment = HorizontalAlignment.Center });

            var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            status.Children.Add(dot);
            title.TextAlignment = TextAlignment.Center;
            status.Children.Add(title);
            col.Children.Add(status);

            visibleAs.TextAlignment = TextAlignment.Center;
            visibleAs.Margin = new Thickness(0, 6, 0, 0);
            col.Children.Add(visibleAs);
            ipText.TextAlignment = TextAlignment.Center;
            ipText.Margin = new Thickness(0, 2, 0, 22);
            col.Children.Add(ipText);

            var how = new StackPanel();
            how.Children.Add(Text("How to cast", 17, Brushes.White, FontWeights.SemiBold));
            how.Children.Add(Step("1", "Connect your phone to the same Wi-Fi as this PC."));
            how.Children.Add(Step("2", "Open a cast-to-DLNA app on the phone: VLC (renderer button), BubbleUPnP, LocalCast, Web Video Caster or your gallery's \"Cast / Smart View\"."));
            how.Children.Add(Step("3", "In the device list pick the name shown above, then choose a video, song or photo. It opens here on this PC."));
            col.Children.Add(CardOf(how));

            var keys = new StackPanel();
            keys.Children.Add(Text("While it plays", 17, Brushes.White, FontWeights.SemiBold));
            keys.Children.Add(Text("Space  play / pause\n← →  jump 10 seconds\n↑ ↓  volume     M  mute\nV  change picture: Fit → Fill → 16:9 → Crop  (keys 1 2 3 4 pick directly)\nF  full screen     Esc  full screen off / stop", 13, Muted, FontWeights.Normal));
            ((TextBlock)keys.Children[1]).Margin = new Thickness(0, 8, 0, 0);
            ((TextBlock)keys.Children[1]).LineHeight = 21;
            col.Children.Add(CardOf(keys));

            var foot = Text("Closing this window keeps Beam running in the tray, near the clock. The first time, allow Beam through Windows Firewall on Private networks.", 12, Muted, FontWeights.Normal);
            foot.TextAlignment = TextAlignment.Center;
            col.Children.Add(foot);
            return col;
        }

        private UIElement BuildSettings()
        {
            var col = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };

            var name = new StackPanel();
            name.Children.Add(Text("Receiver name", 17, Brushes.White, FontWeights.SemiBold));
            name.Children.Add(Text("This is what your phone shows in its list of TVs and speakers.", 12, Muted, FontWeights.Normal));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(nameBox);
            Button save = Flat("Save", SaveName);
            save.Margin = new Thickness(10, 0, 0, 0);
            save.IsDefault = true;
            row.Children.Add(save);
            name.Children.Add(row);
            savedNote.Margin = new Thickness(0, 6, 0, 0);
            name.Children.Add(savedNote);
            col.Children.Add(CardOf(name));

            var pic = new StackPanel();
            pic.Children.Add(Text("Picture", 17, Brushes.White, FontWeights.SemiBold));
            pic.Children.Add(Text("How video and photos fit the screen. Press V while playing to switch.", 12, Muted, FontWeights.Normal));
            for (int i = 0; i < Aspect.All.Length; i++)
            {
                AspectMode m = Aspect.All[i];
                var r = new RadioButton
                {
                    GroupName = "aspect",
                    Foreground = Brushes.White,
                    FontSize = 14,
                    Margin = new Thickness(0, i == 0 ? 10 : 6, 0, 0),
                    Content = Aspect.Label(m) + "   –   " + Aspect.Hint(m),
                };
                r.Checked += delegate { if (!syncing) player.SetMode(m, false); };
                modes[i] = r;
                pic.Children.Add(r);
            }
            col.Children.Add(CardOf(pic));

            var opts = new StackPanel();
            opts.Children.Add(Text("General", 17, Brushes.White, FontWeights.SemiBold));
            fullBox.Click += delegate { rx.Cfg.Fullscreen = fullBox.IsChecked == true; rx.Cfg.Save(); };
            startBox.Click += delegate { Startup.Set(startBox.IsChecked == true); };
            opts.Children.Add(fullBox);
            opts.Children.Add(startBox);
            col.Children.Add(CardOf(opts));

            Button quit = Flat("Quit Beam", delegate { Quit(); });
            quit.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(quit);
            var ver = Text("Beam 0.1.0  ·  local DLNA receiver, no cloud, no account", 12, Muted, FontWeights.Normal);
            ver.Margin = new Thickness(0, 16, 0, 0);
            col.Children.Add(ver);
            return col;
        }

        // ------------------------------------------------------------------ behaviour

        private void LoadSettings()
        {
            nameBox.Text = rx.Cfg.Name;
            savedNote.Text = "";
            SyncChecks();
        }

        private void SyncChecks()
        {
            syncing = true;
            for (int i = 0; i < modes.Length; i++) modes[i].IsChecked = Aspect.All[i] == rx.Cfg.Mode;
            fullBox.IsChecked = rx.Cfg.Fullscreen;
            startBox.IsChecked = Startup.IsOn();
            syncing = false;
        }

        private void SaveName()
        {
            string n = nameBox.Text.Trim();
            if (n.Length == 0) { savedNote.Foreground = Bad; savedNote.Text = "Type a name first."; return; }
            rx.Rename(n);
            savedNote.Foreground = Accent;
            savedNote.Text = "Saved. Your phone will show \"" + n + "\" in a few seconds.";
            Refresh();
        }

        private void Quit()
        {
            Quitting = true;
            player.Quitting = true;
            Application.Current.Shutdown();
        }

        private void Refresh()
        {
            if (rx.Error != null)
            {
                dot.Fill = Bad;
                title.Text = "Can't start";
                visibleAs.Text = rx.Error;
            }
            else if (!rx.Ready)
            {
                dot.Fill = Muted;
                title.Text = "Starting…";
                visibleAs.Text = "";
            }
            else
            {
                dot.Fill = Accent;
                title.Text = "Ready to cast";
                visibleAs.Text = "Visible as \"" + rx.Cfg.Name + "\"";
            }
            if (tick++ % 5 == 0)
            {
                var a = Lan.Addresses();
                ipText.Text = a.Count == 0 ? "No Wi-Fi or Ethernet connection found" : "This PC: " + a[0].Ip + (rx.Port > 0 ? "   ·   port " + rx.Port : "");
            }
            if (settingsPanel.Visibility == Visibility.Visible && !nameBox.IsKeyboardFocusWithin) SyncChecks();
        }
    }
}
