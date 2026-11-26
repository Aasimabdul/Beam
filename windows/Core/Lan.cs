using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Beam
{
    /// <summary>One usable LAN address: the interface, its IPv4 address and prefix length.</summary>
    public sealed class LanAddr
    {
        public NetworkInterface Nic;
        public IPAddress Ip;
        public int Prefix;
    }

    /// <summary>Finds this PC's LAN addresses (skips VPN, virtual-machine and link-local adapters).</summary>
    public static class Lan
    {
        private static readonly Regex Virtual = new Regex(
            "virtual|vmware|vbox|hyper-v|vethernet|pseudo|bluetooth|tap-|wintun|wireguard|tailscale|zerotier|npcap|loopback|docker|wsl",
            RegexOptions.IgnoreCase);

        public static List<LanAddr> Addresses()
        {
            var list = new List<LanAddr>();
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                            || ni.NetworkInterfaceType == NetworkInterfaceType.Ppp) continue;
                        if (!ni.SupportsMulticast) continue;
                        if (Virtual.IsMatch(ni.Description ?? "") || Virtual.IsMatch(ni.Name ?? "")) continue;
                        foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            byte[] b = ua.Address.GetAddressBytes();
                            if (b[0] == 169 && b[1] == 254) continue;
                            int prefix = ua.PrefixLength;
                            list.Add(new LanAddr { Nic = ni, Ip = ua.Address, Prefix = prefix });
                            break;
                        }
                    }
                    catch (Exception)
                    {
                        // an adapter that can't be read is skipped
                    }
                }
            }
            catch (Exception)
            {
                // none
            }
            return list;
        }

        /// <summary>The local IPv4 address on the same subnet as remote, else the first LAN address, else null.</summary>
        public static string LocalIpFor(IPAddress remote)
        {
            string first = null;
            foreach (LanAddr a in Addresses())
            {
                if (first == null) first = a.Ip.ToString();
                if (remote != null && SameSubnet(a.Ip.GetAddressBytes(), remote.GetAddressBytes(), a.Prefix)) return a.Ip.ToString();
            }
            return first;
        }

        public static bool SameSubnet(byte[] a, byte[] b, int prefix)
        {
            if (a.Length != b.Length) return false;
            int bits = prefix;
            for (int i = 0; i < a.Length && bits > 0; i++)
            {
                int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
                if ((a[i] & mask) != (b[i] & mask)) return false;
                bits -= 8;
            }
            return true;
        }
    }
}
