using System.Reflection;
using System.Text.Json;
using SRTPluginBase;
using SRTPluginBase.Abstractions;

namespace SRTHost.Core.Tests;

/// <summary>
/// Guards the invariant the whole discovery design rests on: what the build writes into
/// <c>srtplugin.json</c> and what the plugin reports at run time are two readings of the same
/// metadata, and must never disagree.
/// </summary>
/// <remarks>
/// The host enumerates plugins from their manifests precisely so it does not have to load them - it
/// is AnyCPU and cannot load an x86 plugin at all. So a manifest that quietly drifts from the plugin
/// it describes is not a cosmetic bug: the host would show, filter and schedule a plugin on facts
/// that stopped being true, and nothing would report it. These tests are the only thing standing
/// between the two readings.
/// </remarks>
public class GeneratedManifestTests
{
    [Fact]
    public void ProducerManifestMatchesWhatThePluginReportsAtRuntime()
        => AssertManifestMatches("ProducerManifest", new SpeedRunTool.Demo.Producer.DemoProducer().Info);

    [Fact]
    public void ConsumerManifestMatchesWhatThePluginReportsAtRuntime()
        => AssertManifestMatches("ConsumerManifest", new SpeedRunTool.Demo.Consumer.DemoConsumer().Info);

    [Fact]
    public void ManifestDeclaresThisHostsContractGeneration()
    {
        JsonElement manifest = ReadManifest("ProducerManifest");

        // Read from the assembly reference rather than a defaulted attribute property, which would
        // report the generation of whichever contracts assembly happened to be loaded - see the
        // remarks on SrtContract.GenerationOf.
        Assert.Equal(SrtContract.Generation, manifest.GetProperty("contractGeneration").GetInt32());
    }

    [Fact]
    public void ManifestNamesTheEntryTypeTheRunnerHasToInstantiate()
    {
        JsonElement manifest = ReadManifest("ConsumerManifest");

        Assert.Equal("SpeedRunTool.Demo.Consumer.dll", manifest.GetProperty("entryAssembly").GetString());
        Assert.Equal(typeof(SpeedRunTool.Demo.Consumer.DemoConsumer).FullName, manifest.GetProperty("entryType").GetString());
    }

    private static void AssertManifestMatches(string key, IPluginInfo info)
    {
        JsonElement manifest = ReadManifest(key);

        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(info.Id, manifest.GetProperty("id").GetString());
        Assert.Equal(info.Name, manifest.GetProperty("name").GetString());
        Assert.Equal(info.Description, manifest.GetProperty("description").GetString());
        Assert.Equal(info.Author, manifest.GetProperty("author").GetString());
        Assert.Equal(info.Version.ToString(), manifest.GetProperty("version").GetString());
        Assert.Equal(info.ContractGeneration, manifest.GetProperty("contractGeneration").GetInt32());
        Assert.Equal(info.Kind.ToString(), manifest.GetProperty("kind").GetString());
        Assert.Equal(info.Architecture.ToString(), manifest.GetProperty("architecture").GetString());
        Assert.Equal(info.RequiresUiThread, manifest.GetProperty("requiresUiThread").GetBoolean());
    }

    private static JsonElement ReadManifest(string key)
    {
        string path = typeof(GeneratedManifestTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key)
            .Value!;

        Assert.True(
            File.Exists(path),
            $"No manifest at '{path}'. The build stopped emitting srtplugin.json, which would leave "
            + "the host unable to describe a plugin without loading it.");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}
