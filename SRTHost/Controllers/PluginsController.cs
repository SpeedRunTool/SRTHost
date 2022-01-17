using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

        // GET: api/v1/Plugins
        [HttpGet("", Name = "Get")]
        public IActionResult Get()
        {
            return Ok(pluginSystem.Plugins);
        }
    }
}
