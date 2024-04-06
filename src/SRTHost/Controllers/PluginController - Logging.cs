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

        [LoggerMessage(EventIds.PluginController + 1, LogLevel.Information, "PluginReloadAllPost()", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginReloadAllPost();

        [LoggerMessage(EventIds.PluginController + 2, LogLevel.Information, "PluginLoadPost({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginLoadPost(string plugin);

        [LoggerMessage(EventIds.PluginController + 3, LogLevel.Information, "PluginUnloadPost({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginUnloadPost(string plugin);

        [LoggerMessage(EventIds.PluginController + 4, LogLevel.Information, "PluginReloadPost({plugin})", EventName = PLUGIN_CONTROLLER_EVENT_NAME)]
        private partial void LogPluginReloadPost(string plugin);

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
