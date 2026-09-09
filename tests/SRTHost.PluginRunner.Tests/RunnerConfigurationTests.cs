using System.Text.Json.Nodes;
using SRTHost.Core.Configuration;
using SRTHost.Ipc;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner.Tests;

/// <summary>
/// Drives the settings half of the protocol against a real runner and the real demo consumer.
/// </summary>
/// <remarks>
/// The schema extractor cannot be unit tested usefully: what it has to get right is the plugin's own
/// <c>JsonTypeInfo</c> - the naming policy, the converters, the representation an enum ends up in -
/// and a hand-built <c>JsonTypeInfo</c> in a test would be a different serialiser answering a
/// different question. So this loads the actual plugin in the actual runner and reads what came
/// back, then feeds it through the same <see cref="SchemaReader"/> the settings page uses. Between
/// them, these two assert the whole round trip P6 is.
/// </remarks>
public class RunnerConfigurationTests
{
    private const string ConsumerId = "SpeedRunTool.Demo.Consumer";
    private const string ConsumerType = "SpeedRunTool.Demo.Consumer.DemoConsumer";
    private const string ProducerId = "SpeedRunTool.Demo.Producer";
    private const string ProducerType = "SpeedRunTool.Demo.Producer.DemoProducer";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<ReadyMessage> LoadConsumerAsync(RunnerHarness harness, string? configurationJson = null)
        => await harness.LoadAsync(ConsumerId, ConsumerType, Token, configurationJson);

    private static JsonObject Properties(ReadyMessage ready)
        => (JsonNode.Parse(ready.ConfigurationSchemaJson!)!["properties"] as JsonObject)!;

    private static JsonObject Hints(ReadyMessage ready)
        => (JsonNode.Parse(ready.ConfigurationHintsJson!) as JsonObject)!;

    /// <summary>
    /// The schema rides back on the load reply rather than being fetched, so the host can draw a form
    /// the moment a plugin is up.
    /// </summary>
    [Fact]
    public async Task LoadAnswersWithTheSettingsSchemaAndCurrentValues()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await LoadConsumerAsync(harness);

        Assert.NotNull(ready.ConfigurationSchemaJson);
        Assert.NotNull(ready.ConfigurationHintsJson);
        Assert.NotNull(ready.ConfigurationJson);

