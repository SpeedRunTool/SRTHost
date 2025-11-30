using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace SRTHost.Controllers
{
    public partial class DebugController : Controller
    {
        // Debug events
        private const string DEBUG_CONTROLLER_EVENT_NAME = "Debug Controller";

        [LoggerMessage(EventIds.DebugController + 0, LogLevel.Information, "LogDebugGenerateMainJsonGet()", EventName = DEBUG_CONTROLLER_EVENT_NAME)]
        private partial void LogDebugGenerateMainJsonGet();

        [LoggerMessage(EventIds.DebugController + 1, LogLevel.Information, "LogDebugGenerateManifestHostGet()", EventName = DEBUG_CONTROLLER_EVENT_NAME)]
        private partial void LogDebugGenerateManifestHostGet();
    }
}
