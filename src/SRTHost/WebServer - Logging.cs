using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SRTHost
{
    public partial class WebServer : IHostedService
    {
        private readonly ILogger<WebServer> logger;

        // Load Host event
        private const string LOADED_WEBSERVER_EVENT_NAME = "Load Web Server";
        [LoggerMessage(EventIds.WebServer + 8, LogLevel.Information, "Loaded web server.", EventName = LOADED_WEBSERVER_EVENT_NAME)]
        private partial void LogLoadedWebServer();
    }
}
