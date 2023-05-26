using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        // Plugins events
        private const string PLUGIN_CONTROLLER_EVENT_NAME = "Plugin Controller";
        [LoggerMessage(EventIds.PluginController + 0, LogLevel.Information, "PluginGet()", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginGet();

        [LoggerMessage(EventIds.PluginController + 1, LogLevel.Information, "PluginReloadAllGet()", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginReloadAllGet();

        [LoggerMessage(EventIds.PluginController + 2, LogLevel.Information, "PluginReloadGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginReloadGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 3, LogLevel.Information, "PluginInfoGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginInfoGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 4, LogLevel.Information, "PluginDataGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginDataGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 5, LogLevel.Information, "PluginGenerateManifestGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginGenerateManifestGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 6, LogLevel.Information, "PluginHttpHandlerGet({plugin}, {command})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginHttpHandlerGet(string plugin, string? command);


        // GET: api/v1/Plugin
        // Gets all plugins loaded by the system.
        [HttpGet(Name = "PluginGet")]
        public IActionResult PluginGet()
        {
            LogPluginGet();
            return Ok(pluginHost.LoadedPlugins.Keys);
        }

        // GET: api/v1/Plugin/Reload
        [HttpGet("Reload", Name = "PluginReloadAllGet")]
        public async Task<IActionResult> PluginReloadAllGet()
        {
            LogPluginReloadAllGet();

            try
            {
                await pluginHost.ReloadPlugins(CancellationToken.None);
                return Ok("Success");
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
                await pluginHost.ReloadPlugin(plugin, CancellationToken.None);
                return Ok("Success");
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

            IPlugin? iPlugin = pluginHost.LoadedPlugins.ContainsKey(plugin) ? pluginHost.LoadedPlugins[plugin].Plugin : null;
            if (iPlugin != null)
                return Ok(iPlugin);
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
            if (pluginStateValue != null && pluginStateValue.Plugin is IPluginProducer pluginProducer)
                return Ok(pluginProducer.Refresh());
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
            if (pluginStateValue != null)
            {
                List<string> tags = new List<string>();
                if (pluginStateValue.Plugin is IPluginProducer pluginProducer)
                    tags.Add("Producer");
                else if (pluginStateValue.Plugin is IPluginConsumer pluginConsumer)
                    tags.Add("Consumer");

                return new JsonResult(
                    new ManifestJson() { Contributors = pluginStateValue.Plugin.Info.Author.Split(new string[] { ",", "&", "and", "/", "\\" }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), Tags = tags },
                    new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    }
                    );
            }
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
            if (iPlugin != null)
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
