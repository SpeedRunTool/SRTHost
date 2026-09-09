using System.Text.Json.Nodes;
using SRTHost.Core.Configuration;

namespace SRTHost.Core.Tests;

/// <summary>
/// Covers the immediate feedback the form gives while somebody types.
/// </summary>
/// <remarks>
/// Advisory only - the runner runs the plugin's own annotations and has the final word - so what
/// matters is that this never reports a problem where there is none. A false complaint about a value
/// the plugin would happily accept is worse than saying nothing.
/// </remarks>
public sealed class SettingValidatorTests
{
    private static SettingDescriptor Descriptor(Action<SettingDescriptorBuilder> configure)
    {
        SettingDescriptorBuilder builder = new();
        configure(builder);
        return builder.Build();
    }

    private sealed class SettingDescriptorBuilder
    {
        public SettingEditor Editor { get; set; } = SettingEditor.Text;

        public bool Required { get; set; }

        public bool Integral { get; set; }

        public double? Minimum { get; set; }

        public double? Maximum { get; set; }

        public int? MinimumLength { get; set; }

        public int? MaximumLength { get; set; }

        public string? Pattern { get; set; }

        public SettingDescriptor Build() => new()
        {
            Path = "Setting",
            Name = "Setting",
            Label = "Setting",
            Editor = Editor,
            Required = Required,
            Integral = Integral,
            Minimum = Minimum,
            Maximum = Maximum,
            MinimumLength = MinimumLength,
            MaximumLength = MaximumLength,
            Pattern = Pattern,
        };
    }

    [Fact]
    public void AcceptsAValueInsideItsRange()
    {
        SettingDescriptor setting = Descriptor(builder =>
        {
            builder.Editor = SettingEditor.Slider;
            builder.Minimum = 1;
            builder.Maximum = 600;
            builder.Integral = true;
        });

        Assert.Null(SettingValidator.Validate(setting, JsonValue.Create(30)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(601)]
    public void RejectsAValueOutsideItsRange(int value)
    {
        SettingDescriptor setting = Descriptor(builder =>
        {
            builder.Editor = SettingEditor.Slider;
            builder.Minimum = 1;
            builder.Maximum = 600;
            builder.Integral = true;
        });

        Assert.NotNull(SettingValidator.Validate(setting, JsonValue.Create(value)));
    }

    [Fact]
    public void RejectsAStringPastItsMaximumLength()
    {
        SettingDescriptor setting = Descriptor(builder => builder.MaximumLength = 4);

        Assert.Null(SettingValidator.Validate(setting, JsonValue.Create("abcd")));
        Assert.NotNull(SettingValidator.Validate(setting, JsonValue.Create("abcde")));
    }

    [Fact]
    public void RejectsAStringShortOfItsMinimumLength()
    {
        SettingDescriptor setting = Descriptor(builder => builder.MinimumLength = 3);

        Assert.NotNull(SettingValidator.Validate(setting, JsonValue.Create("ab")));
    }

    [Fact]
    public void RejectsAStringThatDoesNotMatchItsPattern()
    {
        SettingDescriptor setting = Descriptor(builder => builder.Pattern = "^[a-z]+$");

        Assert.Null(SettingValidator.Validate(setting, JsonValue.Create("demo")));
        Assert.NotNull(SettingValidator.Validate(setting, JsonValue.Create("Demo1")));
    }

    /// <summary>
    /// An unusable pattern is the plugin author's problem, and the runner reports it properly.
    /// Blaming the user's input for it would be actively misleading.
    /// </summary>
    [Fact]
    public void SaysNothingAboutAPatternThatDoesNotCompile()
    {
        SettingDescriptor setting = Descriptor(builder => builder.Pattern = "([");

        Assert.Null(SettingValidator.Validate(setting, JsonValue.Create("anything")));
    }

    [Fact]
    public void RejectsAMissingRequiredValue()
    {
        SettingDescriptor setting = Descriptor(builder => builder.Required = true);

        Assert.NotNull(SettingValidator.Validate(setting, value: null));
        Assert.NotNull(SettingValidator.Validate(setting, JsonValue.Create("   ")));
        Assert.Null(SettingValidator.Validate(setting, JsonValue.Create("set")));
    }

    [Fact]
    public void RejectsTextThatIsNotADuration()
    {
        SettingDescriptor setting = Descriptor(builder => builder.Editor = SettingEditor.Duration);

        Assert.Null(SettingValidator.Validate(setting, JsonValue.Create("00:01:30")));
        Assert.NotNull(SettingValidator.Validate(setting, JsonValue.Create("ninety seconds")));
    }

    [Fact]
    public void SaysNothingAboutAValueItHasNoRuleFor()
    {
        SettingDescriptor setting = Descriptor(builder => builder.Editor = SettingEditor.Json);

        Assert.Null(SettingValidator.Validate(setting, JsonNode.Parse("""{ "a": 1 }""")));
    }
}
