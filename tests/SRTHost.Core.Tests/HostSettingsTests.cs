using Microsoft.Extensions.Logging;
using SRTHost.Core.Supervision;

namespace SRTHost.Core.Tests;

/// <summary>
/// The host's own settings file.
/// </summary>
/// <remarks>
/// These do not touch <see cref="HostSettings.Load"/> or <see cref="HostSettings.Save"/>, which read
/// and write a fixed path under <c>%LOCALAPPDATA%</c> - a test that wrote there would clobber the
/// developer's real settings. What is worth asserting is the shape that travels through that file
/// and the defaults a damaged one falls back to, and both are reachable without it.
/// </remarks>
public class HostSettingsTests
{
    [Fact]
    public void RoundTripsThroughJson()
    {
        HostSettings settings = new()
        {
            PluginsDirectory = @"S:\SpeedRunTool\plugins",
            LogLevel = LogLevel.Debug,
            RunnerLogLevel = LogLevel.Warning,
            LogRetention = 3,
            CloseToTray = false,
            StartMinimized = true,
            StartWithWindows = true,
            RestartPolicy = RestartPolicy.Always,
            MaximumRestarts = 9,
            DisabledPlugins = ["SpeedRunTool.Demo.Consumer"],
        };

        HostSettings restored = HostSettings.FromJson(HostSettings.ToJson(settings))!;

        Assert.Equal(settings.PluginsDirectory, restored.PluginsDirectory);
        Assert.Equal(settings.LogLevel, restored.LogLevel);
        Assert.Equal(settings.RunnerLogLevel, restored.RunnerLogLevel);
        Assert.Equal(settings.LogRetention, restored.LogRetention);
        Assert.Equal(settings.CloseToTray, restored.CloseToTray);
        Assert.Equal(settings.StartMinimized, restored.StartMinimized);
        Assert.Equal(settings.StartWithWindows, restored.StartWithWindows);
        Assert.Equal(settings.RestartPolicy, restored.RestartPolicy);
        Assert.Equal(settings.MaximumRestarts, restored.MaximumRestarts);
        Assert.Equal(settings.DisabledPlugins, restored.DisabledPlugins);
    }

    /// <summary>
    /// Enums are written as names, so a settings file stays readable and survives a reordered enum.
    /// </summary>
    [Fact]
    public void WritesEnumsAsNames()
    {
        string json = HostSettings.ToJson(
            new HostSettings { LogLevel = LogLevel.Warning, RestartPolicy = RestartPolicy.Never });

        Assert.Contains("\"Warning\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Never\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file written by a future version has to load, not throw.
    /// </summary>
    /// <remarks>
    /// The host's own settings are the one thing that must never need the UI to repair, because the
    /// UI is what they configure.
    /// </remarks>
    [Fact]
    public void IgnoresUnknownProperties()
    {
        HostSettings settings = HostSettings.FromJson(
            """{"logLevel":"Error","somethingFromTheFuture":42}""")!;

        Assert.Equal(LogLevel.Error, settings.LogLevel);
        Assert.True(settings.CloseToTray);
    }

    [Fact]
    public void CarriesTheKnobsThroughToRuntimeOptions()
    {
        HostRuntimeOptions options = new HostSettings
        {
            PluginsDirectory = @"S:\plugins",
            RunnerLogLevel = LogLevel.Trace,
            RestartPolicy = RestartPolicy.Never,
            MaximumRestarts = 2,
            DisabledPlugins = ["Demo.Off"],
        }.ToRuntimeOptions();

        Assert.Equal(@"S:\plugins", options.PluginsDirectory);
        Assert.Equal(LogLevel.Trace, options.Supervisor.RunnerLogLevel);
        Assert.Equal(RestartPolicy.Never, options.Supervisor.RestartPolicy);
        Assert.Equal(2, options.Supervisor.MaximumRestarts);

        // Case-insensitive, because the id comes off disk as a folder name and Windows paths are not
        // case-sensitive.
        Assert.Contains("demo.off", options.DisabledPlugins);
    }

    /// <summary>An empty plugins directory means "beside the executable", not an empty path.</summary>
    [Fact]
    public void FallsBackToTheDefaultPluginsDirectory()
    {
        Assert.Equal(
            HostPaths.DefaultPluginsDirectory,
            new HostSettings { PluginsDirectory = null }.ToRuntimeOptions().PluginsDirectory);

        Assert.Equal(
            HostPaths.DefaultPluginsDirectory,
            new HostSettings { PluginsDirectory = "  " }.ToRuntimeOptions().PluginsDirectory);
    }
}
