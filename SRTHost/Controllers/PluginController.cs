using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System.Linq;
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
        [LoggerMessage(EventIds.PluginController + 0, LogLevel.Information, "Get()", EventName = pluginControllerEventName)]
        private partial void LogPluginGet();

        [LoggerMessage(EventIds.PluginController + 1, LogLevel.Information, "InfoGet({plugin})", EventName = pluginControllerEventName)]
        private partial void LogPluginInfoGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 2, LogLevel.Information, "DataGet({plugin})", EventName = pluginControllerEventName)]
        private partial void LogPluginDataGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 3, LogLevel.Information, "ReloadGet()", EventName = pluginControllerEventName)]
        private partial void LogPluginReloadGet();

        // GET: api/v1/Plugin
        [HttpGet(Name = "Get")]
        public IActionResult Get()
        {
            LogPluginGet();
            return Ok(pluginSystem.Plugins);
        }

        // GET: api/v1/Plugin/Info/SRTPluginProducerRE2
        [HttpGet("Info/{Plugin}", Name = "InfoGet")]
        public IActionResult InfoGet(string plugin)
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

        // GET: api/v1/Plugin/Data/SRTPluginProducerRE2
        [HttpGet("Data/{Plugin}", Name = "DataGet")]
        public IActionResult DataGet(string plugin)
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

        // GET: api/v1/Plugin/Reload
        [HttpGet("Reload", Name = "ReloadGet")]
        public async Task<IActionResult> ReloadGet()
        {
            LogPluginReloadGet();

            await pluginSystem.ReloadPlugins(CancellationToken.None);
            return Ok("Success");
        }
    }
}
