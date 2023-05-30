using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using MudBlazor;
using SRTPluginBase;

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

        internal static string GetPluginName(string name, string page) => $"api/v1/Plugin/{name}/{page}";

        internal static string GetRunningStatusIcon(PluginStateValue<IPlugin> pluginStateValue)
        {
            switch (pluginStateValue.Status)
            {
                case PluginStatusEnum.NotLoaded:
                    return Icons.Material.Filled.DoNotDisturbOn;

                case PluginStatusEnum.Loaded:
                case PluginStatusEnum.Instantiated:
                    return Icons.Material.Filled.CheckCircle;

                case PluginStatusEnum.LoadingError:
                case PluginStatusEnum.InstantiationError:
                    return Icons.Material.Filled.Warning;

                default:
                    return Icons.Material.Filled.QuestionMark;
            }
        }

        internal static Color GetRunningStatusColor(PluginStateValue<IPlugin> pluginStateValue)
        {
            switch (pluginStateValue.Status)
            {
                case PluginStatusEnum.NotLoaded:
                    return Color.Tertiary;

                case PluginStatusEnum.Loaded:
                case PluginStatusEnum.Instantiated:
                    return Color.Success;

                case PluginStatusEnum.LoadingError:
                case PluginStatusEnum.InstantiationError:
                    return Color.Error;

                default:
                    return Color.Warning;
            }
        }
    }
}
