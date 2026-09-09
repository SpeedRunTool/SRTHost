using System.Text.Json;
using SRTHost.Core;
using SRTHost.Core.Discovery;

namespace SRTHost.Core.Tests;

/// <summary>
/// What a manifest missing fields deserialises to.
/// </summary>
/// <remarks>
/// Written after finding that the <c>System.Text.Json</c> source generator does not run property
/// initialisers, which cost <see cref="HostSettings"/> its defaults. <see cref="PluginManifest"/>
/// declares the same kind of defaults - <c>string.Empty</c> on six properties and <c>"0.0.0.0"</c>
/// on the version - and is deserialised through a generated context, so it is exposed to exactly the
/// same thing.
/// <para>
/// The distinction that matters is between fields discovery <em>validates</em> and fields it only
/// <em>displays</em>. A null id or entry assembly is caught: every check goes through
/// <c>IsNullOrWhiteSpace</c> and produces a legible <see cref="DiscoveryProblem"/>. A null name,
/// description, author or version is not caught by anything, and travels into the shell as a
/// non-nullable string the compiler believes cannot be null.
/// </para>
/// </remarks>
public class PluginManifestDefaultsTests
{
    /// <summary>A manifest with only the fields discovery insists on.</summary>
    private const string Minimal = """
        {
          "schemaVersion": 1,
          "id": "SpeedRunTool.Demo.Producer",
          "contractGeneration": 5,
          "kind": "Producer",
          "architecture": "Any",
          "entryAssembly": "SpeedRunTool.Demo.Producer.dll",
          "entryType": "SpeedRunTool.Demo.Producer.DemoProducer"
        }
        """;

    /// <summary>
    /// Every string the manifest declares a default for has to survive being absent.
    /// </summary>
    /// <remarks>
    /// This is the regression test for the source generator dropping initialisers. If it fails with
    /// nulls, the fix is the one <see cref="HostSettings"/> took - stop using the generated context
    /// for this type - not a null check at each use site, because the use sites are XAML bindings
    /// and there are more of them every phase.
    /// </remarks>
    [Fact]
    public void MissingOptionalStringsKeepTheirDeclaredDefaults()
    {
        PluginManifest manifest = Deserialize(Minimal);

        Assert.Equal(string.Empty, manifest.Name);
        Assert.Equal(string.Empty, manifest.Description);
        Assert.Equal(string.Empty, manifest.Author);
        Assert.Equal("0.0.0.0", manifest.Version);
    }

    /// <summary>Present values win over the defaults, which is the ordinary case.</summary>
    [Fact]
    public void PresentValuesAreRead()
    {
        PluginManifest manifest = Deserialize("""
            {
              "schemaVersion": 1,
              "id": "SpeedRunTool.Demo.Producer",
              "name": "Demo Producer",
              "description": "A synthetic producer.",
              "author": "SpeedRunTool",
              "version": "1.2.3.4",
              "contractGeneration": 5,
              "kind": "Producer",
              "architecture": "X64",
              "requiresUiThread": true,
              "entryAssembly": "SpeedRunTool.Demo.Producer.dll",
              "entryType": "SpeedRunTool.Demo.Producer.DemoProducer"
            }
            """);

        Assert.Equal("Demo Producer", manifest.Name);
        Assert.Equal("A synthetic producer.", manifest.Description);
        Assert.Equal("SpeedRunTool", manifest.Author);
        Assert.Equal("1.2.3.4", manifest.Version);
        Assert.Equal(SRTPluginBase.Abstractions.PluginArchitecture.X64, manifest.Architecture);
        Assert.True(manifest.RequiresUiThread);
    }

    /// <summary>
    /// The fields discovery validates are safe when absent, whatever they deserialise to.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the defaults above because these two properties are load-bearing for
    /// safety rather than for display: the id becomes a settings file name and a pipe name, and the
    /// entry assembly becomes a path. Both are re-checked in <see cref="PluginDiscovery"/> through
    /// <c>IsNullOrWhiteSpace</c>, so a null is rejected rather than dereferenced - this pins that.
    /// </remarks>
    [Fact]
    public void AbsentIdAndEntryAssemblyAreRejectedRatherThanDereferenced()
    {
        PluginManifest manifest = Deserialize("""{ "schemaVersion": 1, "contractGeneration": 5 }""");

        Assert.False(PluginDiscovery.IsSafeId(manifest.Id));
        Assert.True(string.IsNullOrWhiteSpace(manifest.EntryAssembly));
        Assert.True(string.IsNullOrWhiteSpace(manifest.EntryType));
    }

    private static PluginManifest Deserialize(string json)
        => JsonSerializer.Deserialize<PluginManifest>(json, SrtJson.PluginManifest)!;
}
