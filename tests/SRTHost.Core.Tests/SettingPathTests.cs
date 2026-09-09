using System.Text.Json.Nodes;
using SRTHost.Core.Configuration;

namespace SRTHost.Core.Tests;

/// <summary>
/// Covers addressing a value inside a settings document, which every control in the generated form
/// depends on.
/// </summary>
public sealed class SettingPathTests
{
    [Fact]
    public void ReadsATopLevelValue()
    {
        JsonObject document = new() { ["Enabled"] = true };

        Assert.True(SettingPath.Get(document, "Enabled")!.GetValue<bool>());
    }

    [Fact]
    public void ReadsANestedValue()
    {
        JsonObject document = new()
        {
            ["Appearance"] = new JsonObject { ["FontSize"] = 14 },
        };

        Assert.Equal(14, SettingPath.Get(document, "Appearance/FontSize")!.GetValue<int>());
    }

    [Fact]
    public void AnswersNullForAMissingPath()
    {
        JsonObject document = new() { ["Enabled"] = true };

        Assert.Null(SettingPath.Get(document, "Appearance/FontSize"));
    }

    [Fact]
    public void AnswersNullWhenAnIntermediateIsNotAnObject()
    {
        JsonObject document = new() { ["Appearance"] = 3 };

        Assert.Null(SettingPath.Get(document, "Appearance/FontSize"));
    }

    [Fact]
    public void WritesATopLevelValue()
    {
        JsonObject document = [];

        SettingPath.Set(document, "Enabled", JsonValue.Create(true));

        Assert.True(document["Enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// The case that matters: a settings file written before a nested group existed has no object to
    /// put the new value in, and a form that could not create one would silently drop every edit
    /// under that group.
    /// </summary>
    [Fact]
    public void CreatesIntermediateObjectsOnTheWay()
    {
        JsonObject document = [];

        SettingPath.Set(document, "Appearance/FontSize", JsonValue.Create(18));

        Assert.Equal(18, SettingPath.Get(document, "Appearance/FontSize")!.GetValue<int>());
    }

    [Fact]
    public void ReplacesAnIntermediateThatIsNotAnObject()
    {
        JsonObject document = new() { ["Appearance"] = "nonsense" };

        SettingPath.Set(document, "Appearance/FontSize", JsonValue.Create(18));

        Assert.Equal(18, SettingPath.Get(document, "Appearance/FontSize")!.GetValue<int>());
    }

    /// <summary>
    /// A <see cref="JsonNode"/> belongs to exactly one parent, so assigning one that still has a
    /// parent throws - which is easy to hit here, since values come from schema defaults and from
    /// other documents.
    /// </summary>
    [Fact]
    public void AcceptsANodeThatAlreadyHasAParent()
    {
        JsonObject source = new() { ["Value"] = 5 };
        JsonObject document = [];

        SettingPath.Set(document, "Copied", source["Value"]);

        Assert.Equal(5, document["Copied"]!.GetValue<int>());
        Assert.Equal(5, source["Value"]!.GetValue<int>());
    }
}
