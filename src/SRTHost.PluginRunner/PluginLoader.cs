using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner;

/// <summary>
/// A load or configuration failure the runner can classify, so the host shows a cause rather than a
/// stack trace.
/// </summary>
internal sealed class PluginLoadException(PluginSubStatus subStatus, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>What kind of failure this was.</summary>
    public PluginSubStatus SubStatus { get; } = subStatus;
}

/// <summary>
/// One loaded plugin and everything that has to be torn down with it.
/// </summary>
internal sealed class LoadedPlugin : IAsyncDisposable
{
    /// <summary>The plugin's stable id.</summary>
    public required string Id { get; init; }

    /// <summary>The plugin instance.</summary>
    public required IPlugin Instance { get; init; }

    /// <summary>The plugin's own view of its metadata.</summary>
    public required IPluginInfo Info { get; init; }

    /// <summary>The thread the plugin's code runs on.</summary>
    public required IPluginDispatcher Dispatcher { get; init; }

    /// <summary>The isolated context the plugin's assemblies were loaded into.</summary>
    public required PluginLoadContext LoadContext { get; init; }

    /// <summary>The container the plugin was constructed from.</summary>
    public required ServiceProvider Services { get; init; }

    /// <summary>The plugin as a producer, or null.</summary>
    public IProducerPlugin? Producer => Instance as IProducerPlugin;

    /// <summary>The plugin as a consumer, or null.</summary>
    public IConsumerPlugin? Consumer => Instance as IConsumerPlugin;

    /// <summary>The plugin as configurable, or null.</summary>
    public IConfigurablePlugin? Configurable => Instance as IConfigurablePlugin;

