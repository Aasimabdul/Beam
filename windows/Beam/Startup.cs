using System;
using System.Reflection;
using Microsoft.Win32;

namespace Beam
{
    /// <summary>"Start with Windows": a value under HKCU\...\Run (no admin rights needed). Autostart launches straight to the tray.</summary>
    internal static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool IsOn()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("Beam") != null;
            }
            catch (Exception) { return false; }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("Beam", "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue("Beam", false);
                }
            }
            catch (Exception ex) { Settings.Log("startup toggle failed: " + ex.Message); }
        }
    }
}
