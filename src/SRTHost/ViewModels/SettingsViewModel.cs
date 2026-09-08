using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SRTHost.Core;
using SRTHost.Core.Supervision;

namespace SRTHost.ViewModels;

/// <summary>
/// The host's own settings - not a plugin's, which arrive in Phase 6.
/// </summary>
/// <remarks>
/// Edits are held here and written on Save rather than applied as they are typed. Half of these are
/// read once during construction - the plugins directory, the log providers, the supervisor's
/// restart policy - so a page that wrote through on every keystroke would show a value that had not
/// taken effect and give no way to tell which ones had.
/// <para>
/// So the page says so instead: <see cref="RestartRequired"/> is shown after a save that changed one
/// of those fields. The alternative - restarting the runtime under the user when they change a
/// dropdown - would tear down every running plugin mid-game.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly HostContext host;
    private readonly ILogger logger;

    /// <summary>The levels the two log dropdowns offer.</summary>
    public IReadOnlyList<LogLevel> Levels { get; } =
        [LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical];

    /// <summary>The restart policies the dropdown offers.</summary>
    public IReadOnlyList<RestartPolicy> Policies { get; } =
        [RestartPolicy.OnCrash, RestartPolicy.Always, RestartPolicy.Never];

    [ObservableProperty]
    public partial string PluginsDirectory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LogLevel LogLevel { get; set; }

    [ObservableProperty]
    public partial LogLevel RunnerLogLevel { get; set; }

    [ObservableProperty]
    public partial int LogRetention { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial RestartPolicy RestartPolicy { get; set; }

    [ObservableProperty]
    public partial int MaximumRestarts { get; set; }

    /// <summary>What the last save did, or why it failed.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Whether the last save changed something that only takes effect on the next launch.</summary>
    [ObservableProperty]
    public partial bool RestartRequired { get; set; }

    /// <summary>Where this run's log file is, so the page can show it.</summary>
    public string LogFilePath => host.FileLogger.FilePath;

    /// <summary>Where the settings themselves live.</summary>
    public string SettingsFilePath => HostSettings.FilePath;

    /// <summary>Creates the page from the settings the host was built with.</summary>
    public SettingsViewModel(HostContext host)
    {
        ArgumentNullException.ThrowIfNull(host);

        this.host = host;
        logger = host.LoggerFactory.CreateLogger<SettingsViewModel>();

        Revert();
    }

    /// <inheritdoc />
    public override string Title => "Settings";

    /// <summary>Discards edits and re-reads the values the host is running with.</summary>
    [RelayCommand]
    private void Revert()
    {
        HostSettings settings = host.Settings;

        PluginsDirectory = settings.PluginsDirectory ?? HostPaths.DefaultPluginsDirectory;
        LogLevel = settings.LogLevel;
        RunnerLogLevel = settings.RunnerLogLevel;
        LogRetention = settings.LogRetention;
        CloseToTray = settings.CloseToTray;
        StartMinimized = settings.StartMinimized;
        RestartPolicy = settings.RestartPolicy;
        MaximumRestarts = settings.MaximumRestarts;

        // Read from the registry rather than from the file: the user may have turned the entry off
        // in Task Manager's Startup tab, which is where Windows tells them to, and the checkbox has
        // to agree with what is actually registered.
        StartWithWindows = StartupRegistration.IsRegistered();

        Status = null;
        RestartRequired = false;
    }

    /// <summary>Writes the settings file and applies what can be applied now.</summary>
    [RelayCommand]
    private void Save()
    {
        HostSettings previous = host.Settings;

        HostSettings updated = previous with
        {
            // Stored as null when it is the default, so a user who never changed it keeps following
            // the install rather than being pinned to today's path.
            PluginsDirectory = string.Equals(
                Path.TrimEndingDirectorySeparator(PluginsDirectory),
                Path.TrimEndingDirectorySeparator(HostPaths.DefaultPluginsDirectory),
                StringComparison.OrdinalIgnoreCase)
                ? null
                : PluginsDirectory,
            LogLevel = LogLevel,
            RunnerLogLevel = RunnerLogLevel,
            LogRetention = Math.Clamp(LogRetention, 1, 100),
            CloseToTray = CloseToTray,
            StartMinimized = StartMinimized,
            StartWithWindows = StartWithWindows,
            RestartPolicy = RestartPolicy,
            MaximumRestarts = Math.Clamp(MaximumRestarts, 0, 100),
        };

        if (StartupRegistration.Set(StartWithWindows) is { } registrationError)
        {
            Status = $"Could not change the sign-in entry: {registrationError}";
            logger.LogWarning("Could not change the sign-in entry: {Error}", registrationError);
            return;
        }

        if (updated.Save() is { } saveError)
        {
            Status = $"Could not save: {saveError}";
            logger.LogError("Could not save host settings: {Error}", saveError);
            return;
        }

        host.ApplySavedSettings(updated);

        // Close-to-tray and start-minimized are read at the moment they matter, so they are live
        // immediately. The rest were consumed during construction.
        RestartRequired =
            updated.PluginsDirectory != previous.PluginsDirectory
            || updated.LogLevel != previous.LogLevel
            || updated.RunnerLogLevel != previous.RunnerLogLevel
            || updated.LogRetention != previous.LogRetention
            || updated.RestartPolicy != previous.RestartPolicy
            || updated.MaximumRestarts != previous.MaximumRestarts;

        Status = RestartRequired
            ? "Saved. Some of these take effect the next time SRT Host starts."
            : "Saved.";

        logger.LogInformation("Host settings saved to {Path}.", HostSettings.FilePath);
    }
}
