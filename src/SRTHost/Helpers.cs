using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

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

        internal static async Task<HttpResponseMessage> Request(this HttpClient httpClient, string method, Uri requestUri, HttpContent? httpContent = default)
        {
            using (HttpRequestMessage httpRequestMessage = new HttpRequestMessage()
            {
                Method = new HttpMethod(method),
                RequestUri = requestUri,
                Content = httpContent
            })
                return await httpClient.SendAsync(httpRequestMessage);
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
    }
}
