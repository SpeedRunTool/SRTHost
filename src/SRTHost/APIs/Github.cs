using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Collections.Generic;
using System.Linq;
using SRTHost.Exceptions.HTTP;
using SRTPluginBase;

namespace SRTHost.APIs
{
    internal class GithubAPIHandler : BaseAPIHandler
    {
        internal GithubAPIHandler() : base()
        {
            client.BaseAddress = new Uri("https://github.com/");
        }

        internal ManifestPluginJson GetPluginManifest(string pluginName)
        {
            // Get the list of manifests
            HttpResponseMessage manifestsResult = client.GetAsync("SpeedRunTool/SRTPlugins/plugins.json").Result;
            manifestsResult.EnsureSuccessStatusCode();

            List<ManifestPluginJson>? manifests = manifestsResult.Content.ReadFromJsonAsync<List<ManifestPluginJson>>().Result;
            ManifestPluginJson manifest = (manifests?.First(a => a.Name == pluginName)) ?? throw new HTTPPluginNotFoundException(pluginName);

            // Get the list of versions for the plugin from the plugin repo
            HttpResponseMessage versionsResult = client.GetAsync($"{manifest.RepoURL}/versions.json").Result;
            versionsResult.EnsureSuccessStatusCode();

            manifest.Releases = new List<ManifestReleaseJson>();
            List<ManifestReleaseJson>? versions = versionsResult.Content.ReadFromJsonAsync<List<ManifestReleaseJson>>().Result;
            if (versions != null && versions.Count > 0)
                manifest.Releases = versions;

            return manifest;
        }
    }
}