    /// <summary>Whether the plugin has been started and not yet stopped.</summary>
    public bool IsStarted { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// The load context is deliberately not unloaded here. A runner hosts one plugin, so the only
    /// thing that follows disposal is process exit, and <c>Unload</c> on the way out buys nothing
    /// while introducing a class of shutdown hang that is very hard to reproduce. Collectibility is
    /// retained for the configuration-only reload path, not for this one.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(
                async _ => await Instance.DisposeAsync().ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.DisposeAsync().ConfigureAwait(false);
            await Services.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Turns a <see cref="LoadPluginMessage"/> into a live plugin instance.
/// </summary>
internal static class PluginLoader
{
    /// <summary>
    /// Loads, constructs and initialises the plugin described by <paramref name="request"/>.
    /// </summary>
    /// <exception cref="PluginLoadException">
    /// The plugin could not be loaded, and the reason was classifiable.
    /// </exception>
    public static async Task<LoadedPlugin> LoadAsync(
        LoadPluginMessage request,
        ILoggerProvider loggerProvider,
        LogLevel minimumLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(loggerProvider);

        // The id becomes a directory name under the state root, so it is re-validated here even
        // though the build-time generator (SRT1007) and IpcProtocol.PipeName both check it: a
        // manifest can arrive beside a downloaded plugin having never passed through either.
        if (!IsSafeId(request.PluginId))
        {
            throw new PluginLoadException(
                PluginSubStatus.UndefinedException,
                $"Plugin id '{request.PluginId}' must be letters, digits, '.', '_' or '-' with no empty segments.");
        }

        string assemblyPath = Path.GetFullPath(Path.Combine(request.PluginDirectory, request.EntryAssembly));

        if (!File.Exists(assemblyPath))
        {
            throw new PluginLoadException(
                PluginSubStatus.DependencyNotFound,
                $"Plugin assembly '{assemblyPath}' does not exist.");
        }

        PluginLoadContext loadContext = new(assemblyPath);
        Assembly assembly;

        try
        {
            assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
        {
            // Nearly unreachable now: the router reads the architecture out of the manifest and
            // picks the matching runner before spawning anything. Kept because "nearly" is doing
            // real work there - a hand-assembled plugin folder need not have a manifest at all.
            throw new PluginLoadException(
                PluginSubStatus.IncorrectArchitecture,
                $"'{assemblyPath}' could not be loaded into the {RunnerArchitectureName} runner. "
                + "This usually means the plugin was built for the other bitness.",
                ex);
        }

        int generation = SrtContract.GenerationOf(assembly);

        if (generation != SrtContract.Generation)
        {
            throw new PluginLoadException(
                PluginSubStatus.ContractGenerationMismatch,
                $"'{request.EntryAssembly}' was built against contract generation {generation}; "
                + $"this host loads generation {SrtContract.Generation}.");
        }

        Type entryType = assembly.GetType(request.EntryType, throwOnError: false)
            ?? throw new PluginLoadException(
                PluginSubStatus.UndefinedException,
                $"'{request.EntryType}' was not found in '{request.EntryAssembly}'.");

        if (!typeof(IPlugin).IsAssignableFrom(entryType))
        {
            throw new PluginLoadException(
                PluginSubStatus.UndefinedException,
                $"'{request.EntryType}' does not implement {nameof(IPlugin)}.");
        }

        ServiceCollection services = new();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);
            builder.AddProvider(loggerProvider);
        });

        RunnerPluginContext context = new(
            request.PluginId,
            Path.GetFullPath(request.PluginDirectory),
            cancellationToken);

        // The context is registered as an instance and then handed the container it was registered
        // in. The circularity is real - a plugin resolves services through its context, and the
        // context is itself one of those services - and closing it after the fact is the only way to
        // end up with exactly one container rather than two that disagree about singletons.
        services.AddSingleton<IPluginContext>(context);

        ServiceProvider provider = services.BuildServiceProvider();
        context.SetServices(provider);

        IPluginDispatcher dispatcher = InlinePluginDispatcher.Instance;
        IPlugin instance;
        IPluginInfo info;

        try
        {
            instance = (IPlugin)ActivatorUtilities.CreateInstance(provider, entryType);
            info = instance.Info;

            // The dispatcher can only be chosen once the instance exists, because RequiresUiThread is
            // the plugin's own answer. Construction itself therefore always happens on the calling
            // thread - acceptable, and the reason the contract puts window creation in
            // InitializeAsync rather than in a constructor.
            if (info.RequiresUiThread)
            {
                if (!OperatingSystem.IsWindows())
                {
                    throw new PluginLoadException(
                        PluginSubStatus.InitializationFailure,
                        $"'{request.PluginId}' requires a UI thread, which only the Windows runner provides.");
                }

                dispatcher = new UiThreadDispatcher($"SRT Plugin UI - {request.PluginId}");
            }

            context.SetInfo(info);
        }
        catch (Exception ex)
        {
            await provider.DisposeAsync().ConfigureAwait(false);

            throw new PluginLoadException(
                PluginSubStatus.InitializationFailure,
                $"'{request.EntryType}' could not be constructed: {ex.Message}",
                ex);
        }

        LoadedPlugin loaded = new()
        {
            Id = request.PluginId,
            Instance = instance,
            Info = info,
            Dispatcher = dispatcher,
            LoadContext = loadContext,
            Services = provider,
        };

        try
        {
            await dispatcher.InvokeAsync(
                async token => await instance.InitializeAsync(context, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            // Skipped when the plugin already holds exactly these settings, which at startup it
            // usually does: the configurable base classes read the same file during Initialize that
            // the host read to fill this field. Applying anyway is harmless but not free - a plugin
            // reacts in OnConfigurationChangedAsync, and an overlay that rebuilds a render target
            // there would do it twice on every launch, once against settings it already had.
            if (request.ConfigurationJson is not null && !HasConfiguration(loaded, request.ConfigurationJson))
                await ApplyConfigurationAsync(loaded, request.ConfigurationJson, cancellationToken).ConfigureAwait(false);
        }
        catch (PluginLoadException)
        {
            await loaded.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await loaded.DisposeAsync().ConfigureAwait(false);

            throw new PluginLoadException(
                PluginSubStatus.InitializationFailure,
                $"'{request.PluginId}' failed to initialize: {ex.Message}",
                ex);
        }

        return loaded;
    }

    /// <summary>
    /// Deserialises <paramref name="configurationJson"/> into the plugin's configuration type and
    /// applies it.
    /// </summary>
    /// <remarks>
    /// The runner does this rather than the host because the configuration type lives in the
    /// plugin's own assembly, which the host process never loads - that is the whole reason the
    /// settings form is built from a schema shipped over the wire instead of from reflection.
    /// </remarks>
    /// <exception cref="PluginLoadException">
    /// The plugin has no settings, or the document does not fit them.
    /// </exception>
    public static async Task ApplyConfigurationAsync(
        LoadedPlugin plugin,
        string configurationJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(configurationJson);

        if (plugin.Configurable is not { } configurable)
        {
            throw new PluginLoadException(
                PluginSubStatus.ConfigurationInvalid,
                $"'{plugin.Id}' does not implement {nameof(IConfigurablePlugin)} and has no settings to apply.");
        }

        object? configuration;

        try
        {
            // Through the plugin's own JsonTypeInfo, not a JsonSerializerOptions of the runner's
            // choosing. The document being read is the one the host generated from a schema that was
            // itself extracted from this same metadata, so reading it any other way would let the two
            // halves of the round trip disagree about naming policies, converters and enum
            // representation - the failure being a saved setting that quietly does nothing.
            configuration = JsonSerializer.Deserialize(configurationJson, configurable.ConfigurationTypeInfo);
        }
        catch (JsonException ex)
        {
            throw new PluginLoadException(
                PluginSubStatus.ConfigurationInvalid,
                $"Settings for '{plugin.Id}' are not valid JSON for "
                + $"{configurable.ConfigurationTypeInfo.Type.Name}: {ex.Message}",
                ex);
        }

        if (configuration is null)
        {
            throw new PluginLoadException(
                PluginSubStatus.ConfigurationInvalid,
                $"Settings for '{plugin.Id}' deserialised to null.");
        }

        try
        {
            await plugin.Dispatcher.InvokeAsync(
                async token => await configurable.ApplyConfigurationAsync(configuration, token).ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The plugin is authoritative on its own validation, and rejecting settings by throwing
            // is the documented way to do it - so this is an expected outcome, not a fault.
            throw new PluginLoadException(
                PluginSubStatus.ConfigurationInvalid,
                $"'{plugin.Id}' rejected the settings: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Whether <paramref name="plugin"/> already holds the settings in
    /// <paramref name="configurationJson"/>.
    /// </summary>
    /// <remarks>
    /// Compared as documents rather than as text, by round-tripping the candidate through the
    /// plugin's own serialiser and comparing what comes out with what the plugin currently
    /// serialises to. Comparing the raw strings would answer "different" for a file that merely
    /// omitted a defaulted property or indented differently, which is most of them.
    /// <para>
    /// Any failure answers false, so the worst case is applying settings that were already in force.
    /// </para>
    /// </remarks>
    private static bool HasConfiguration(LoadedPlugin plugin, string configurationJson)
    {
        if (plugin.Configurable is not { } configurable)
            return false;

        try
        {
            object? candidate = JsonSerializer.Deserialize(configurationJson, configurable.ConfigurationTypeInfo);

            if (candidate is null)
                return false;

            return string.Equals(
                JsonSerializer.Serialize(candidate, configurable.ConfigurationTypeInfo),
                JsonSerializer.Serialize(configurable.Configuration, configurable.ConfigurationTypeInfo),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            // A document that does not parse is not "already applied" - it is a document
            // ApplyConfigurationAsync has to reject with a message naming the problem.
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> is safe to use as a file name and a pipe name component.
    /// </summary>
    public static bool IsSafeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        foreach (string segment in id.Split('.'))
        {
            if (segment.Length == 0)
                return false;
        }

        foreach (char character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
                return false;
        }

        return true;
    }

#if x64
    private const string RunnerArchitectureName = "64-bit";
#else
    private const string RunnerArchitectureName = "32-bit";
#endif
}
