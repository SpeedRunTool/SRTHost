using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SRTHost
{
    public partial class PluginSystem : BackgroundService, IHostedService
    {
        private readonly ILogger<PluginSystem> logger;

        // Version Banner events
        private const string versionBannerEventName = "Version Banner";
        [LoggerMessage(EventIds.PluginSystem + 0, LogLevel.Information, "{productName} v{productVersion} {productArchitecture}", EventName = versionBannerEventName)]
        private partial void LogVersionBanner(string? productName, string? productVersion, string? productArchitecture);

        // Command line argument events
        private const string commandLineArgsEventName = "Command Line Arguments";
        [LoggerMessage(EventIds.PluginSystem + 1, LogLevel.Information, "Command-line arguments:", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineBanner();

        [LoggerMessage(EventIds.PluginSystem + 2, LogLevel.Information, "{key}: {value}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineKeyValue(string? key, string? value);

        [LoggerMessage(EventIds.PluginSystem + 3, LogLevel.Information, "{key}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineKey(string? key);

        [LoggerMessage(EventIds.PluginSystem + 4, LogLevel.Information, "Arguments and examples", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpBanner();

        [LoggerMessage(EventIds.PluginSystem + 5, LogLevel.Information, "--{key}: {description}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpEntry(string? key, string? description);

        [LoggerMessage(EventIds.PluginSystem + 6, LogLevel.Information, "--{key}=<Value>: {description}\r\nExample: --{key}={defaultValue}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpEntryValue(string? key, string? description, string? defaultValue);

        [LoggerMessage(EventIds.PluginSystem + 7, LogLevel.Error, "{value} cannot be less than 16ms or greater than 2000ms. Resetting to default (66ms)", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpUpdateRateOutOfRange(string? value);

        // Load Host event
        private const string loadedHostEventName = "Load Host";
        [LoggerMessage(EventIds.PluginSystem + 8, LogLevel.Information, "Loaded host: {hostName}", EventName = loadedHostEventName)]
        private partial void LogLoadedHost(string? hostName);

        // Load Plugin event
        private const string loadedPluginEventName = "Load Plugin";
        [LoggerMessage(EventIds.PluginSystem + 9, LogLevel.Information, "Loaded plugin: {pluginName}", EventName = loadedPluginEventName)]
        private partial void LogLoadedPlugin(string? pluginName);

        [LoggerMessage(EventIds.PluginSystem + 10, LogLevel.Error, "Unable to find any plugins located in the \"plugins\" folder that implement IPlugin", EventName = loadedPluginEventName)]
        private partial void LogNoPlugins();

        // Signing Info events
        private const string signingInfoEventName = "Signing Info";
        [LoggerMessage(EventIds.PluginSystem + 11, LogLevel.Information, "Digitally signed and verified: {sigSubject} [Thumbprint: {sigThumbprint}]", EventName = signingInfoEventName)]
        private partial void LogSigningInfoVerified(string? sigSubject, string? sigThumbprint);

        [LoggerMessage(EventIds.PluginSystem + 12, LogLevel.Warning, "Digitally signed but NOT verified: {sigSubject} [Thumbprint: {sigThumbprint}]", EventName = signingInfoEventName)]
        private partial void LogSigningInfoNotVerified(string? sigSubject, string? sigThumbprint);

        [LoggerMessage(EventIds.PluginSystem + 13, LogLevel.Warning, "No digital signature found", EventName = signingInfoEventName)]
        private partial void LogSigningInfoNotFound();

        // Plugin Version events
        private const string pluginVersionEventName = "Plugin Version";
        [LoggerMessage(EventIds.PluginSystem + 14, LogLevel.Information, "Version v{version}", EventName = pluginVersionEventName)]
        private partial void LogPluginVersion(string? version);

        // Exception events
        private const string exceptionEventName = "Exception";
        [LoggerMessage(EventIds.PluginSystem + 15, LogLevel.Critical, "[{exName}] {exString}", EventName = exceptionEventName)]
        private partial void LogException(string? exName, string? exString);

        // Incorrect Architecture events
        private const string incorrectArchitectureEventName = "Incorrect Architecture";
#if x64
        [LoggerMessage(EventIds.PluginSystem + 16, LogLevel.Warning, "Failed plugin: \"{pluginPath}\"\r\nIncorrect architecture. " + APP_DISPLAY_NAME + " cannot load a " + APP_ARCHITECTURE_x86 + " DLL", EventName = incorrectArchitectureEventName)]
#else
        [LoggerMessage(EventIds.PluginSystem + 16, LogLevel.Warning, "Failed plugin: \"{pluginPath}\"\r\nIncorrect architecture. " + APP_DISPLAY_NAME + " cannot load a " + APP_ARCHITECTURE_x64 + " DLL", EventName = incorrectArchitectureEventName)]
#endif
        private partial void LogIncorrectArchitecturePlugin(string? pluginPath);

#if x64
        [LoggerMessage(EventIds.PluginSystem + 17, LogLevel.Warning, "Failed plugin: \"plugins\\x5C{sourcePlugin}\\x5C{sourcePlugin}.dll\"\r\nIncorrect architecture in referenced assembly \"{assemblyName}\". " + APP_DISPLAY_NAME + " cannot load a " + APP_ARCHITECTURE_x86 + " DLL", EventName = incorrectArchitectureEventName)]
#else
        [LoggerMessage(EventIds.PluginSystem + 17, LogLevel.Warning, "Failed plugin: \"plugins\\x5C{sourcePlugin}\\x5C{sourcePlugin}.dll\"\r\nIncorrect architecture in referenced assembly \"{assemblyName}\". " + APP_DISPLAY_NAME + " cannot load a " + APP_ARCHITECTURE_x64 + " DLL", EventName = incorrectArchitectureEventName)]
#endif
        private partial void LogIncorrectArchitecturePluginReference(string? sourcePlugin, string? assemblyName);

        // Plugin Startup events
        private const string pluginStartupEventName = "Plugin Startup";
        [LoggerMessage(EventIds.PluginSystem + 18, LogLevel.Information, "[{pluginName}] successfully started", EventName = pluginStartupEventName)]
        private partial void LogPluginStartupSuccess(string? pluginName);

        [LoggerMessage(EventIds.PluginSystem + 19, LogLevel.Error, "[{pluginName}] failed to startup properly with status {statusCode}", EventName = pluginStartupEventName)]
        private partial void LogPluginStartupFailure(string? pluginName, int statusCode);

        // Plugin Receive Data events
        private const string pluginReceiveDataEventName = "Plugin Receive Data";
        [LoggerMessage(EventIds.PluginSystem + 20, LogLevel.Trace, "[{pluginName}] successfully received data", EventName = pluginReceiveDataEventName)]
        private partial void LogPluginReceiveDataSuccess(string? pluginName);

        [LoggerMessage(EventIds.PluginSystem + 21, LogLevel.Debug, "[{pluginName}] failed to receive data with status code {statusCode}", EventName = pluginReceiveDataEventName)]
        private partial void LogPluginReceiveDataFailure(string? pluginName, int statusCode);

        // Plugin Shutdown events
        private const string pluginShutdownEventName = "Plugin Shutdown";
        [LoggerMessage(EventIds.PluginSystem + 22, LogLevel.Information, "[{pluginName}] successfully shutdown", EventName = pluginShutdownEventName)]
        private partial void LogPluginShutdownSuccess(string? pluginName);

        [LoggerMessage(EventIds.PluginSystem + 23, LogLevel.Error, "[{pluginName}] failed to shutdown properly with status {statusCode}", EventName = pluginShutdownEventName)]
        private partial void LogPluginShutdownFailure(string? pluginName, int statusCode);

        // Application Shutdown events
        private const string appShutdownEventName = "Application Shutdown";
        [LoggerMessage(EventIds.PluginSystem + 24, LogLevel.Information, "{appName} shutting down...", EventName = appShutdownEventName)]
        private partial void LogAppShutdown(string? appName);
    }
}
