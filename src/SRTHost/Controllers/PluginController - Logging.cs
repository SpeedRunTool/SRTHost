using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace SRTHost.Controllers
{
    public partial class PluginController : Controller
    {
        // Plugins events
        private const string PLUGIN_CONTROLLER_EVENT_NAME = "Plugin Controller";

        [LoggerMessage(EventIds.PluginController + 0, LogLevel.Information, "PluginGet()", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginGet();

        [LoggerMessage(EventIds.PluginController + 1, LogLevel.Information, "PluginReloadAllGet()", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginReloadAllGet();

        [LoggerMessage(EventIds.PluginController + 2, LogLevel.Information, "PluginLoadGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginLoadGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 3, LogLevel.Information, "PluginUnloadGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginUnloadGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 4, LogLevel.Information, "PluginReloadGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginReloadGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 5, LogLevel.Information, "PluginInfoGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginInfoGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 6, LogLevel.Information, "PluginDataGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginDataGet(string plugin);

        [LoggerMessage(EventIds.PluginController + 7, LogLevel.Information, "PluginHttpHandlerGet({plugin}, {command})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginHttpHandlerGet(string plugin, string? command);

        [LoggerMessage(EventIds.PluginController + 8, LogLevel.Information, "PluginGenerateManifestGet({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginGenerateManifestGet(string plugin);
    }
}