        Assert.Equal(
            "https://json-schema.org/draft/2020-12/schema",
            JsonNode.Parse(ready.ConfigurationSchemaJson)!["$schema"]!.GetValue<string>());
    }

    /// <summary>A plugin with no settings says so by omission, rather than with an empty schema.</summary>
    [Fact]
    public async Task LoadAnswersWithNoSchemaForAPluginThatHasNoSettings()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await harness.LoadAsync(ProducerId, ProducerType, Token);

        Assert.Null(ready.ConfigurationSchemaJson);
        Assert.Null(ready.ConfigurationJson);
    }

    /// <summary>
    /// The editor a hint names is what the whole form hangs off, and the demo's settings type exists
    /// precisely to cover every one of them.
    /// </summary>
    [Fact]
    public async Task ChoosesAnEditorForEverySupportedShape()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        JsonObject hints = Hints(await LoadConsumerAsync(harness));

        Assert.Equal("toggle", hints["Enabled"]!["editor"]!.GetValue<string>());
        Assert.Equal("enum", hints["Corner"]!["editor"]!.GetValue<string>());
        Assert.Equal("flags", hints["Fields"]!["editor"]!.GetValue<string>());
        Assert.Equal("slider", hints["Opacity"]!["editor"]!.GetValue<string>());
        Assert.Equal("slider", hints["LogEvery"]!["editor"]!.GetValue<string>());
        Assert.Equal("text", hints["Prefix"]!["editor"]!.GetValue<string>());
        Assert.Equal("object", hints["Appearance"]!["editor"]!.GetValue<string>());
        Assert.Equal("color", hints["Appearance/Foreground"]!["editor"]!.GetValue<string>());
        Assert.Equal("font", hints["Appearance/Font"]!["editor"]!.GetValue<string>());
        Assert.Equal("slider", hints["Appearance/FontSize"]!["editor"]!.GetValue<string>());
        Assert.Equal("path", hints["OutputFolder"]!["editor"]!.GetValue<string>());
        Assert.Equal("hotkey", hints["ToggleHotkey"]!["editor"]!.GetValue<string>());
        Assert.Equal("duration", hints["Linger"]!["editor"]!.GetValue<string>());
        Assert.Equal("list", hints["IgnoredLabels"]!["editor"]!.GetValue<string>());
    }

    /// <summary>
    /// The per-property fallback: a dictionary is perfectly serialisable and completely undrawable,
    /// and it must degrade to a JSON row without taking the rest of the form with it.
    /// </summary>
    [Fact]
    public async Task FallsBackToJsonForAPropertyItCannotDraw()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await LoadConsumerAsync(harness);

        Assert.Equal("json", Hints(ready)["Ignored"]!["editor"]!.GetValue<string>());

        // And the rest of the form is unaffected, which is the half that matters.
        IReadOnlyList<SettingGroup> groups = SchemaReader.Read(
            ready.ConfigurationSchemaJson, ready.ConfigurationHintsJson);

        Assert.Contains(groups, group => group.Name == "General");
        Assert.Contains(groups.SelectMany(group => group.Settings), setting => setting.Editor == SettingEditor.Toggle);
    }

    [Fact]
    public async Task LeavesOutAHiddenProperty()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await LoadConsumerAsync(harness);

        Assert.False(Properties(ready).ContainsKey("InternalRevision"));
        Assert.False(Hints(ready).ContainsKey("InternalRevision"));
    }

    /// <summary>
    /// The question only the plugin's own serialiser can answer, and the reason the extractor walks
    /// <c>JsonTypeInfo</c> instead of the CLR type: the demo writes one enum by name and the other as
    /// a number, and the form has to write back whichever the plugin will read.
    /// </summary>
    [Fact]
    public async Task TakesEachEnumsRepresentationFromThePluginsOwnSerialiser()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        JsonObject properties = Properties(await LoadConsumerAsync(harness));

        Assert.Equal("string", properties["Corner"]!["type"]!.GetValue<string>());
        Assert.Equal("TopLeft", properties["Corner"]!["default"]!.GetValue<string>());
        Assert.Equal("TopLeft", properties["Corner"]!["enum"]![0]!.GetValue<string>());

        Assert.Equal("integer", properties["Fields"]!["type"]!.GetValue<string>());
        Assert.Equal(15, properties["Fields"]!["default"]!.GetValue<int>());
    }

    [Fact]
    public async Task CarriesRangeAndLengthConstraintsIntoTheSchema()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        JsonObject properties = Properties(await LoadConsumerAsync(harness));

        Assert.Equal(1, properties["LogEvery"]!["minimum"]!.GetValue<int>());
        Assert.Equal(600, properties["LogEvery"]!["maximum"]!.GetValue<int>());
        Assert.Equal(32, properties["Prefix"]!["maxLength"]!.GetValue<int>());
        Assert.Equal("integer", properties["LogEvery"]!["type"]!.GetValue<string>());
        Assert.Equal("number", properties["Opacity"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExpandsANestedSettingsObject()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        JsonObject properties = Properties(await LoadConsumerAsync(harness));
        JsonObject appearance = (properties["Appearance"]!["properties"] as JsonObject)!;

        Assert.Equal("object", properties["Appearance"]!["type"]!.GetValue<string>());
        Assert.True(appearance.ContainsKey("Foreground"));
        Assert.Equal(14, appearance["FontSize"]!["default"]!.GetValue<int>());
    }

    [Fact]
    public async Task CarriesGroupsOrderingAndDependencies()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        JsonObject hints = Hints(await LoadConsumerAsync(harness));

        Assert.Equal("General", hints["Enabled"]!["group"]!.GetValue<string>());
        Assert.Equal("Advanced", hints["ToggleHotkey"]!["group"]!.GetValue<string>());
        Assert.True(hints["ToggleHotkey"]!["advanced"]!.GetValue<bool>());
        Assert.Equal(1, hints["Enabled"]!["order"]!.GetValue<int>());

        JsonNode dependency = hints["Corner"]!["dependsOn"]![0]!;

        Assert.Equal("Enabled", dependency["property"]!.GetValue<string>());
        Assert.True(dependency["value"]!.GetValue<bool>());
    }

    /// <summary>The <c>[SrtPath(Directory = true)]</c> hint the Browse button needs.</summary>
    [Fact]
    public async Task SaysWhenAPathSettingPicksAFolder()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        JsonObject hints = Hints(await LoadConsumerAsync(harness));

        Assert.True(hints["OutputFolder"]!["pickDirectory"]!.GetValue<bool>());
    }

    /// <summary>
    /// Settings sent with <c>LoadPlugin</c> are in force before the plugin starts, and are reported
    /// back as the current values - which is not the same document, because the plugin fills in its
    /// own defaults for everything the file omitted.
    /// </summary>
    [Fact]
    public async Task AppliesSettingsSuppliedWithLoad()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await LoadConsumerAsync(harness, """{"LogEvery":7,"Prefix":"loaded"}""");

        JsonObject current = (JsonNode.Parse(ready.ConfigurationJson!) as JsonObject)!;

        Assert.Equal(7, current["LogEvery"]!.GetValue<int>());
        Assert.Equal("loaded", current["Prefix"]!.GetValue<string>());

        // Untouched by the file, so it is the plugin's own default rather than a missing value.
        Assert.True(current["Enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AppliesSettingsAtRuntimeAndReportsThemBack()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await LoadConsumerAsync(harness);

        await harness.RequestAsync<AckMessage>(
            new ApplyConfigurationMessage { ConfigurationJson = """{"Prefix":"applied","LogEvery":3}""" },
            Token);

        ConfigurationSchemaMessage described = await harness.RequestAsync<ConfigurationSchemaMessage>(
            new GetConfigurationSchemaMessage(),
            Token);

        JsonObject current = (JsonNode.Parse(described.CurrentJson) as JsonObject)!;

        Assert.Equal("applied", current["Prefix"]!.GetValue<string>());
        Assert.Equal(3, current["LogEvery"]!.GetValue<int>());
        Assert.NotEqual("{}", described.SchemaJson);
        Assert.NotEqual("{}", described.HintsJson);
    }

    /// <summary>
    /// Validation is authoritative in the runner. The host's own checks are a convenience, and a
    /// document that would break the plugin has to be refused here whether or not it ever passed
    /// through a form.
    /// </summary>
    [Fact]
    public async Task RefusesSettingsThePluginsAnnotationsReject()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await LoadConsumerAsync(harness);

        IpcRequestException failure = await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.RequestAsync<AckMessage>(
                new ApplyConfigurationMessage { ConfigurationJson = """{"LogEvery":9000}""" },
                Token));

        Assert.Equal(PluginSubStatus.ConfigurationInvalid, failure.SubStatus);
    }

    [Fact]
    public async Task RefusesSettingsThatAreNotValidJsonForTheSettingsType()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await LoadConsumerAsync(harness);

        IpcRequestException failure = await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.RequestAsync<AckMessage>(
                new ApplyConfigurationMessage { ConfigurationJson = """{"LogEvery":"not a number"}""" },
                Token));

        Assert.Equal(PluginSubStatus.ConfigurationInvalid, failure.SubStatus);
    }

    /// <summary>
    /// The end of the round trip: what the runner describes, the host reads into a form, and the
    /// paths in that form are the keys the plugin's own serialiser reads.
    /// </summary>
    [Fact]
    public async Task DescribesSomethingTheHostCanBuildAFormFrom()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await LoadConsumerAsync(harness);

        IReadOnlyList<SettingGroup> groups = SchemaReader.Read(
            ready.ConfigurationSchemaJson, ready.ConfigurationHintsJson);

        Assert.Equal(["General", "Appearance", "Advanced"], groups.Select(group => group.Name));

        SettingDescriptor enabled = groups[0].Settings[0];

        Assert.Equal("Enabled", enabled.Path);
        Assert.Equal("Log payloads", enabled.Label);
        Assert.Equal(SettingEditor.Toggle, enabled.Editor);

        JsonObject current = (JsonNode.Parse(ready.ConfigurationJson!) as JsonObject)!;

        foreach (SettingDescriptor setting in groups.SelectMany(group => group.Settings))
            Assert.True(current.ContainsKey(setting.Name), $"'{setting.Name}' is not a key the plugin writes.");
    }

    /// <summary>
    /// Round-trips a form edit the way the settings page does: read the current document, change one
    /// value at its path, send it back, and confirm the plugin adopted it.
    /// </summary>
    [Fact]
    public async Task RoundTripsAnEditMadeThroughTheFormsPaths()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        ReadyMessage ready = await LoadConsumerAsync(harness);

        JsonObject document = (JsonNode.Parse(ready.ConfigurationJson!) as JsonObject)!;

        SettingPath.Set(document, "Appearance/FontSize", JsonValue.Create(31));
        SettingPath.Set(document, "Corner", JsonValue.Create("BottomRight"));

        await harness.RequestAsync<AckMessage>(
            new ApplyConfigurationMessage { ConfigurationJson = document.ToJsonString() },
            Token);

        ConfigurationSchemaMessage described = await harness.RequestAsync<ConfigurationSchemaMessage>(
            new GetConfigurationSchemaMessage(),
            Token);

        JsonObject current = (JsonNode.Parse(described.CurrentJson) as JsonObject)!;

        Assert.Equal(31, SettingPath.Get(current, "Appearance/FontSize")!.GetValue<int>());
        Assert.Equal("BottomRight", SettingPath.Get(current, "Corner")!.GetValue<string>());
    }

    /// <summary>An unconfigurable plugin answers the schema request with an error, not a crash.</summary>
    [Fact]
    public async Task AnswersTheSchemaRequestForAnUnconfigurablePluginWithAnError()
    {
        await using RunnerHarness harness = await RunnerHarness.StartAsync(Token);

        await harness.LoadAsync(ProducerId, ProducerType, Token);

        await Assert.ThrowsAsync<IpcRequestException>(
            async () => await harness.RequestAsync<ConfigurationSchemaMessage>(
                new GetConfigurationSchemaMessage(),
                Token));
    }
}
