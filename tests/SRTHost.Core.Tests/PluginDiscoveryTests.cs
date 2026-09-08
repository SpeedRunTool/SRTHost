using SRTHost.Core.Discovery;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Tests;

/// <summary>
/// What the host will and will not accept out of a plugins directory.
/// </summary>
/// <remarks>
/// Discovery is the one place the host reads a document it did not write. A manifest can arrive
/// beside a downloaded plugin having never passed through the build-time generator, and its id
/// becomes a settings file name, a state directory name and part of a pipe name - so every rule
/// checked at build time is checked again here, and these tests are what say so.
/// <para>
/// Every rejection is also expected to be <em>reported</em> rather than merely skipped. "My plugin
/// does not appear, and nothing says why" is the least diagnosable failure this application has, so
/// a silent skip would be a worse bug than a wrong one.
/// </para>
/// </remarks>
public class PluginDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "srt-discovery-" + Guid.NewGuid().ToString("N")[..8]);

    public PluginDiscoveryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives one test run is not worth failing over.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FindsAWellFormedPlugin()
    {
        WritePlugin("SpeedRunTool.Demo.Producer");

        DiscoveryResult result = PluginDiscovery.Scan(root);

        DiscoveredPlugin plugin = Assert.Single(result.Plugins);

        Assert.Empty(result.Problems);
        Assert.Equal("SpeedRunTool.Demo.Producer", plugin.Id);
        Assert.Equal(PluginKind.Producer, plugin.Manifest.Kind);
        Assert.True(File.Exists(plugin.EntryAssemblyPath));
    }

    /// <summary>
    /// The directory name is not the identity. Generation 1 required the DLL to be named after its
    /// folder; the manifest names the entry assembly instead, so a plugin unzipped into a folder
    /// called anything at all still works.
    /// </summary>
    [Fact]
    public void DoesNotRequireTheFolderToBeNamedAfterThePlugin()
    {
        WritePlugin("SpeedRunTool.Demo.Producer", folderName: "downloaded (1)");

        Assert.Single(PluginDiscovery.Scan(root).Plugins);
    }

    [Fact]
    public void ReportsAFolderWithNoManifest()
    {
        Directory.CreateDirectory(Path.Combine(root, "NotAPlugin"));

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Contains("srtplugin.json", Assert.Single(result.Problems).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAMalformedManifest()
    {
        string directory = Path.Combine(root, "Broken");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "srtplugin.json"), "{ not json");

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void RefusesAPluginBuiltForAnotherContractGeneration()
    {
        WritePlugin("SpeedRunTool.Demo.Old", generation: SrtContract.Generation - 1);

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Contains("generation", Assert.Single(result.Problems).Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An id becomes a file name and a pipe name component, so a manifest that never passed through
    /// the build-time check has to be caught here.
    /// </summary>
    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("..")]
    [InlineData("trailing.")]
    [InlineData("double..dot")]
    public void RefusesAnUnsafeId(string id)
    {
        // The entry assembly is named explicitly: the helper would otherwise derive it from the id,
        // and several of these ids are not legal file names - which is the whole point of them.
        WritePlugin(id, folderName: "Unsafe", entryAssembly: "plugin.dll");

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Single(result.Problems);
    }

    /// <summary>
    /// Two folders claiming one id would share a settings file, a state directory and a pipe name.
    /// Which one the user meant is not knowable from here, so neither is loaded.
    /// </summary>
    [Fact]
    public void RefusesADuplicateId()
    {
        WritePlugin("SpeedRunTool.Demo.Producer", folderName: "first");
        WritePlugin("SpeedRunTool.Demo.Producer", folderName: "second");

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Single(result.Plugins);
        Assert.Contains("already claimed", Assert.Single(result.Problems).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest is not a trusted document: an entry assembly of <c>..\..\something.dll</c> would
    /// otherwise load an assembly from outside the plugin's own directory.
    /// </summary>
    [Fact]
    public void RefusesAnEntryAssemblyThatIsAPath()
    {
        WritePlugin("SpeedRunTool.Demo.Escape", entryAssembly: @"..\..\evil.dll", createAssembly: false);

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Contains("file name", Assert.Single(result.Problems).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsAManifestWhoseAssemblyIsMissing()
    {
        WritePlugin("SpeedRunTool.Demo.Gone", createAssembly: false);

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Contains("not in this folder", Assert.Single(result.Problems).Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reserved in the schema and honoured nowhere: no runner declares the Windows Desktop
    /// framework, so saying so beats loading it and failing on assembly resolution.
    /// </summary>
    [Fact]
    public void RefusesAPluginNeedingTheWindowsDesktopFramework()
    {
        WritePlugin("SpeedRunTool.Demo.WinForms", requiresWindowsDesktop: true);

        DiscoveryResult result = PluginDiscovery.Scan(root);

        Assert.Empty(result.Plugins);
        Assert.Contains("Windows Desktop", Assert.Single(result.Problems).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnsNothingForADirectoryThatDoesNotExist()
    {
        DiscoveryResult result = PluginDiscovery.Scan(Path.Combine(root, "absent"));

        Assert.Empty(result.Plugins);
        Assert.Empty(result.Problems);
    }

    private void WritePlugin(
        string id,
        string? folderName = null,
        int? generation = null,
        string? entryAssembly = null,
        bool createAssembly = true,
        bool requiresWindowsDesktop = false)
    {
        string directory = Path.Combine(root, folderName ?? id);
        Directory.CreateDirectory(directory);

        entryAssembly ??= id + ".dll";

        if (createAssembly)
            File.WriteAllBytes(Path.Combine(directory, entryAssembly), [0x4D, 0x5A]);

        File.WriteAllText(Path.Combine(directory, "srtplugin.json"), $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "name": "Test Plugin",
              "description": "",
              "author": "SpeedRunTool",
              "version": "1.0.0.0",
              "contractGeneration": {{generation ?? SrtContract.Generation}},
              "kind": "Producer",
              "architecture": "Any",
              "requiresUiThread": false,
              "requiresWindowsDesktop": {{(requiresWindowsDesktop ? "true" : "false")}},
              "entryAssembly": "{{entryAssembly.Replace("\\", "\\\\")}}",
              "entryType": "Test.Plugin"
            }
            """);
    }
}
