using SRTHost.Core.Configuration;

namespace SRTHost.Core.Tests;

/// <summary>
/// Covers turning a runner's schema and hints into the descriptors a form is built from.
/// </summary>
/// <remarks>
/// Both documents arrive from a plugin's runner, so the interesting cases are the malformed ones:
/// the reader has to answer with a form rather than an exception however badly a plugin describes
/// itself. Those are as important here as the happy path.
/// </remarks>
public sealed class SchemaReaderTests
{
    private const string Schema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["Prefix"],
          "properties": {
            "Enabled":  { "type": "boolean", "title": "Log payloads", "default": true },
            "Corner":   { "type": "string", "enum": ["TopLeft", "TopRight"], "title": "Corner" },
            "Opacity":  { "type": "number", "minimum": 0, "maximum": 1, "title": "Opacity" },
            "LogEvery": { "type": "integer", "minimum": 1, "maximum": 600, "title": "Log every" },
            "Prefix":   { "type": "string", "maxLength": 32, "title": "Prefix" },
            "Appearance": {
              "type": "object",
              "title": "Appearance",
              "properties": {
                "Font":     { "type": "string", "title": "Font" },
                "FontSize": { "type": "integer", "minimum": 6, "maximum": 72, "title": "Font size" }
              }
            },
            "Ignored": { "title": "Label replacements" }
          }
        }
        """;

    private const string Hints = """
        {
          "Enabled":  { "editor": "toggle", "label": "Log payloads", "group": "General", "order": 1 },
          "Corner":   { "editor": "enum", "label": "Corner", "group": "General", "order": 2,
                        "options": [ { "value": "TopLeft", "label": "Top left" },
                                     { "value": "TopRight", "label": "Top right" } ],
                        "dependsOn": [ { "property": "Enabled", "value": true, "hide": false } ] },
          "Opacity":  { "editor": "slider", "label": "Opacity", "group": "General", "order": 4, "step": 0.01 },
          "LogEvery": { "editor": "slider", "label": "Log every", "group": "General", "order": 3 },
          "Prefix":   { "editor": "text", "label": "Prefix", "group": "General", "order": 5 },
          "Appearance": { "editor": "object", "label": "Appearance", "group": "Appearance", "order": 1 },
          "Appearance/Font":     { "editor": "font", "label": "Font" },
          "Appearance/FontSize": { "editor": "slider", "label": "Font size" },
          "Ignored": { "editor": "json", "label": "Label replacements", "group": "Advanced", "advanced": true }
        }
        """;

    private static IReadOnlyList<SettingGroup> Read() => SchemaReader.Read(Schema, Hints);

    private static SettingDescriptor Find(string path)
        => Read().SelectMany(group => Flatten(group.Settings)).Single(setting => setting.Path == path);

    private static IEnumerable<SettingDescriptor> Flatten(IEnumerable<SettingDescriptor> settings)
        => settings.SelectMany(setting => Flatten(setting.Children).Prepend(setting));

    [Fact]
    public void GroupsSettingsInTheOrderTheirFirstMemberAppears()
    {
        Assert.Equal(["General", "Appearance", "Advanced"], Read().Select(group => group.Name));
    }

    [Fact]
    public void OrdersWithinAGroupByTheDeclaredOrder()
    {
        SettingGroup general = Read().First(group => group.Name == "General");

        Assert.Equal(
            ["Enabled", "Corner", "LogEvery", "Opacity", "Prefix"],
            general.Settings.Select(setting => setting.Name));
    }

    [Fact]
    public void ReadsTheEditorFromTheHints()
    {
        Assert.Equal(SettingEditor.Toggle, Find("Enabled").Editor);
        Assert.Equal(SettingEditor.Enum, Find("Corner").Editor);
        Assert.Equal(SettingEditor.Slider, Find("Opacity").Editor);
        Assert.Equal(SettingEditor.Object, Find("Appearance").Editor);
        Assert.Equal(SettingEditor.Font, Find("Appearance/Font").Editor);
    }

    [Fact]
    public void ReadsConstraintsFromTheSchema()
    {
        SettingDescriptor opacity = Find("Opacity");

        Assert.Equal(0, opacity.Minimum);
        Assert.Equal(1, opacity.Maximum);
        Assert.False(opacity.Integral);

        SettingDescriptor logEvery = Find("LogEvery");

        Assert.True(logEvery.Integral);
        Assert.Equal(600, logEvery.Maximum);

        Assert.Equal(32, Find("Prefix").MaximumLength);
    }

    [Fact]
    public void ReadsRequiredFromTheSchema()
    {
        Assert.True(Find("Prefix").Required);
        Assert.False(Find("Enabled").Required);
    }

    [Fact]
    public void ReadsOptionsWithTheirLabels()
    {
        SettingDescriptor corner = Find("Corner");

        Assert.Equal(["Top left", "Top right"], corner.Options.Select(option => option.Label));
        Assert.Equal("TopLeft", corner.Options[0].Value!.GetValue<string>());
    }

    [Fact]
    public void ReadsDefaults()
    {
        Assert.True(Find("Enabled").Default!.GetValue<bool>());
    }

    /// <summary>
    /// A dependency names a sibling, so a nested setting's dependency has to resolve against its own
    /// parent rather than against the document root.
    /// </summary>
    [Fact]
    public void RewritesADependencyIntoAFullPath()
    {
        SettingDependency dependency = Assert.Single(Find("Corner").DependsOn);

        Assert.Equal("Enabled", dependency.PropertyPath);
        Assert.True(dependency.Value!.GetValue<bool>());
        Assert.False(dependency.HideWhenUnmet);
    }

    [Fact]
    public void ExpandsANestedObjectIntoChildren()
    {
        SettingDescriptor appearance = Find("Appearance");

        Assert.Equal(["Appearance/Font", "Appearance/FontSize"], appearance.Children.Select(child => child.Path));
        Assert.Equal("FontSize", appearance.Children[1].Name);
    }

    [Fact]
    public void CarriesTheAdvancedFlag()
    {
        Assert.True(Find("Ignored").Advanced);
        Assert.False(Find("Enabled").Advanced);
    }

    /// <summary>
    /// The fallback that keeps the declarative layer from ever fully blocking an author: a property
    /// the runner could not classify still gets a row, and it is an editable one.
    /// </summary>
    [Fact]
    public void FallsBackToTheJsonEditorForAnUnknownEditor()
    {
        Assert.Equal(SettingEditor.Json, Find("Ignored").Editor);

        IReadOnlyList<SettingGroup> groups = SchemaReader.Read(
            """{ "type": "object", "properties": { "Thing": { } } }""",
            """{ "Thing": { "editor": "something-a-newer-runner-invented" } }""");

        Assert.Equal(SettingEditor.Json, Assert.Single(Assert.Single(groups).Settings).Editor);
    }

    [Fact]
    public void FallsBackToThePropertyNameWhenNothingSuppliesALabel()
    {
        IReadOnlyList<SettingGroup> groups = SchemaReader.Read(
            """{ "type": "object", "properties": { "Thing": { "type": "string" } } }""",
            "{}");

        Assert.Equal("Thing", Assert.Single(Assert.Single(groups).Settings).Label);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not json at all", "{}")]
    [InlineData("[1,2,3]", "{}")]
    [InlineData("""{ "type": "object" }""", "{}")]
    public void AnswersAnEmptyFormRatherThanThrowing(string? schema, string? hints)
    {
        Assert.Empty(SchemaReader.Read(schema, hints));
    }

    /// <summary>
    /// Hints of the wrong shape are a plugin describing itself badly, which must cost it its
    /// presentation and nothing else.
    /// </summary>
    [Fact]
    public void IgnoresHintsOfTheWrongType()
    {
        IReadOnlyList<SettingGroup> groups = SchemaReader.Read(
            """{ "type": "object", "properties": { "Thing": { "type": "string", "title": "Thing" } } }""",
            """{ "Thing": { "editor": 7, "order": "first", "advanced": "yes", "options": 3 } }""");

        SettingDescriptor setting = Assert.Single(Assert.Single(groups).Settings);

        Assert.Equal(SettingEditor.Json, setting.Editor);
        Assert.Equal(0, setting.Order);
        Assert.False(setting.Advanced);
        Assert.Empty(setting.Options);
    }
}
