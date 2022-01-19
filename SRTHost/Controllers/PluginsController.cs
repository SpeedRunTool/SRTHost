using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace SRTHost.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [EnableCors("CORSPolicy")]
    public partial class PluginsController : ControllerBase
    {
        private readonly ILogger<PluginsController> logger;
        private readonly PluginSystem pluginSystem;

        public PluginsController(ILogger<PluginsController> logger, PluginSystem pluginSystem)
        {
            this.logger = logger;
            this.pluginSystem = pluginSystem;
        }

        // Plugins events
        private const int pluginsControllerEventId = 1;
        private const string pluginsControllerEventName = "Plugins Controller";
        [LoggerMessage(pluginsControllerEventId, LogLevel.Information, "Get()", EventName = pluginsControllerEventName)]
        private partial void LogPluginsGet();

        [LoggerMessage(pluginsControllerEventId, LogLevel.Information, "ReloadGet()", EventName = pluginsControllerEventName)]
        private partial void LogPluginsReloadGet();

        // GET: api/v1/Plugins
        [HttpGet("", Name = "Get")]
        public IActionResult Get()
        {
            LogPluginsGet();
            return Ok(pluginSystem.Plugins);
        }

        // GET: api/v1/Plugins/Reload
        [HttpGet("Reload", Name = "ReloadGet")]
        public async Task<IActionResult> ReloadGet()
        {
            LogPluginsReloadGet();
            await pluginSystem.ReloadPlugins(CancellationToken.None);
            return Ok("Success");
        }
    }
}
