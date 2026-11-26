using System;
using System.Drawing;
using System.Reflection;
using System.Windows;
using WF = System.Windows.Forms;

namespace Beam
{
    /// <summary>The notification-area icon and its menu: open Beam, picture mode, settings, start with Windows, quit.</summary>
    public sealed class Tray : IDisposable
    {
        private readonly Receiver rx;
        private readonly PlayerWindow player;
        private readonly HomeWindow home;
        private readonly Application app;
        private readonly WF.NotifyIcon icon;
        private readonly WF.ToolStripMenuItem titleItem;
        private readonly WF.ToolStripMenuItem[] modeItems = new WF.ToolStripMenuItem[Aspect.All.Length];
        private readonly WF.ToolStripMenuItem fullItem;
        private readonly WF.ToolStripMenuItem startupItem;

        public Tray(Receiver rx, PlayerWindow player, HomeWindow home, Application app)
        {
            this.rx = rx;
            this.player = player;
            this.home = home;
            this.app = app;

            var menu = new WF.ContextMenuStrip();
            titleItem = new WF.ToolStripMenuItem("Beam") { Enabled = false };
            menu.Items.Add(titleItem);
            menu.Items.Add(new WF.ToolStripSeparator());

            var open = new WF.ToolStripMenuItem("Open Beam");
            open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
            open.Click += delegate { home.Open(false); };
            menu.Items.Add(open);

            var show = new WF.ToolStripMenuItem("Show player");
            show.Click += delegate { player.ShowIfLoaded(); };
            menu.Items.Add(show);

            var picture = new WF.ToolStripMenuItem("Picture");
            for (int i = 0; i < Aspect.All.Length; i++)
            {
                AspectMode m = Aspect.All[i];
                var item = new WF.ToolStripMenuItem(Aspect.Label(m) + "  –  " + Aspect.Hint(m));
                item.Click += delegate { player.SetMode(m, false); };
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

            var settings = new WF.ToolStripMenuItem("Settings…");
            settings.Click += delegate { home.Open(true); };
            menu.Items.Add(settings);

            startupItem = new WF.ToolStripMenuItem("Start with Windows");
            startupItem.Click += delegate { Startup.Set(!Startup.IsOn()); };
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
                startupItem.Checked = Startup.IsOn();
            };

            icon = new WF.NotifyIcon { Visible = true, ContextMenuStrip = menu, Icon = LoadIcon() };
            SetTip();
            icon.DoubleClick += delegate { if (player.HasMedia) player.ShowIfLoaded(); else home.Open(false); };
            rx.NameChanged += delegate { app.Dispatcher.BeginInvoke(new Action(SetTip)); };
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
            rx.Error = text;
            Settings.Log("start failed: " + text);
            Balloon("Beam could not start", text);
        }

        private void Quit()
        {
            player.Quitting = true;
            home.Quitting = true;
            app.Shutdown();
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }
    }
}
