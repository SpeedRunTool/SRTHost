using System.Text.Json;
using System.Text.Json.Nodes;

namespace SRTHost.Core.Configuration;

/// <summary>
/// Turns the schema and hints a runner sent into the descriptors a settings form is built from.
/// </summary>
/// <remarks>
/// Everything here is read out of <see cref="JsonNode"/> by hand rather than deserialised into a
/// model. That is not squeamishness about the source generator - it is that these two documents come
/// from a plugin's runner, so the host has to treat every field as absent, of the wrong type, or
/// nonsense, and answer with a form rather than an exception. A missing hint means a default; a
/// hint of the wrong shape means a default; an editor name the host does not know means the JSON
/// editor. A plugin cannot break the settings page by describing itself badly.
/// </remarks>
public static class SchemaReader
{
    /// <summary>
    /// Reads a settings description into ordered groups.
    /// </summary>
    /// <param name="schemaJson">JSON Schema 2020-12 for the settings type.</param>
    /// <param name="hintsJson">The hints sidecar, keyed by setting path.</param>
    /// <returns>
    /// The groups, in order, with the unnamed group first. Empty when the schema describes no
    /// properties - which is exactly the case that drives the whole-document JSON editor.
    /// </returns>
    public static IReadOnlyList<SettingGroup> Read(string? schemaJson, string? hintsJson)
    {
        JsonObject? schema = Parse(schemaJson);
        JsonObject hints = Parse(hintsJson) ?? [];

        if (schema?["properties"] is not JsonObject properties)
            return [];

        List<SettingDescriptor> settings = ReadProperties(properties, ReadRequired(schema), hints, parentPath: string.Empty);

        // Grouped in declaration order, then ordered inside each group. Order is a rank within a
        // group and nothing else: sorting globally first would let a group whose first setting
        // happens to be Order 1 jump ahead of the group declared before it, which is how a form with
        // sensible per-group ordering comes out shuffled.
        List<SettingGroup> groups = [];
        Dictionary<string, List<SettingDescriptor>> members = new(StringComparer.Ordinal);

        foreach (SettingDescriptor setting in settings)
        {
            string name = setting.Group ?? string.Empty;

            if (!members.TryGetValue(name, out List<SettingDescriptor>? bucket))
            {
                bucket = [];
                members[name] = bucket;
                groups.Add(new SettingGroup(name, bucket));
            }

            bucket.Add(setting);
        }

        // OrderBy rather than List.Sort, because List.Sort is not stable and the tie-break that
        // matters is the order the plugin declared its properties in - the only one an author can
        // predict without setting Order on every single setting.
        return [.. groups.Select(group =>
            new SettingGroup(group.Name, [.. group.Settings.OrderBy(setting => setting.Order)]))];
    }

    private static List<SettingDescriptor> ReadProperties(
        JsonObject properties,
        HashSet<string> required,
        JsonObject hints,
        string parentPath)
    {
        List<SettingDescriptor> settings = [];

        foreach (KeyValuePair<string, JsonNode?> property in properties)
        {
            if (property.Value is not JsonObject definition)
                continue;

            string path = parentPath.Length == 0 ? property.Key : $"{parentPath}/{property.Key}";

            settings.Add(ReadProperty(property.Key, path, definition, required.Contains(property.Key), hints));
        }

        // Left in declaration order here. The top level is grouped first and ordered per group by
        // the caller; a nested object's children are one group already, so they are ordered here.
        return parentPath.Length == 0 ? settings : [.. settings.OrderBy(setting => setting.Order)];
    }

