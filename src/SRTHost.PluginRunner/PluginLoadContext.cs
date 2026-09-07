using System.Reflection;
using System.Runtime.Loader;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner;

/// <summary>
/// Loads one plugin and its private dependency closure, isolated from the runner's own assemblies.
/// </summary>
/// <remarks>
/// There is exactly one plugin in a runner process, so this does far less than the generation 1
/// loader it replaces. What is left is worth keeping for two reasons: the plugin's dependency
/// closure stays separate from the runner's, so a plugin shipping its own copy of a library the
/// runner also uses does not have to agree on a version; and a collectible context leaves the door
/// open to a configuration-only reload that does not recycle the process.
/// <para>
/// Resolution order, and why:
/// </para>
/// <list type="number">
///   <item>
///     A <see cref="SharedAssemblies">shared-surface assembly</see> comes from
///     <see cref="AssemblyLoadContext.Default"/>, so its types are reference-identical on both sides
///     of the boundary.
///   </item>
///   <item>
///     <see cref="AssemblyDependencyResolver"/>, which reads the plugin's own <c>deps.json</c> and is
///     the only component that actually knows what the plugin was built against, RID-specific assets
///     included.
///   </item>
///   <item>
///     A non-recursive probe of the plugin folder root, for a dependency dropped in beside the DLL
///     without a <c>deps.json</c> entry - which is how most hand-assembled plugin folders look.
///   </item>
///   <item>
///     <see langword="null"/>, letting the default context serve shared-framework assemblies.
///   </item>
/// </list>
/// <para>
/// Three pieces of the old loader are gone rather than ported. The issue #26 producer-diversion
/// rule - refuse a local copy of a producer assembly and delegate to whichever context already owns
/// that name - existed because every plugin shared one process and a duplicate producer assembly
/// meant duplicate producer types; with one plugin per process there is no cross-plugin type
/// identity left to protect and a stale copy of somebody else's DLL is simply inert. The recursive
/// <c>AllDirectories</c> search for the highest-versioned match is gone too: it ranked versions with
/// <c>Major*1000 + Minor*100 + Build*10 + Revision</c>, which mis-orders any component of 10 or more
/// (2.10.0 and 3.0.0 both score 3000), and it would happily load out of a nested
/// <c>runtimes\win-x86\</c> directory belonging to the other architecture.
/// </para>
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>
    /// Assemblies that must resolve to the runner's own copy rather than the plugin's.
    /// </summary>
    /// <remarks>
    /// These are the assemblies whose types physically cross the boundary between the runner and the
    /// plugin, and a second copy of any of them turns an ordinary call into an
    /// <see cref="InvalidCastException"/> with a message naming the same type twice.
    /// <list type="bullet">
    ///   <item>
    ///     <c>SRTPluginBase.Abstractions</c> - <see cref="IPlugin"/>, <see cref="IPluginContext"/> and
    ///     <c>PayloadFrame</c> are handed across in both directions.
    ///   </item>
    ///   <item>
    ///     <c>Microsoft.Extensions.Logging.Abstractions</c> - the runner resolves the plugin's
    ///     <c>ILoggerFactory</c> out of its own container, and the plugin casts what it gets back.
    ///     This is not hypothetical: a plugin folder assembled by copying a publish output contains
    ///     this assembly nearly every time.
    ///   </item>
    ///   <item>
    ///     <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> - the same, for the service
    ///     provider the plugin resolves through.
    ///   </item>
    /// </list>
    /// The set is deliberately this short. Every entry is a version the plugin no longer gets to
    /// choose, so anything added here is a constraint on plugin authors, not a convenience.
    /// </remarks>
    private static readonly string[] SharedAssemblies =
    [
        SrtContract.AbstractionsAssemblyName,
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
    ];

    private readonly AssemblyDependencyResolver resolver;
    private readonly string pluginRoot;

    /// <summary>Creates a context for the plugin assembly at <paramref name="pluginAssemblyPath"/>.</summary>
    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginAssemblyPath);

        resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
        pluginRoot = Path.GetDirectoryName(Path.GetFullPath(pluginAssemblyPath))
            ?? throw new ArgumentException($"'{pluginAssemblyPath}' has no directory.", nameof(pluginAssemblyPath));
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
            return null;

        if (SharedAssemblies.Contains(assemblyName.Name, StringComparer.OrdinalIgnoreCase))
        {
            // Not wrapped in a try: a shared-surface assembly the runner cannot itself load is a
            // broken runner, and falling through to the plugin's copy would replace a clear startup
            // failure with an InvalidCastException much later and much further away.
            return Default.LoadFromAssemblyName(assemblyName);
        }

        string? resolved = resolver.ResolveAssemblyToPath(assemblyName);

        if (resolved is not null)
            return LoadFromAssemblyPath(resolved);

        string probed = Path.Combine(pluginRoot, assemblyName.Name + ".dll");

        return File.Exists(probed) ? LoadFromAssemblyPath(probed) : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Producers exist to read another process's memory, so native dependencies are the normal case
    /// here rather than an edge one. The resolver handles the <c>runtimes\win-x64\native\</c> layout
    /// a published plugin has; the root probe covers a DLL dropped in beside the plugin by hand.
    /// </remarks>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? resolved = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

        if (resolved is not null)
            return LoadUnmanagedDllFromPath(resolved);

        string probed = Path.Combine(pluginRoot, unmanagedDllName);

        return File.Exists(probed) ? LoadUnmanagedDllFromPath(probed) : IntPtr.Zero;
    }
}
