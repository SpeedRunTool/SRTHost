using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace SRTHost
{
    internal static class Helpers
    {
        private static IEnumerable<UnicastIPAddressInformation> GetIPs() => NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(a => a.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 || a.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                .Select(a => a.GetIPProperties())
                .Where(a => a.GatewayAddresses.Count > 0)
                .SelectMany(a => a.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                .Where(a => a.IsDnsEligible);

        internal static string? GetIPv4() => GetIPs().FirstOrDefault(ip => ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address?.ToString();
        internal static string? GetIPv6() => GetIPs().FirstOrDefault(ip => ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)?.Address?.ToString();
    }
}
