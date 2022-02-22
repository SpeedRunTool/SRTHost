using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SRTHost.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [EnableCors("CORSPolicy")]
    public partial class PluginController : ControllerBase
    {
        private readonly ILogger<PluginController> logger;
        private readonly PluginSystem pluginSystem;

        public PluginController(ILogger<PluginController> logger, PluginSystem pluginSystem)
        {
            this.logger = logger;
            this.pluginSystem = pluginSystem;
        }

        // Plugins events
        private const string pluginControllerEventName = "Plugin Controller";
        [LoggerMessage(EventIds.PluginController + 0, LogLevel.Information, "PluginGet()", EventName = pluginControllerEventName)]
        private partial void LogPluginGet();

        [LoggerMessage(EventIds.PluginController + 1, LogLevel.Information, "PluginReloadGet()", EventName = pluginControllerEventName)]
        private partial void LogPluginReloadGet();

        [LoggerMessage(EventIds.PluginController + 2, LogLevel.Information, "PluginInfoGet({plugin})", EventName = pluginControllerEventName)]
        private partial void LogPluginInfoGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 3, LogLevel.Information, "PluginDataGet({plugin})", EventName = pluginControllerEventName)]
        private partial void LogPluginDataGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 4, LogLevel.Information, "PluginCommandGet({plugin}, {command}, {args})", EventName = pluginControllerEventName)]
        private partial void LogPluginCommandGet(string plugin, string command, string? args);


        // GET: api/v1/Plugin
        // Gets all plugins loaded by the system.
        [HttpGet(Name = "PluginGet")]
        public IActionResult PluginGet()
        {
            LogPluginGet();
            return Ok(pluginSystem.Plugins);
        }

        // GET: api/v1/Plugin/Reload
        [HttpGet("Reload", Name = "PluginReloadGet")]
        public async Task<IActionResult> PluginReloadGet()
        {
            LogPluginReloadGet();

            await pluginSystem.ReloadPlugins(CancellationToken.None);
            return Ok("Success");
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Info
        [HttpGet("{Plugin}/Info", Name = "PluginInfoGet")]
        public IActionResult PluginInfoGet(string plugin)
        {
            LogPluginInfoGet(plugin);

            if (string.IsNullOrWhiteSpace(plugin))
                return BadRequest("A plugin name must be provided.");

            IPlugin? iPlugin = pluginSystem.Plugins.ContainsKey(plugin) ? pluginSystem.Plugins[plugin] : null;
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

            PluginProducerStateValue? pluginState = pluginSystem.PluginProducersAndDependentUIs.Select(a => a.Key).Where(a => a.Plugin.TypeName == plugin).FirstOrDefault();
            if (pluginState != null)
                return Ok(pluginState.LastData);
            else
                return NotFound(string.Format("Producer plugin \"{0}\" not found.", plugin));
        }

        // GET: api/v1/Plugin/SRTPluginProducerRE2/Roar?Name=Burrito
        // SRTPluginProducerRE2.CommandHandler("Roar", { "Name", { "Burrito" } });
        [HttpGet("{Plugin}/{Command}", Name = "PluginCommandGet")]
        public IActionResult PluginCommandGet(string plugin, string command)
        {
            KeyValuePair<string, string[]>[] args = HttpContext.Request.Query.Select(a => new KeyValuePair<string, string[]>(a.Key, a.Value.ToArray())).ToArray();
            LogPluginCommandGet(plugin, command, args.ToString());

            if (string.IsNullOrWhiteSpace(plugin))
                return BadRequest("A plugin name must be provided.");

            if (string.IsNullOrWhiteSpace(command))
                return BadRequest("A command must be provided.");

            IPlugin? iPlugin = pluginSystem.Plugins.ContainsKey(plugin) ? pluginSystem.Plugins[plugin] : null;
            if (iPlugin != null)
            {
                object? value = iPlugin.CommandHandler(command, args, out HttpStatusCode httpStatusCode);
                return StatusCode((int)httpStatusCode, value);
            }
            else
                return NotFound(string.Format("Plugin \"{0}\" not found.", plugin));
        }
    }
}
