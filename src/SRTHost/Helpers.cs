using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using MudBlazor;
using SRTPluginBase.Interfaces;

namespace SRTHost
{
    internal static class Helpers
    {
        internal static HttpClient ConfigureHttpClient(this HttpClient httpClient, Uri? baseUri = default, string acceptHeader = "application/json")
        {
            FileVersionInfo srtHostFileVersionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, PluginHost.APP_EXE_NAME));
            httpClient.DefaultRequestHeaders.Add("User-Agent", $"{srtHostFileVersionInfo.ProductName} v{srtHostFileVersionInfo.ProductVersion} {PluginHost.APP_ARCHITECTURE}");
            httpClient.DefaultRequestHeaders.Add("Accept", acceptHeader);
            httpClient.Timeout = new TimeSpan(0, 0, 10); // Might be too low but we'll see. May make this configurable.
            if (baseUri is not null)
                httpClient.BaseAddress = baseUri;
            
            return httpClient;
        }

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
