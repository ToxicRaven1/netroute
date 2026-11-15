// Live adapter facts from Windows.
//
// A connection names an adapter by its friendly name. The proxy needs the live
// facts: the IPv4 interface index (for IP_UNICAST_IF), the local IPv4 address,
// the DNS servers, and whether it is up. These change when the adapter
// reconnects, so MainForm re-reads them every couple of seconds and on every
// network-change event.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRoute
{
    public sealed class AdapterInfo
    {
        public string Name;          // Windows friendly name, e.g. "Wi-Fi 2"
        public string Description;   // hardware description, e.g. "Realtek USB Wi-Fi"
        public int Index;            // IPv4 interface index
        public IPAddress IPv4;       // first non-link-local IPv4 address, or null
        public bool HasGateway;      // true when an IPv4 default gateway is set
        public bool OperUp;          // OperationalStatus == Up
        public bool IsWireless;      // a Wi-Fi adapter: it can be told which network to join
        public List<IPAddress> DnsServers = new List<IPAddress>();

        /// Usable means: up, with a real IPv4 address, and an index we can bind to.
        public bool IsUsable
        {
            get { return OperUp && IPv4 != null && Index > 0; }
        }

        /// Looks like it reaches the internet on its own (has a default gateway).
        public bool LooksLikeInternet
        {
            get { return IsUsable && HasGateway; }
        }
    }

    public static class Adapters
    {
        /// Read every non-loopback adapter from Windows. Never throws.
        public static List<AdapterInfo> Read()
        {
            var list = new List<AdapterInfo>();
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { Log.Write("Could not read adapters: " + ex.Message); return list; }

            foreach (var nic in nics)
            {
                try
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue; // Teredo, ISATAP, 6to4

                    var a = new AdapterInfo();
                    a.Name = nic.Name;
                    a.Description = nic.Description;
                    a.OperUp = nic.OperationalStatus == OperationalStatus.Up;
                    a.IsWireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

                    var props = nic.GetIPProperties();
                    try
                    {
                        var v4 = props.GetIPv4Properties();
                        if (v4 != null) a.Index = v4.Index;
                    }
                    catch { a.Index = 0; }

                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var b = ua.Address.GetAddressBytes();
                        if (b[0] == 169 && b[1] == 254) continue; // link-local: no real network
                        a.IPv4 = ua.Address;
                        break;
                    }

                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork
                            && !gw.Address.Equals(IPAddress.Any))
                        {
                            a.HasGateway = true;
                            break;
                        }
                    }

                    foreach (var dns in props.DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork) a.DnsServers.Add(dns);
                    }

                    list.Add(a);
                }
                catch (Exception ex)
                {
                    Log.Write("Skipping adapter '" + nic.Name + "': " + ex.Message);
                }
            }

            // Usable ones first, then by name, so dropdowns show the live choices at the top.
            list.Sort(delegate (AdapterInfo x, AdapterInfo y)
            {
                int ux = x.LooksLikeInternet ? 0 : (x.IsUsable ? 1 : 2);
                int uy = y.LooksLikeInternet ? 0 : (y.IsUsable ? 1 : 2);
                if (ux != uy) return ux.CompareTo(uy);
                return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        public static AdapterInfo Find(List<AdapterInfo> adapters, string name)
        {
            if (adapters == null || string.IsNullOrEmpty(name)) return null;
            foreach (var a in adapters)
            {
                if (string.Equals(a.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        /// IP_UNICAST_IF (option 31) wants the IPv4 interface index in network byte order.
        public static byte[] UnicastIfValue(int index)
        {
            return BitConverter.GetBytes(IPAddress.HostToNetworkOrder(index));
        }

        public const SocketOptionName IP_UNICAST_IF = (SocketOptionName)31;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
        const uint HANDLE_FLAG_INHERIT = 1;

        /// Stop a socket handle from being inherited by programs netroute launches.
        /// Without this, a browser started from the Launch button would keep the
        /// proxy's listening port open after netroute exits, and the next start
        /// would find the port busy.
        public static void NoInherit(Socket s)
        {
            try { SetHandleInformation(s.Handle, HANDLE_FLAG_INHERIT, 0); } catch { }
        }

        /// Create a socket whose traffic leaves on this adapter and nowhere else.
        /// Both IP_UNICAST_IF and a bind to the adapter's own address are set, so
        /// the route table cannot pull the packet onto another adapter.
        public static Socket BoundSocket(AdapterInfo a, SocketType type, ProtocolType proto)
        {
            var s = new Socket(AddressFamily.InterNetwork, type, proto);
            NoInherit(s);
            try
            {
                s.SetSocketOption(SocketOptionLevel.IP, IP_UNICAST_IF, UnicastIfValue(a.Index));
                s.Bind(new IPEndPoint(a.IPv4, 0));
            }
            catch
            {
                s.Close();
                throw;
            }
            return s;
        }
    }
}
