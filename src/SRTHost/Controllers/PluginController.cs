using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

        public PluginController(ILogger<PluginController> logger, PluginHost pluginHost)
        {
            this.logger = logger;
            this.pluginHost = pluginHost;
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

        // GET: api/v1/Plugin/ReloadAll
        [HttpGet("ReloadAll", Name = "PluginReloadAllGet")]
        public async Task<IActionResult> PluginReloadAllGet()
        {
            LogPluginReloadAllGet();

            try
            {
                await pluginHost.ReloadPluginsAsync(CancellationToken.None).ConfigureAwait(false);
                return LocalRedirect("/OperationStatus/reload%20all/successfully");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Load
        [HttpGet("{Plugin}/Load", Name = "PluginLoadGet")]
        public async Task<IActionResult> PluginLoadGet(string plugin)
        {
            LogPluginLoadGet(plugin);

            try
            {
                // TODO: Implement load but not instantiate and expose either internal or public.
                //await pluginHost.LoadPlugin(plugin, CancellationToken.None);
                await Task.CompletedTask;
                return LocalRedirect("/OperationStatus/load/successfully");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Unload
        [HttpGet("{Plugin}/Unload", Name = "PluginUnloadGet")]
        public async Task<IActionResult> PluginUnloadGet(string plugin)
        {
            LogPluginUnloadGet(plugin);

            try
            {
                // TODO: Implement unload and expose either internal or public.
                //await pluginHost.UnloadPlugin(plugin, CancellationToken.None);
                await Task.CompletedTask;
                return LocalRedirect("/OperationStatus/unload/successfully");
            }
            catch (Exception ex)
            {
                return StatusCode((int)HttpStatusCode.InternalServerError, ex);
            }
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Reload
        [HttpGet("{Plugin}/Reload", Name = "PluginReloadGet")]
        public async Task<IActionResult> PluginReloadGet(string plugin)
        {
            LogPluginReloadGet(plugin);

            try
            {
                await pluginHost.ReloadPluginAsync(plugin, CancellationToken.None);
                return LocalRedirect("/OperationStatus/reload/successfully");
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

        // GET: api/v1/Plugin/SRTPluginProducerRE2/GenerateManifest
        [HttpGet("{Plugin}/GenerateManifest", Name = "PluginGenerateManifestGet")]
        public IActionResult PluginGenerateManifestGet(string plugin)
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

                Version pluginVersion = pluginStateValue.Plugin?.Info.Version ?? new Version();
                return new JsonResult(
                    new ManifestPluginJson()
                    {
                        Contributors = pluginStateValue.Plugin?.Info.Author.Split(new string[] { ",", "&", "and", "/", "\\" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? Enumerable.Empty<string>(),
                        Tags = tags,
                        Releases = new ManifestReleaseJson[]
                        {
                            new ManifestReleaseJson()
                            {
                                Version = $"{pluginVersion}",
                                DownloadURL = new Uri($"https://github.com/REPLACEME_YourUSERorORGANIZATION/{plugin}/releases/download/{pluginVersion}/{plugin}-v{pluginVersion}.zip"),
                            },
                        },
                    },
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

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Roar?Name=Burrito
        // SRTPluginProducerRE2.HttpHandler(this);
        [HttpGet("{Plugin}/{**Command}", Name = "PluginHttpHandlerGet")]
        public async Task<IActionResult> PluginHttpHandlerGet(string plugin, string? command)
        {
            LogPluginHttpHandlerGet(plugin, command);

            if (string.IsNullOrWhiteSpace(plugin))
                return BadRequest("A plugin name must be provided.");

            IPlugin? iPlugin = pluginHost.LoadedPlugins.ContainsKey(plugin) ? pluginHost.LoadedPlugins[plugin].Plugin : null;
            if (iPlugin is not null)
            {
                if (command is not null && iPlugin.RegisteredPages.ContainsKey(command))
                    return await iPlugin.RegisteredPages[command].Invoke(this);
                else
                    return NotFound($"Plugin \"{plugin}\" does not have the command \"{command}\" registered.");
            }
            else
                return NotFound($"Plugin \"{plugin}\" not found.");
        }
    }
}
