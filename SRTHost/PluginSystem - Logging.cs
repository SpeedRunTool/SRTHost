using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace SRTHost
{
    public partial class PluginSystem : BackgroundService
    {
        private readonly ILogger<PluginSystem> logger;

        // Version Banner events
        private const int versionBannerEventId = 1;
        private const string versionBannerEventName = "Version Banner";
        [LoggerMessage(versionBannerEventId, LogLevel.Information, "{productName} v{productVersion} {productArchitecture}", EventName = versionBannerEventName)]
        private partial void LogVersionBanner(string? productName, string? productVersion, string? productArchitecture);

        // Command line argument events
        private const int commandLineArgsEventId = 2;
        private const string commandLineArgsEventName = "Command Line Arguments";
        [LoggerMessage(commandLineArgsEventId, LogLevel.Information, "Command-line arguments:", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineBanner();
        [LoggerMessage(commandLineArgsEventId, LogLevel.Information, "{key}: {value}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineKeyValue(string? key, string? value);
        [LoggerMessage(commandLineArgsEventId, LogLevel.Information, "{key}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineKey(string? key);
        [LoggerMessage(commandLineArgsEventId, LogLevel.Information, "Arguments and examples", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpBanner();
        [LoggerMessage(commandLineArgsEventId, LogLevel.Information, "--{key}: {description}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpEntry(string? key, string? description);
        [LoggerMessage(commandLineArgsEventId, LogLevel.Information, "--{key}=<Value>: {description}\r\nExample: --{key}={defaultValue}", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpEntryValue(string? key, string? description, string? defaultValue);
        [LoggerMessage(commandLineArgsEventId, LogLevel.Error, "Error: {value} cannot be less than 16ms or greater than 2000ms. Resetting to default (66ms)", EventName = commandLineArgsEventName)]
        private partial void LogCommandLineHelpUpdateRateOutOfRange(string? value);

        // Loaded Host event
        private const int loadedHostEventId = 3;
        private const string loadedHostEventName = "Loaded Host";
        [LoggerMessage(loadedHostEventId, LogLevel.Information, "Loaded host: {hostName}", EventName = loadedHostEventName)]
        private partial void LogLoadedHost(string? hostName);

        // Loaded Plugin event
        private const int loadedPluginEventId = 4;
        private const string loadedPluginEventName = "Loaded Plugin";
        [LoggerMessage(loadedPluginEventId, LogLevel.Information, "Loaded plugin: {pluginName}", EventName = loadedPluginEventName)]
        private partial void LogLoadedPlugin(string? pluginName);

        // Signing Info events
        private const int signingInfoEventId = 5;
        private const string signingInfoEventName = "Signing Info";
        [LoggerMessage(signingInfoEventId, LogLevel.Information, "Digitally signed and verified: {sigSubject} [Thumbprint: {sigThumbprint}]", EventName = signingInfoEventName)]
        private partial void LogSigningInfoVerified(string? sigSubject, string? sigThumbprint);
        [LoggerMessage(signingInfoEventId, LogLevel.Warning, "Digitally signed but NOT verified: {sigSubject} [Thumbprint: {sigThumbprint}]", EventName = signingInfoEventName)]
        private partial void LogSigningInfoNotVerified(string? sigSubject, string? sigThumbprint);
        [LoggerMessage(signingInfoEventId, LogLevel.Warning, "No digital signature found", EventName = signingInfoEventName)]
        private partial void LogSigningInfoNotFound();

        // Plugin Version events
        private const int pluginVersionEventId = 6;
        private const string pluginVersionEventName = "Plugin Version";
        [LoggerMessage(pluginVersionEventId, LogLevel.Information, "Version v{version}", EventName = pluginVersionEventName)]
        private partial void LogPluginVersion(string? version);

        // Exception events
        private const int exceptionEventId = 7;
        private const string exceptionEventName = "Exception";
        [LoggerMessage(exceptionEventId, LogLevel.Critical, "[{exName}] {exString}", EventName = exceptionEventName)]
        private partial void LogException(string? exName, string? exString);

        // Incorrect Architecture events
        private const int incorrectArchitectureEventId = 8;
        private const string incorrectArchitectureEventName = "Incorrect Architecture";
#if x64
        [LoggerMessage(incorrectArchitectureEventId, LogLevel.Warning, "Failed plugin: \"{pluginPath}\"\r\nIncorrect architecture. SRT Host 64-bit (x64) cannot load a 32-bit (x86) DLL", EventName = incorrectArchitectureEventName)]
#else
        [LoggerMessage(incorrectArchitectureEventId, LogLevel.Warning, "Failed plugin: \"{pluginPath}\"\r\nIncorrect architecture. SRT Host 32-bit (x86) cannot load a 64-bit (x64) DLL", EventName = incorrectArchitectureEventName)]
#endif
        private partial void LogIncorrectArchitecturePlugin(string? pluginPath);
#if x64
        [LoggerMessage(incorrectArchitectureEventId, LogLevel.Warning, "Failed plugin: \"plugins\\x5C{sourcePlugin}\\x5C{sourcePlugin}.dll\"\r\nIncorrect architecture in referenced assembly \"{assemblyName}\". SRT Host 64-bit (x64) cannot load a 32-bit (x86) DLL", EventName = incorrectArchitectureEventName)]
#else
        [LoggerMessage(incorrectArchitectureEventId, LogLevel.Warning, "Failed plugin: \"plugins\\x5C{sourcePlugin}\\x5C{sourcePlugin}.dll\"\r\nIncorrect architecture in referenced assembly \"{assemblyName}\". SRT Host 32-bit (x86) cannot load a 64-bit (x64) DLL", EventName = incorrectArchitectureEventName)]
#endif
        private partial void LogIncorrectArchitecturePluginReference(string? sourcePlugin, string? assemblyName);

        // Plugin Startup events
        private const int pluginStartupEventId = 9;
        private const string pluginStartupEventName = "Plugin Startup";
        [LoggerMessage(pluginStartupEventId, LogLevel.Information, "[{pluginName}] successfully started", EventName = pluginStartupEventName)]
        private partial void LogPluginStartupSuccess(string? pluginName);
        [LoggerMessage(pluginStartupEventId, LogLevel.Error, "[{pluginName}] failed to startup properly with status {statusCode}", EventName = pluginStartupEventName)]
        private partial void LogPluginStartupFailure(string? pluginName, int statusCode);

        // Plugin Receive Data events
        private const int pluginReceiveDataEventId = 10;
        private const string pluginReceiveDataEventName = "Plugin Receive Data";
        [LoggerMessage(pluginReceiveDataEventId, LogLevel.Trace, "[{pluginName}] successfully received data", EventName = pluginReceiveDataEventName)]
        private partial void LogPluginReceiveDataSuccess(string? pluginName);
        [LoggerMessage(pluginReceiveDataEventId, LogLevel.Debug, "[{pluginName}] failed to receive data with status code {statusCode}", EventName = pluginReceiveDataEventName)]
        private partial void LogPluginReceiveDataFailure(string? pluginName, int statusCode);

        // Plugin Shutdown events
        private const int pluginShutdownEventId = 11;
        private const string pluginShutdownEventName = "Plugin Shutdown";
        [LoggerMessage(pluginShutdownEventId, LogLevel.Information, "[{pluginName}] successfully shutdown", EventName = pluginShutdownEventName)]
        private partial void LogPluginShutdownSuccess(string? pluginName);
        [LoggerMessage(pluginShutdownEventId, LogLevel.Error, "[{pluginName}] failed to shutdown properly with status {statusCode}", EventName = pluginShutdownEventName)]
        private partial void LogPluginShutdownFailure(string? pluginName, int statusCode);

        // Exit Helper events
        private const int exitHelperEventId = 12;
        private const string exitHelperEventName = "Exit Helper";
        [LoggerMessage(exitHelperEventId, LogLevel.Information, "Press CTRL+C in this console window to shutdown the SRT", EventName = exitHelperEventName)]
        private partial void LogExitHelper();
    }
}
