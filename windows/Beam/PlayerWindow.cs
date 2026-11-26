using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Beam
{
    /// <summary>Keeps the PC awake while something plays.</summary>
    internal static class Power
    {
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);

        private const uint Continuous = 0x80000000;
        private const uint SystemRequired = 0x00000001;
        private const uint DisplayRequired = 0x00000002;

        public static void Keep() { SetThreadExecutionState(Continuous | SystemRequired | DisplayRequired); }
        public static void Release() { SetThreadExecutionState(Continuous); }
    }

    /// <summary>
    /// The full-screen player: one MediaElement (video / audio) or one picture at a time, with Fit / Fill / 16:9 / Crop.
    /// The IPlayer calls arrive on HTTP threads and are handed to the UI thread; reads use cached values and never block.
    /// </summary>
    public sealed class PlayerWindow : Window, IPlayer
    {
        private readonly Settings cfg;
        public UpnpDevice Device;
        public bool Quitting;

        private readonly MediaElement media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual,
            ScrubbingEnabled = true,
            Stretch = Stretch.Uniform,
        };
        private readonly Image pic = new Image { Visibility = Visibility.Collapsed };
        private readonly Grid stage = new Grid { ClipToBounds = true, Background = Brushes.Black };
        private readonly TextBlock noteGlyph = new TextBlock { Text = "♫", FontSize = 140, Foreground = new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly TextBlock titleText = new TextBlock { Foreground = Brushes.White, FontSize = 18, Margin = new Thickness(20, 14, 20, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        private readonly TextBlock toast = new TextBlock { Foreground = Brushes.White, FontSize = 26, Padding = new Thickness(22, 10, 22, 10) };
        private readonly Border toastBox = new Border { Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0B, 0x1F, 0x3A)), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 70, 0, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        private readonly TextBlock errorText = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)), FontSize = 22, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 800, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };

        private readonly Border bar = new Border { Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x0B, 0x1F, 0x3A)), VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(16, 8, 16, 10) };
        private readonly Button playBtn = new Button { Content = "▶" };
        private readonly Button stopBtn = new Button { Content = "■" };
        private readonly Button muteBtn = new Button { Content = "🔊" };
        private readonly Button modeBtn = new Button { MinWidth = 64 };
        private readonly Button fsBtn = new Button { Content = "⛶" };
        private readonly Slider seek = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Margin = new Thickness(0, 0, 0, 6) };
        private readonly Slider volume = new Slider { Minimum = 0, Maximum = 100, Width = 110, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        private readonly TextBlock timeText = new TextBlock { Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

        private readonly DispatcherTimer ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer hideBar = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };

        private long pos;
        private long dur;
        private int vol = 100;
        private bool mute;
        private int gen;
        private string kind = "video";
        private string uri = "";
        private string title = "";
        private bool opened;
        private bool playing;
        private bool wantPlay;
        private long pendingSeek = -1;
        private bool draggingSeek;
        private bool fullscreen;
        private bool syncing;
        private AspectMode mode = AspectMode.Fit;

        public PlayerWindow(Settings cfg)
        {
            this.cfg = cfg;
            vol = cfg.Volume;
            mode = cfg.Mode;
            Title = "Beam";
            Background = Brushes.Black;
            WindowStyle = WindowStyle.SingleBorderWindow;
            Width = 960;
            Height = 540;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Build();
            Wire();
            ApplyVolume();
        }

        // ------------------------------------------------------------------ building the window

        private static ControlTemplate FlatButton()
        {
            var t = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "bd";
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            t.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x55, 0x2D, 0xD4, 0xBF)), "bd"));
            t.Triggers.Add(hover);
            return t;
        }

        private static void StyleButton(Button b, ControlTemplate tpl)
        {
            b.Template = tpl;
            b.Background = Brushes.Transparent;
            b.Foreground = Brushes.White;
            b.BorderThickness = new Thickness(0);
            b.FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI");
            b.FontSize = 20;
            b.Padding = new Thickness(12, 5, 12, 5);
            b.Margin = new Thickness(0, 0, 2, 0);
            b.Cursor = Cursors.Hand;
            b.Focusable = false;
        }

        private void Build()
        {
            ControlTemplate tpl = FlatButton();
            StyleButton(playBtn, tpl); StyleButton(stopBtn, tpl); StyleButton(muteBtn, tpl); StyleButton(modeBtn, tpl); StyleButton(fsBtn, tpl);
            modeBtn.FontSize = 16;
            modeBtn.ToolTip = "Picture: Fit / Fill / 16:9 / Crop  (V)";
            fsBtn.ToolTip = "Full screen  (F)";
            playBtn.ToolTip = "Play / pause  (Space)";
            stopBtn.ToolTip = "Stop";
            muteBtn.ToolTip = "Mute  (M)";
            seek.Focusable = false;
            volume.Focusable = false;
            volume.Value = vol;

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(playBtn);
            left.Children.Add(stopBtn);
            left.Children.Add(timeText);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(muteBtn);
            right.Children.Add(volume);
            right.Children.Add(modeBtn);
            right.Children.Add(fsBtn);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            row.Children.Add(left);
            row.Children.Add(right);

            var barContent = new StackPanel();
            barContent.Children.Add(seek);
            barContent.Children.Add(row);
            bar.Child = barContent;

            toastBox.Child = toast;
            stage.Children.Add(media);
            stage.Children.Add(pic);
            stage.Children.Add(noteGlyph);

            var root = new Grid { Background = Brushes.Black };
            root.Children.Add(stage);
            root.Children.Add(titleText);
            root.Children.Add(errorText);
            root.Children.Add(toastBox);
            root.Children.Add(bar);
            Content = root;

            var menu = new ContextMenu();
            foreach (AspectMode m in Aspect.All)
            {
                AspectMode mm = m;
                var item = new MenuItem { Header = Aspect.Label(m) + "  –  " + Aspect.Hint(m), IsCheckable = true, Tag = m };
                item.Click += delegate { SetMode(mm, true); };
                menu.Items.Add(item);
            }
            menu.Opened += delegate
            {
                foreach (object o in menu.Items)
                {
                    var mi = o as MenuItem;
                    if (mi != null) mi.IsChecked = (AspectMode)mi.Tag == mode;
                }
            };
            root.ContextMenu = menu;

            modeBtn.Content = Aspect.Label(mode);
            UpdateButtons();
        }

        private void Wire()
        {
            playBtn.Click += delegate { UserToggle(); };
            stopBtn.Click += delegate { UserStop(); };
            muteBtn.Click += delegate { UserMute(); };
            modeBtn.Click += delegate { SetMode(Aspect.Next(mode), true); };
            fsBtn.Click += delegate { ToggleFullscreen(); };

            seek.PreviewMouseLeftButtonDown += delegate { draggingSeek = true; };
            seek.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(delegate
            {
                if (!draggingSeek) return;
                draggingSeek = false;
                UserSeek((long)seek.Value);
            }), true);
            seek.ValueChanged += delegate
            {
                if (draggingSeek) timeText.Text = Upnp.Fmt((long)seek.Value) + " / " + Upnp.Fmt(Interlocked.Read(ref dur));
            };
            volume.ValueChanged += delegate
            {
                if (syncing) return;
                vol = (int)Math.Round(volume.Value);
                mute = false;
                cfg.Volume = vol;
                ApplyVolume();
                if (Device != null) Device.OnVolumeChanged();
            };

            media.MediaOpened += delegate { OnOpened(); };
            media.MediaEnded += delegate { OnEnded(); };
            media.MediaFailed += delegate(object s, ExceptionRoutedEventArgs e)
            {
                string why = e.ErrorException == null ? "" : e.ErrorException.Message;
                Fail("Windows can't play this file" + (why.Length > 0 ? ": " + why : "") + ".\nTry an MP4 (H.264) or install the codec for this format.");
            };

            ticker.Tick += delegate { Tick(); };
            hideBar.Tick += delegate { hideBar.Stop(); if (playing || kind == "image") HideBar(); };
            toastTimer.Tick += delegate { toastTimer.Stop(); toastBox.Visibility = Visibility.Collapsed; toast.Visibility = Visibility.Collapsed; };

            PreviewMouseMove += delegate { ShowBar(); };
            MouseDoubleClick += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton == MouseButton.Left && !bar.IsMouseOver) ToggleFullscreen();
            };
            PreviewKeyDown += OnKey;
            SizeChanged += delegate { ApplyLayout(); };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (Quitting) return;
                e.Cancel = true;
                UserStop();
            };
        }

        // ------------------------------------------------------------------ picture mode

        public AspectMode Mode { get { return mode; } }

        public void SetMode(AspectMode m, bool announce)
        {
            mode = m;
            cfg.Mode = m;
            cfg.Save();
            modeBtn.Content = Aspect.Label(m);
            ApplyLayout();
            if (announce) Toast("Picture: " + Aspect.Label(m) + "  –  " + Aspect.Hint(m));
        }

        private void ApplyLayout()
        {
            double sw = stage.ActualWidth, sh = stage.ActualHeight;
            Fit(media, sw, sh);
            Fit(pic, sw, sh);
        }

        private void Fit(FrameworkElement el, double sw, double sh)
        {
            Stretch st;
            double w = double.NaN, h = double.NaN;
            switch (mode)
            {
                case AspectMode.Fill: st = Stretch.Fill; break;
                case AspectMode.Crop: st = Stretch.UniformToFill; break;
                case AspectMode.Wide:
                    st = Stretch.Fill;
                    if (sw > 0 && sh > 0)
                    {
                        double bw = sw, bh = sw * 9.0 / 16.0;
                        if (bh > sh) { bh = sh; bw = sh * 16.0 / 9.0; }
                        w = bw;
                        h = bh;
                    }
                    break;
                default: st = Stretch.Uniform; break;
            }
            var me = el as MediaElement;
            if (me != null) me.Stretch = st;
            var im = el as Image;
            if (im != null) im.Stretch = st;
            el.Width = w;
            el.Height = h;
            el.HorizontalAlignment = HorizontalAlignment.Center;
            el.VerticalAlignment = VerticalAlignment.Center;
        }

        // ------------------------------------------------------------------ window state

        public void ToggleFullscreen()
        {
            SetFullscreen(!fullscreen);
            if (kind != "audio") { cfg.Fullscreen = fullscreen; cfg.Save(); }
        }

        private void SetFullscreen(bool on)
        {
            fullscreen = on;
            if (on)
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                Topmost = false;
            }
            fsBtn.Content = on ? "⤡" : "⛶";
        }

        private void ShowPlayer()
        {
            bool wantFull = kind != "audio" && cfg.Fullscreen;
            if (!IsVisible)
            {
                SetFullscreen(wantFull);
                Show();
            }
            else if (wantFull != fullscreen && kind != "audio")
            {
                SetFullscreen(wantFull);
            }
            if (!fullscreen)
            {
                Topmost = true;
                Topmost = false;
            }
            Activate();
            ShowBar();
        }

        /// <summary>Brings the player back (tray menu) when something is loaded.</summary>
        public void ShowIfLoaded()
        {
            if (uri.Length > 0) ShowPlayer();
        }

        public bool HasMedia { get { return uri.Length > 0; } }

        private void ShowBar()
        {
            bar.Visibility = Visibility.Visible;
            titleText.Visibility = Visibility.Visible;
            Cursor = Cursors.Arrow;
            hideBar.Stop();
            hideBar.Start();
        }

        private void HideBar()
        {
            if (bar.IsMouseOver) { hideBar.Start(); return; }
            bar.Visibility = Visibility.Collapsed;
            titleText.Visibility = Visibility.Collapsed;
            Cursor = Cursors.None;
        }

        private void Toast(string text)
        {
            toast.Text = text;
            toast.Visibility = Visibility.Visible;
            toastBox.Visibility = Visibility.Visible;
            toastTimer.Stop();
            toastTimer.Start();
        }

        private void UpdateButtons()
        {
            playBtn.Content = playing ? "⏸" : "▶";
            muteBtn.Content = mute || vol == 0 ? "🔇" : "🔊";
            playBtn.IsEnabled = kind != "image";
            seek.IsEnabled = kind != "image";
            seek.Visibility = kind == "image" ? Visibility.Collapsed : Visibility.Visible;
            timeText.Visibility = kind == "image" ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ keys

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Space:
                case Key.K:
                case Key.MediaPlayPause:
                    UserToggle(); break;
                case Key.Left: UserSeek(Math.Max(0, (long)PositionNow() - 10000)); Toast("« 10 s"); break;
                case Key.Right: UserSeek(Math.Min(Math.Max(0, Interlocked.Read(ref dur) - 500), (long)PositionNow() + 10000)); Toast("10 s »"); break;
                case Key.Up: AdjustVolume(5); break;
                case Key.Down: AdjustVolume(-5); break;
                case Key.M: UserMute(); break;
                case Key.V: SetMode(Aspect.Next(mode), true); break;
                case Key.D1: SetMode(AspectMode.Fit, true); break;
                case Key.D2: SetMode(AspectMode.Fill, true); break;
                case Key.D3: SetMode(AspectMode.Wide, true); break;
                case Key.D4: SetMode(AspectMode.Crop, true); break;
                case Key.F:
                case Key.F11:
                    ToggleFullscreen(); break;
                case Key.Enter:
                    if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) ToggleFullscreen();
                    else return;
                    break;
                case Key.Escape:
                    if (fullscreen && kind != "audio") ToggleFullscreen(); else UserStop();
                    break;
                case Key.MediaStop: UserStop(); break;
                default: return;
            }
            e.Handled = true;
        }

        private double PositionNow()
        {
            try { return opened ? media.Position.TotalMilliseconds : Interlocked.Read(ref pos); }
            catch (Exception) { return Interlocked.Read(ref pos); }
        }

        private void AdjustVolume(int d)
        {
            vol = Math.Max(0, Math.Min(100, vol + d));
            mute = false;
            cfg.Volume = vol;
            ApplyVolume();
            Toast("Volume " + vol + "%");
            if (Device != null) Device.OnVolumeChanged();
        }

        // ------------------------------------------------------------------ IPlayer (any thread)

        public void Load(string u, string meta, string k, string t)
        {
            Dispatcher.BeginInvoke(new Action(delegate { DoLoad(u, k, t); }));
        }

        public void Play() { Dispatcher.BeginInvoke(new Action(DoPlay)); }
        public void Pause() { Dispatcher.BeginInvoke(new Action(DoPause)); }
        public void Stop() { Dispatcher.BeginInvoke(new Action(DoStop)); }
        public void Seek(long ms) { Dispatcher.BeginInvoke(new Action(delegate { DoSeek(ms); })); }

        public long PositionMs { get { return Interlocked.Read(ref pos); } }
        public long DurationMs { get { return Interlocked.Read(ref dur); } }

        public void SetVolume(int percent)
        {
            vol = percent;
            mute = false;
            Dispatcher.BeginInvoke(new Action(delegate { cfg.Volume = vol; ApplyVolume(); }));
        }

        public int Volume { get { return vol; } }

        public void SetMute(bool m)
        {
            mute = m;
            Dispatcher.BeginInvoke(new Action(ApplyVolume));
        }

        public bool Muted { get { return mute; } }

        // ------------------------------------------------------------------ from the window (UI thread)

        private void UserToggle()
        {
            if (kind == "image") return;
            if (playing)
            {
                DoPause();
                if (Device != null) Device.OnPlayerState("PAUSED_PLAYBACK");
            }
            else
            {
                DoPlay();
                if (Device != null) Device.OnPlayerState("PLAYING");
            }
        }

        private void UserSeek(long ms) { DoSeek(ms); }

        private void UserStop()
        {
            DoStop();
            if (Device != null) Device.OnPlayerState("STOPPED");
        }

        private void UserMute()
        {
            mute = !mute;
            ApplyVolume();
            Toast(mute ? "Muted" : "Volume " + vol + "%");
            if (Device != null) Device.OnVolumeChanged();
        }

        // ------------------------------------------------------------------ UI-thread work

        private void DoLoad(string u, string k, string t)
        {
            gen++;
            ReleaseMedia();
            uri = u;
            kind = k;
            title = t.Length > 0 ? t : NameOf(u);
            titleText.Text = title;
            errorText.Visibility = Visibility.Collapsed;
            Interlocked.Exchange(ref pos, 0);
            Interlocked.Exchange(ref dur, 0);
            wantPlay = false;
            pendingSeek = -1;
            seek.Value = 0;
            seek.Maximum = 1;
            timeText.Text = "0:00:00 / 0:00:00";
            noteGlyph.Visibility = k == "audio" ? Visibility.Visible : Visibility.Collapsed;
            pic.Source = null;
            if (k == "image")
            {
                media.Visibility = Visibility.Collapsed;
                pic.Visibility = Visibility.Visible;
                LoadImage(u, gen);
            }
            else
            {
                pic.Visibility = Visibility.Collapsed;
                media.Visibility = Visibility.Visible;
                try
                {
                    media.Source = new Uri(u);
                    media.Pause();
                }
                catch (Exception e)
                {
                    Fail("Can't open this: " + e.Message);
                    return;
                }
            }
            ApplyLayout();
            UpdateButtons();
            ShowPlayer();
        }

        private static string NameOf(string u)
        {
            try
            {
                string last = Path.GetFileName(new Uri(u).AbsolutePath);
                return string.IsNullOrEmpty(last) ? u : Uri.UnescapeDataString(last);
            }
            catch (Exception) { return u; }
        }

        private void DoPlay()
        {
            wantPlay = true;
            if (kind == "image" || uri.Length == 0) return;
            try { media.Play(); }
            catch (Exception) { Fail("Can't start playback"); return; }
            playing = true;
            ticker.Start();
            Power.Keep();
            UpdateButtons();
            ShowBar();
        }

        private void DoPause()
        {
            wantPlay = false;
            if (uri.Length == 0) return;
            try { media.Pause(); } catch (Exception) { }
            playing = false;
            ticker.Stop();
            Power.Release();
            UpdateButtons();
            ShowBar();
        }

        private void DoStop()
        {
            wantPlay = false;
            ReleaseMedia();
            uri = "";
            Interlocked.Exchange(ref pos, 0);
            Interlocked.Exchange(ref dur, 0);
            pendingSeek = -1;
            if (IsVisible) Hide();
        }

        private void DoSeek(long ms)
        {
            if (kind == "image") return;
            if (opened)
            {
                try { media.Position = TimeSpan.FromMilliseconds(ms); }
                catch (Exception) { }
            }
            else pendingSeek = ms;
            Interlocked.Exchange(ref pos, ms);
            if (!draggingSeek) seek.Value = Math.Min(seek.Maximum, ms);
        }

        private void ApplyVolume()
        {
            double f = mute ? 0 : vol / 100.0;
            try { media.Volume = f; } catch (Exception) { }
            syncing = true;
            volume.Value = vol;
            syncing = false;
            UpdateButtons();
        }

        private void ReleaseMedia()
        {
            ticker.Stop();
            playing = false;
            opened = false;
            Power.Release();
            try
            {
                media.Close();
                media.Source = null;
            }
            catch (Exception) { }
            pic.Source = null;
            UpdateButtons();
        }

        private void Tick()
        {
            if (!opened || !playing) return;
            double p = PositionNow();
            Interlocked.Exchange(ref pos, (long)p);
            if (!draggingSeek)
            {
                seek.Value = Math.Min(seek.Maximum, p);
                timeText.Text = Upnp.Fmt((long)p) + " / " + Upnp.Fmt(Interlocked.Read(ref dur));
            }
        }

        private void OnOpened()
        {
            opened = true;
            if (media.NaturalDuration.HasTimeSpan) Interlocked.Exchange(ref dur, (long)media.NaturalDuration.TimeSpan.TotalMilliseconds);
            long d = Interlocked.Read(ref dur);
            seek.Maximum = d > 0 ? d : 1;
            timeText.Text = Upnp.Fmt(Interlocked.Read(ref pos)) + " / " + Upnp.Fmt(d);
            ApplyVolume();
            ApplyLayout();
            if (pendingSeek >= 0)
            {
                try { media.Position = TimeSpan.FromMilliseconds(pendingSeek); } catch (Exception) { }
                Interlocked.Exchange(ref pos, pendingSeek);
                pendingSeek = -1;
            }
            if (Device != null) Device.OnMediaReady();
            if (wantPlay) DoPlay();
        }

        private void OnEnded()
        {
            int g = gen;
            wantPlay = false;
            playing = false;
            ticker.Stop();
            Power.Release();
            Interlocked.Exchange(ref pos, Interlocked.Read(ref dur));
            UpdateButtons();
            if (Device != null) Device.OnPlayerState("STOPPED");
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += delegate
            {
                t.Stop();
                if (g == gen && !playing && kind != "image" && IsVisible) { uri = ""; ReleaseMedia(); Hide(); }
            };
            t.Start();
        }

        private void Fail(string msg)
        {
            int g = gen;
            ReleaseMedia();
            wantPlay = false;
            errorText.Text = msg;
            errorText.Visibility = Visibility.Visible;
            if (!IsVisible) ShowPlayer();
            ShowBar();
            if (Device != null) Device.OnPlayerState("STOPPED");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
            t.Tick += delegate
            {
                t.Stop();
                if (g == gen && errorText.Visibility == Visibility.Visible) { errorText.Visibility = Visibility.Collapsed; uri = ""; if (IsVisible) Hide(); }
            };
            t.Start();
        }

        // ------------------------------------------------------------------ pictures

        private void LoadImage(string u, int g)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bi.UriSource = new Uri(u);
                bi.EndInit();
                if (bi.IsDownloading)
                {
                    bi.DownloadCompleted += delegate { if (g == gen) ShowImage(bi); };
                    bi.DownloadFailed += delegate { if (g == gen) Fail("Can't open this picture"); };
                }
                else ShowImage(bi);
            }
            catch (Exception)
            {
                Fail("Can't open this picture");
            }
        }

        private void ShowImage(BitmapImage bi)
        {
            pic.Source = bi;
            ApplyLayout();
            if (Device != null) Device.OnMediaReady();
        }
    }
}
