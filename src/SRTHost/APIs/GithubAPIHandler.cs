using System;
using System.Linq;
using SRTHost.Exceptions.HTTP;
using SRTPluginBase;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SRTHost.APIs
{
    internal class GithubAPIHandler : BaseAPIHandler
    {
        private MainJson? main = default;
        private MainHostEntry[]? hosts = default;
        private MainPluginEntry[]? plugins = default;

        internal GithubAPIHandler() : base()
        {
            client.BaseAddress = new Uri("https://github.com/");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        internal async Task RefreshAsync(ILogger logger)
        {
            main = await SRTPluginBase.Helpers.GetSRTJsonAsync<MainJson>(client, "SpeedRunTool/SRTPlugins/manifest.json");

            hosts = main?.Hosts.Select(async a =>
            {
                try
                {
                    await a.SetManifestAsync(client);
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
                    await a.SetManifestAsync(client);
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error occurred trying to set manifest on plugin [{a.Name}]({a.ManifestURL})!{Environment.NewLine}{ex}");
                }
                return a;
            }).Select(a => a.Result).ToArray();
        }

        internal async Task<MainJson> GetMainEntryAsync(ILogger logger)
        {
            if (main is null)
                await RefreshAsync(logger);

            return main ?? throw new HTTPManifestNotFoundException();
        }

        internal async Task<MainHostEntry> GetHostEntryAsync(ILogger logger, string hostName)
        {
            if (hosts is null)
                await RefreshAsync(logger);

            return hosts?.First(a => a.Name == hostName) ?? throw new HTTPHostNotFoundException(hostName);
        }

        internal async Task<MainPluginEntry> GetPluginEntryAsync(ILogger logger, string pluginName)
        {
            if (plugins is null)
                await RefreshAsync(logger);

            return plugins?.First(a => a.Name == pluginName) ?? throw new HTTPPluginNotFoundException(pluginName);
        }
    }
}
