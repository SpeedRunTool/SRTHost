using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner;

/// <summary>
/// The runner's <see cref="IPluginContext"/> - everything a plugin is handed at initialisation.
/// </summary>
internal sealed class RunnerPluginContext : IPluginContext
{
#if x64
    private const PluginArchitecture RunnerArchitecture = PluginArchitecture.X64;
#else
    private const PluginArchitecture RunnerArchitecture = PluginArchitecture.X86;
#endif

    private IPluginInfo? info;
    private IServiceProvider? services;

    /// <summary>Creates the context for one plugin.</summary>
    public RunnerPluginContext(string pluginId, string pluginDirectory, CancellationToken stopping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);

        PluginDirectory = pluginDirectory;
        Stopping = stopping;

        StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SRTHost",
            "state",
            pluginId);

        // Created eagerly, because the contract promises a writable directory and a plugin that has
        // to create it itself will get it wrong in a different way each time.
        Directory.CreateDirectory(StateDirectory);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Set immediately after the container is built. The context is registered inside that same
    /// container, so it has to exist before the container does; closing the loop afterwards is what
    /// keeps there being exactly one container rather than two that disagree about singletons.
    /// </remarks>
    public IServiceProvider Services
        => services ?? throw new InvalidOperationException(
            $"{nameof(Services)} is not available until the plugin container has been built.");

    /// <inheritdoc />
    /// <remarks>
    /// Filled in immediately after the plugin instance is constructed and before
    /// <see cref="IPlugin.InitializeAsync"/> runs, which is the earliest point it can be known: the
    /// authoritative <see cref="IPluginInfo"/> is the one the plugin's own <c>Info</c> property
    /// returns, and that property cannot be read until the instance exists - while the instance
    /// needs this context to be constructible in the first place.
    /// <para>
    /// Reading it before then is a programming error inside the runner, not a runtime condition, so
    /// it throws rather than returning a placeholder that would quietly become a plugin's idea of
    /// its own identity.
    /// </para>
    /// </remarks>
    public IPluginInfo Info
        => info ?? throw new InvalidOperationException(
            $"{nameof(Info)} is not available until the plugin instance has been constructed.");

    /// <inheritdoc />
    public PluginArchitecture ProcessArchitecture => RunnerArchitecture;

    /// <inheritdoc />
    public string PluginDirectory { get; }

    /// <inheritdoc />
    public string StateDirectory { get; }

    /// <inheritdoc />
    public CancellationToken Stopping { get; }

    /// <summary>Publishes the plugin's own metadata into the context.</summary>
    public void SetInfo(IPluginInfo pluginInfo) => info = pluginInfo;

    /// <summary>Publishes the container the plugin was constructed from into the context.</summary>
    public void SetServices(IServiceProvider provider) => services = provider;
}