    private static SettingDescriptor ReadProperty(
        string name,
        string path,
        JsonObject definition,
        bool required,
        JsonObject hints)
    {
        JsonObject hint = hints[path] as JsonObject ?? [];

        SettingEditor editor = ParseEditor(String(hint, "editor"));
        string? type = String(definition, "type");
        bool integral = string.Equals(type, "integer", StringComparison.Ordinal);

        List<SettingDescriptor> children = [];

        if (editor == SettingEditor.Object && definition["properties"] is JsonObject nested)
            children = ReadProperties(nested, ReadRequired(definition), hints, path);

        return new SettingDescriptor
        {
            Path = path,
            Name = name,
            Label = String(hint, "label") ?? String(definition, "title") ?? name,
            Help = String(hint, "help") ?? String(definition, "description"),
            Group = String(hint, "group"),
            Order = Int(hint, "order") ?? 0,
            Advanced = Bool(hint, "advanced") ?? false,
            Editor = editor,
            Required = required,
            Default = definition["default"]?.DeepClone(),
            Minimum = Double(definition, "minimum"),
            Maximum = Double(definition, "maximum"),
            Step = Double(hint, "step"),
            Integral = integral,
            MinimumLength = Int(definition, "minLength"),
            MaximumLength = Int(definition, "maxLength"),
            Pattern = String(definition, "pattern"),
            PickDirectory = Bool(hint, "pickDirectory") ?? false,
            Filter = String(hint, "filter"),
            Options = ReadOptions(hint),
            DependsOn = ReadDependencies(hint, path),
            ItemEditor = ParseEditor(String(hint, "itemEditor")),
            Children = children,
        };
    }

    private static IReadOnlyList<SettingOption> ReadOptions(JsonObject hint)
    {
        if (hint["options"] is not JsonArray options)
            return [];

        List<SettingOption> parsed = [];

        foreach (JsonNode? entry in options)
        {
            if (entry is not JsonObject option)
                continue;

            JsonNode? value = option["value"]?.DeepClone();

            parsed.Add(new SettingOption(value, String(option, "label") ?? value?.ToString() ?? string.Empty));
        }

        return parsed;
    }

    private static IReadOnlyList<SettingDependency> ReadDependencies(JsonObject hint, string path)
    {
        if (hint["dependsOn"] is not JsonArray dependencies)
            return [];

        // A dependency names a sibling, and the form resolves it against the document, so it is
        // rewritten here as a full path once rather than at every evaluation.
        int separator = path.LastIndexOf('/');
        string parent = separator < 0 ? string.Empty : path[..(separator + 1)];

        List<SettingDependency> parsed = [];

        foreach (JsonNode? entry in dependencies)
        {
            if (entry is not JsonObject dependency || String(dependency, "property") is not { } property)
                continue;

            parsed.Add(new SettingDependency(
                parent + property,
                dependency["value"]?.DeepClone(),
                Bool(dependency, "hide") ?? false));
        }

        return parsed;
    }

    private static HashSet<string> ReadRequired(JsonObject schema)
    {
        HashSet<string> required = new(StringComparer.Ordinal);

        if (schema["required"] is JsonArray names)
        {
            foreach (JsonNode? name in names)
            {
                if (name?.GetValueKind() == JsonValueKind.String)
                    required.Add(name.GetValue<string>());
            }
        }

        return required;
    }

    private static SettingEditor ParseEditor(string? editor) => editor switch
    {
        "toggle" => SettingEditor.Toggle,
        "enum" => SettingEditor.Enum,
        "flags" => SettingEditor.Flags,
        "slider" => SettingEditor.Slider,
        "number" => SettingEditor.Number,
        "text" => SettingEditor.Text,
        "multiline" => SettingEditor.Multiline,
        "path" => SettingEditor.Path,
        "color" => SettingEditor.Color,
        "font" => SettingEditor.Font,
        "hotkey" => SettingEditor.Hotkey,
        "duration" => SettingEditor.Duration,
        "list" => SettingEditor.List,
        "object" => SettingEditor.Object,

        // Includes null and anything a newer runner invented. An unknown editor is a property the
        // host cannot draw, which is precisely what the JSON row is for.
        _ => SettingEditor.Json,
    };

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            // A runner that sent something unparseable gets a settings page with no form and a JSON
            // editor, rather than a page that throws while it is being built.
            return null;
        }
    }

    private static string? String(JsonObject source, string name)
        => source[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static bool? Bool(JsonObject source, string name)
        => source[name]?.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static int? Int(JsonObject source, string name)
        => SettingNumber.TryRead(source[name], out double number) ? (int)number : null;

    private static double? Double(JsonObject source, string name)
        => SettingNumber.TryRead(source[name], out double number) ? number : null;
}
