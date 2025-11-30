using System;
using System.Linq;
using SRTHost.Exceptions.HTTP;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SRTPluginBase.Implementations;
using SRTPluginBase.Interfaces;
using System.Net.Http;

namespace SRTHost.APIs
{
    internal class GithubAPIHandler
    {
        private IMainJson? main = default;
        private IMainHostEntry[]? hosts = default;
        private IMainPluginEntry[]? plugins = default;

        private readonly HttpClient httpClient;
        internal GithubAPIHandler(IHttpClientFactory httpClientFactory)
        {
            httpClient = httpClientFactory.CreateClient().ConfigureHttpClient(new Uri("https://github.com/"));
        }

        internal async Task RefreshAsync(ILogger logger)
        {
            main = await SRTPluginBase.Helpers.GetSRTJsonAsync<MainJson>(httpClient, "SpeedRunTool/SRTPlugins/manifest.json");

            hosts = main?.Hosts.Select(async a =>
            {
                try
                {
                    await a.SetManifestAsync(httpClient);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error occurred trying to set manifest on host [{a.Name}]({a.ManifestURL})!{Environment.NewLine}{ex}");
                }
                return a;
            }).Select(a => a.Result).ToArray();

            plugins = main?.Plugins.Select(async a =>
            {
                try
                {
                    await a.SetManifestAsync(httpClient);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error occurred trying to set manifest on plugin [{a.Name}]({a.ManifestURL})!{Environment.NewLine}{ex}");
                }
                return a;
            }).Select(a => a.Result).ToArray();
        }

        internal async Task<IMainJson> GetMainEntryAsync(ILogger logger)
        {
            if (main is null)
                await RefreshAsync(logger);

            return main ?? throw new HTTPManifestNotFoundException();
        }

        internal async Task<IMainHostEntry> GetHostEntryAsync(ILogger logger, string hostName)
        {
            if (hosts is null)
                await RefreshAsync(logger);

            return hosts?.First(a => a.Name == hostName) ?? throw new HTTPHostNotFoundException(hostName);
        }

        internal async Task<IMainPluginEntry> GetPluginEntryAsync(ILogger logger, string pluginName)
        {
            if (plugins is null)
                await RefreshAsync(logger);

            return plugins?.First(a => a.Name == pluginName) ?? throw new HTTPPluginNotFoundException(pluginName);
        }
    }
}
