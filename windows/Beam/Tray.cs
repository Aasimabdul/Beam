using System;
using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace Beam
{
    /// <summary>The notification-area icon and its menu: picture mode, rename, start with Windows, quit.</summary>
    public sealed class Tray : IDisposable
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private readonly Receiver rx;
        private readonly PlayerWindow player;
        private readonly Application app;
        private readonly WF.NotifyIcon icon;
        private readonly WF.ToolStripMenuItem titleItem;
        private readonly WF.ToolStripMenuItem[] modeItems = new WF.ToolStripMenuItem[Aspect.All.Length];
        private readonly WF.ToolStripMenuItem fullItem;
        private readonly WF.ToolStripMenuItem startupItem;

        public Tray(Receiver rx, PlayerWindow player, Application app)
        {
            this.rx = rx;
            this.player = player;
            this.app = app;

            var menu = new WF.ContextMenuStrip();
            titleItem = new WF.ToolStripMenuItem("Beam") { Enabled = false };
            menu.Items.Add(titleItem);
            menu.Items.Add(new WF.ToolStripSeparator());

            var show = new WF.ToolStripMenuItem("Show player");
            show.Click += delegate { player.ShowIfLoaded(); };
            menu.Items.Add(show);

            var picture = new WF.ToolStripMenuItem("Picture");
            for (int i = 0; i < Aspect.All.Length; i++)
            {
                AspectMode m = Aspect.All[i];
                var item = new WF.ToolStripMenuItem(Aspect.Label(m) + "  –  " + Aspect.Hint(m));
                item.Click += delegate { player.Dispatcher.Invoke(new Action(delegate { player.SetMode(m, false); })); };
                modeItems[i] = item;
                picture.DropDownItems.Add(item);
            }
            picture.DropDownOpening += delegate
            {
                for (int i = 0; i < modeItems.Length; i++) modeItems[i].Checked = Aspect.All[i] == rx.Cfg.Mode;
            };
            menu.Items.Add(picture);

            fullItem = new WF.ToolStripMenuItem("Open video full screen");
            fullItem.Click += delegate { rx.Cfg.Fullscreen = !rx.Cfg.Fullscreen; rx.Cfg.Save(); };
            menu.Items.Add(fullItem);

            var rename = new WF.ToolStripMenuItem("Rename receiver…");
            rename.Click += delegate { Rename(); };
            menu.Items.Add(rename);

            startupItem = new WF.ToolStripMenuItem("Start with Windows");
            startupItem.Click += delegate { SetStartup(!StartupOn()); };
            menu.Items.Add(startupItem);

            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit Beam");
            quit.Click += delegate { Quit(); };
            menu.Items.Add(quit);

            menu.Opening += delegate
            {
                titleItem.Text = "Beam – " + rx.Cfg.Name;
                show.Enabled = player.HasMedia;
                fullItem.Checked = rx.Cfg.Fullscreen;
                startupItem.Checked = StartupOn();
            };

            icon = new WF.NotifyIcon { Visible = true, ContextMenuStrip = menu, Icon = LoadIcon() };
            SetTip();
            icon.DoubleClick += delegate { player.ShowIfLoaded(); };
        }

        private static Icon LoadIcon()
        {
            try
            {
                Icon i = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
                if (i != null) return i;
            }
            catch (Exception) { }
            return SystemIcons.Application;
        }

        private void SetTip()
        {
            string t = "Beam – " + rx.Cfg.Name;
            icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        public void Balloon(string title, string text)
        {
            app.Dispatcher.BeginInvoke(new Action(delegate { icon.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.Info); }));
        }

        public void Ready()
        {
            if (!rx.Cfg.Welcomed)
            {
                rx.Cfg.Welcomed = true;
                rx.Cfg.Save();
                Balloon("Beam is ready", "Cast from your phone to \"" + rx.Cfg.Name + "\". If Windows Firewall asks, allow Private networks.");
            }
        }

        public void Error(string text)
        {
            Settings.Log("start failed: " + text);
            Balloon("Beam could not start", text);
        }

        private void Rename()
        {
            string n = Ask("Rename receiver", rx.Cfg.Name);
            if (n == null) return;
            n = n.Trim();
            if (n.Length == 0) return;
            if (n.Length > 40) n = n.Substring(0, 40);
            rx.Rename(n);
            SetTip();
        }

        private static string Ask(string title, string current)
        {
            string result = null;
            var w = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
            };
            var box = new TextBox { Text = current, MaxLength = 40, Margin = new Thickness(14, 14, 14, 8), FontSize = 15, Padding = new Thickness(4) };
            var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(4) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80, Padding = new Thickness(4) };
            ok.Click += delegate { result = box.Text; w.DialogResult = true; };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 0, 14, 14) };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            var col = new StackPanel();
            col.Children.Add(box);
            col.Children.Add(row);
            w.Content = col;
            w.Loaded += delegate { box.Focus(); box.SelectAll(); };
            w.ShowDialog();
            return result;
        }

        private static bool StartupOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Beam") != null;
            }
            catch (Exception) { return false; }
        }

        private static void SetStartup(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("Beam", "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue("Beam", false);
                }
            }
            catch (Exception ex) { Settings.Log("startup toggle failed: " + ex.Message); }
        }

        private void Quit()
        {
            player.Quitting = true;
            app.Shutdown();
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }
}
