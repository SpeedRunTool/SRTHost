using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SRTPluginBase.Interfaces;
using System.Net.Http;

namespace SRTHost.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [EnableCors("CORSPolicy")]
    public partial class PluginController : Controller
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly ILogger<PluginController> logger;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly PluginHost pluginHost;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly IHttpClientFactory httpClientFactory;

        public PluginController(ILogger<PluginController> logger, PluginHost pluginHost, IHttpClientFactory httpClientFactory)
        {
            this.logger = logger;
            this.pluginHost = pluginHost;
            this.httpClientFactory = httpClientFactory;
        }

        // GET: api/v1/Plugin
        // Gets all plugins loaded by the system.
        [HttpGet(Name = "PluginGet")]
        public IActionResult PluginGet()
        {
            LogPluginGet();
            return new JsonResult(
                pluginHost.LoadedPlugins.Keys,
                new JsonSerializerOptions()
                {
                    WriteIndented = true
                }
                );
        }

        // POST: api/v1/Plugin/ReloadAll
        [HttpPost("ReloadAll", Name = "PluginReloadAllPost")]
        public async Task<IActionResult> PluginReloadAllPost()
        {
            LogPluginReloadAllPost();

            try
            {
                await pluginHost.ReloadPluginsAsync(CancellationToken.None).ConfigureAwait(false);
                return new OkObjectResult("Plugins reloaded successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // POST: api/v1/Plugin/SRTPluginProducerRE2/Load
        [HttpPost("{Plugin}/Load", Name = "PluginLoadPost")]
        public async Task<IActionResult> PluginLoadPost(string plugin)
        {
            LogPluginLoadPost(plugin);

            try
            {
                // TODO: Implement load but not instantiate and expose either internal or public.
                //await pluginHost.LoadPlugin(plugin, CancellationToken.None);
                await Task.CompletedTask;
                return new OkObjectResult($"{plugin} loaded successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // POST: api/v1/Plugin/SRTPluginProducerRE2/Unload
        [HttpPost("{Plugin}/Unload", Name = "PluginUnloadPost")]
        public async Task<IActionResult> PluginUnloadPost(string plugin)
        {
            LogPluginUnloadPost(plugin);

            try
            {
                // TODO: Implement unload and expose either internal or public.
                //await pluginHost.UnloadPlugin(plugin, CancellationToken.None);
                await Task.CompletedTask;
                return new OkObjectResult($"{plugin} unloaded successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // POST: api/v1/Plugin/SRTPluginProducerRE2/Reload
        [HttpPost("{Plugin}/Reload", Name = "PluginReloadPost")]
        public async Task<IActionResult> PluginReloadPost(string plugin)
        {
            LogPluginReloadPost(plugin);

            try
            {
                await pluginHost.ReloadPluginAsync(plugin, CancellationToken.None);
                return new OkObjectResult($"{plugin} reloaded successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Info
        [HttpGet("{Plugin}/Info", Name = "PluginInfoGet")]
        public IActionResult PluginInfoGet(string plugin)
        {
            LogPluginInfoGet(plugin);

            if (string.IsNullOrWhiteSpace(plugin))
                return BadRequest("A plugin name must be provided.");

            IPluginStateValue<IPlugin>? pluginStateValue = pluginHost.LoadedPlugins.ContainsKey(plugin) ? pluginHost.LoadedPlugins[plugin] : null;
            if (pluginStateValue is not null && pluginStateValue.Status == PluginStatusEnum.Instantiated)
                return new JsonResult(
                    pluginStateValue.Plugin!.Info,
                    new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    }
                    );
            else if (pluginStateValue is not null && pluginStateValue.Plugin is null)
                return UnprocessableEntity($"Plugin \"{plugin}\" is loaded but is either not instantiated or is null.");
            else
                return NotFound(string.Format("Plugin \"{0}\" not found.", plugin));
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Data
        [HttpGet("{Plugin}/Data", Name = "PluginDataGet")]
        public IActionResult PluginDataGet(string plugin)
        {
            LogPluginDataGet(plugin);

            if (string.IsNullOrWhiteSpace(plugin))
                return BadRequest("A plugin name must be provided.");

            IPluginStateValue<IPlugin>? pluginStateValue = pluginHost.LoadedPlugins.ContainsKey(plugin) ? pluginHost.LoadedPlugins[plugin] : null;
            if (pluginStateValue is not null && pluginStateValue.Plugin is IPluginProducer pluginProducer)
                return new JsonResult(
                    pluginProducer.Refresh(),
                    new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    });
            else if (pluginStateValue is not null && pluginStateValue.Plugin is null)
                return UnprocessableEntity($"Plugin \"{plugin}\" is loaded but is either not instantiated or is null.");
            else
                return NotFound(string.Format("Producer plugin \"{0}\" not found.", plugin));
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Manifest
        [HttpGet("{Plugin}/Manifest", Name = "PluginManifestGet")]
        public async Task<IActionResult> PluginManifestGet(string plugin)
        {
            LogPluginGenerateManifestGet(plugin);

            if (string.IsNullOrWhiteSpace(plugin))
                return BadRequest("A plugin name must be provided.");

            IPluginStateValue<IPlugin>? pluginStateValue = pluginHost.LoadedPlugins.ContainsKey(plugin) ? pluginHost.LoadedPlugins[plugin] : null;
            if (pluginStateValue is not null && pluginStateValue.Status == PluginStatusEnum.Instantiated)
            {
                List<string> tags = new List<string>();
                if (pluginStateValue.PluginType?.IsAssignableTo(typeof(IPluginProducer)) ?? false)
                    tags.Add("Producer");
                else if (pluginStateValue.PluginType?.IsAssignableTo(typeof(IPluginConsumer)) ?? false)
                    tags.Add("Consumer");

                IMainPluginEntry manifest = await pluginHost.githubAPIHandler.GetPluginEntryAsync(logger, plugin);
                await manifest.SetManifestAsync(httpClientFactory.CreateClient().ConfigureHttpClient(new Uri("https://github.com/")));
                return new JsonResult(
                    manifest.Manifest,
                    new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    }
                    );
            }
            else if (pluginStateValue is not null && pluginStateValue.Plugin is null)
                return UnprocessableEntity($"Plugin \"{plugin}\" is loaded but is either not instantiated or is null.");
            else
                return NotFound(string.Format("Plugin \"{0}\" not found.", plugin));
        }
    }
}
