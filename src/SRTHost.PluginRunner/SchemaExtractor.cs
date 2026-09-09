using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using SRTPluginBase.Abstractions;

namespace SRTHost.PluginRunner;

/// <summary>
/// Describes a plugin's settings type as a JSON Schema document plus a sidecar of UI hints, so the
/// host can render a form for a type it will never load.
/// </summary>
/// <remarks>
/// This is the piece the whole declarative-configuration design rests on. The host process never
/// loads a plugin assembly - that is the point of the out-of-process design - so it cannot reflect
/// over a configuration POCO. The runner can, once, at load, and what crosses the wire is data.
/// <para>
/// It walks <see cref="JsonTypeInfo"/> rather than the CLR type directly, and that is deliberate:
/// the names in the schema are then the exact keys the plugin's own serialiser reads and writes,
/// including whatever naming policy or <c>[JsonPropertyName]</c> its author chose. Reflecting over
/// <see cref="PropertyInfo"/> would produce a form that writes <c>Corner</c> into a document the
/// plugin reads as <c>corner</c>, and the setting would silently do nothing.
/// </para>
/// <para>
/// Every property's default is captured by serialising a fresh instance through that same
/// serialiser, so the schema carries the value in its real wire representation. That answers the
/// question the host could not otherwise settle: an enum is a number to one plugin and a string to
/// another depending on whether its context has a <c>JsonStringEnumConverter</c>, and the form has
/// to write back whichever the plugin will read.
/// </para>
/// <para>
/// Nothing here throws for a shape it does not understand. An unrecognised property gets an empty
/// schema and the <c>json</c> editor hint, which the host renders as a raw-JSON row - the
/// per-property fallback that keeps the declarative layer from ever fully blocking an author.
/// </para>
/// </remarks>
internal static class SchemaExtractor
{
    /// <summary>The 2020-12 dialect, stated so any other tool reading this knows what it has.</summary>
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// How deep nested objects are expanded before the rest is handed to the JSON editor.
    /// </summary>
    /// <remarks>
    /// Four is past anything a settings form should be asking a person to navigate, and a bound is
    /// required rather than tidy: a configuration type holding a reference back to its own parent
    /// type would otherwise recurse until the stack ran out, inside a load that has not yet answered
    /// the router.
    /// </remarks>
    private const int MaximumDepth = 4;

    /// <summary>The extracted description of a settings type.</summary>
    /// <param name="SchemaJson">JSON Schema 2020-12 for the settings type.</param>
    /// <param name="HintsJson">Presentation hints, keyed by setting path.</param>
    /// <param name="CurrentJson">The settings as they stand, in the plugin's own representation.</param>
    public readonly record struct ConfigurationDescription(
        string SchemaJson,
        string HintsJson,
        string CurrentJson);

    /// <summary>
    /// Describes <paramref name="plugin"/>'s settings.
    /// </summary>
    /// <remarks>
    /// Failure to describe is not failure to configure: if reflection over the type throws for any
    /// reason, an empty schema is returned, and the host falls back to the raw JSON editor over the
    /// current values. A plugin must not fail to load because its settings could not be drawn as a
    /// form.
    /// </remarks>
    public static ConfigurationDescription Describe(IConfigurablePlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);

        JsonTypeInfo typeInfo = plugin.ConfigurationTypeInfo;

        string current;

        try
        {
            current = JsonSerializer.Serialize(plugin.Configuration, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            current = "{}";
        }

        try
        {
            JsonObject defaults = SerializeDefaults(typeInfo);
            JsonObject hints = [];
            JsonObject schema = DescribeObject(typeInfo, defaults, hints, path: string.Empty, depth: 0, [])
                ?? new JsonObject { ["type"] = "object" };

            schema["$schema"] = Dialect;

            return new ConfigurationDescription(schema.ToJsonString(), hints.ToJsonString(), current);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or TargetInvocationException)
        {
            // The JSON editor still works, and saying so beats refusing the load.
            return new ConfigurationDescription("{}", "{}", current);
        }
    }

    /// <summary>
    /// Serialises a fresh instance of the settings type, to read each property's default in the
    /// representation the plugin's own serialiser produces.
    /// </summary>
    private static JsonObject SerializeDefaults(JsonTypeInfo typeInfo)
    {
        try
        {
            object? instance = typeInfo.CreateObject?.Invoke() ?? Activator.CreateInstance(typeInfo.Type);

            return instance is null
                ? []
                : JsonSerializer.SerializeToNode(instance, typeInfo) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is MissingMethodException or NotSupportedException
            or InvalidOperationException or JsonException or TargetInvocationException)
        {
            // A settings type without a usable parameterless constructor has no defaults to report.
            // The form still renders; every control simply starts from the current document.
            return [];
        }
    }

    private static JsonObject? DescribeObject(
        JsonTypeInfo typeInfo,
        JsonObject? defaults,
        JsonObject hints,
        string path,
        int depth,
        HashSet<Type> ancestors)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || !ancestors.Add(typeInfo.Type))
            return null;

        try
        {
            JsonObject properties = [];
            JsonArray required = [];

            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                if (property.Get is null || property.Set is null)
                    continue;

                PropertyInfo? clr = property.AttributeProvider as PropertyInfo;

                if (clr?.GetCustomAttribute<SrtHiddenAttribute>() is not null)
                    continue;

                string childPath = path.Length == 0 ? property.Name : $"{path}/{property.Name}";
                JsonNode? defaultValue = defaults?[property.Name];

                properties[property.Name] = DescribeProperty(
                    typeInfo, property, clr, defaultValue, hints, childPath, depth, ancestors);

                if (clr?.GetCustomAttribute<RequiredAttribute>() is not null)
                    required.Add(property.Name);
            }

            JsonObject schema = new()
            {
                ["type"] = "object",
                ["properties"] = properties,
            };

            if (required.Count > 0)
                schema["required"] = required;

            return schema;
        }
        finally
        {
            ancestors.Remove(typeInfo.Type);
        }
    }

    private static JsonObject DescribeProperty(
        JsonTypeInfo owner,
        JsonPropertyInfo property,
        PropertyInfo? clr,
        JsonNode? defaultValue,
        JsonObject hints,
        string path,
        int depth,
        HashSet<Type> ancestors)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        JsonObject schema = [];
        JsonObject hint = [];

        DisplayAttribute? display = clr?.GetCustomAttribute<DisplayAttribute>();
        SrtSettingAttribute? setting = clr?.GetCustomAttribute<SrtSettingAttribute>();

        string label = display?.Name ?? Humanize(property.Name);

        schema["title"] = label;
        hint["label"] = label;

        string? help = setting?.HelpText ?? display?.Description;

        if (!string.IsNullOrWhiteSpace(help))
        {
            schema["description"] = help;
            hint["help"] = help;
        }

        string? group = setting?.Group ?? display?.GroupName;

        if (!string.IsNullOrWhiteSpace(group))
            hint["group"] = group;

        int order = setting?.Order ?? display?.GetOrder() ?? 0;

        if (order != 0)
            hint["order"] = order;

        if (setting?.Advanced == true)
            hint["advanced"] = true;

        if (defaultValue is not null)
            schema["default"] = defaultValue.DeepClone();

        DescribeDependencies(clr, hint);

        hint["editor"] = DescribeShape(
            owner, property, clr, type, defaultValue, schema, hint, path, hints, depth, ancestors);

        hints[path] = hint;

        return schema;
    }

    /// <summary>
    /// Fills in the type-specific half of the schema and returns the editor the host should use.
    /// </summary>
    private static string DescribeShape(
        JsonTypeInfo owner,
        JsonPropertyInfo property,
        PropertyInfo? clr,
        Type type,
        JsonNode? defaultValue,
        JsonObject schema,
        JsonObject hint,
        string path,
        JsonObject hints,
        int depth,
        HashSet<Type> ancestors)
    {
        if (type == typeof(bool))
        {
            schema["type"] = "boolean";
            return "toggle";
        }

        if (type.IsEnum)
            return DescribeEnum(type, defaultValue, schema, hint);

        if (type == typeof(string))
            return DescribeString(clr, schema, hint);

        if (IsIntegral(type) || IsFractional(type))
            return DescribeNumber(clr, type, schema, hint);

        if (type == typeof(TimeSpan))
        {
            // TimeSpan serialises as "hh:mm:ss" through System.Text.Json, so the wire type is a
            // string and only the editor knows it is a duration.
            schema["type"] = "string";
            schema["format"] = "duration";
            return "duration";
        }

        if (type == typeof(DateTimeOffset) || type == typeof(DateTime))
        {
            schema["type"] = "string";
            schema["format"] = "date-time";
            return "text";
        }

        if (type == typeof(Uri) || type == typeof(Guid))
        {
            schema["type"] = "string";
            return "text";
        }

        if (TryGetElementType(type, out Type? element))
        {
            // Only lists of primitives get an editor. A list of objects is a table of forms, which is
            // more UI than a settings pane should grow for a case no plugin has yet needed - it
            // degrades to the JSON editor and remains fully editable.
            JsonObject items = [];
            string elementEditor = DescribePrimitive(element, items);

            schema["type"] = "array";
            schema["items"] = items;

            if (elementEditor == "json")
                return "json";

            hint["itemEditor"] = elementEditor;
            return "list";
        }

        if (depth < MaximumDepth && TryGetTypeInfo(owner, type, out JsonTypeInfo? nested) && nested is not null)
        {
            JsonObject? child = DescribeObject(
                nested, defaultValue as JsonObject, hints, path, depth + 1, ancestors);

            if (child is not null)
            {
                foreach (KeyValuePair<string, JsonNode?> member in child)
                    schema[member.Key] = member.Value?.DeepClone();

                return "object";
            }
        }

        // Deliberately no "type" keyword: an empty schema validates anything, which is the honest
        // description of a property the host cannot draw and will hand to the JSON editor.
        return "json";
    }

    private static string DescribeEnum(Type type, JsonNode? defaultValue, JsonObject schema, JsonObject hint)
    {
        // The wire representation is whatever the plugin's own serialiser produced for the default:
        // a string with a JsonStringEnumConverter in play, a number without one. The form has to
        // write back the form the plugin will read, and this is the only reliable way to know.
        bool asString = defaultValue is JsonValue value && value.GetValueKind() == JsonValueKind.String;

        bool flags = type.GetCustomAttribute<FlagsAttribute>() is not null;

        JsonArray members = [];
        JsonArray options = [];

        foreach (string name in Enum.GetNames(type))
        {
            object member = Enum.Parse(type, name);

            JsonNode encoded = asString
                ? JsonValue.Create(name)
                : JsonValue.Create(Convert.ToInt64(member, System.Globalization.CultureInfo.InvariantCulture));

            members.Add(encoded.DeepClone());

            options.Add(new JsonObject
            {
                ["value"] = encoded.DeepClone(),
                ["label"] = MemberLabel(type, name),
            });
        }

        schema["type"] = asString ? "string" : "integer";
        hint["options"] = options;

        // A [Flags] enum is a combination rather than a choice, so its members are checkboxes and the
        // value is not one of the listed names - which is why only the non-flags case constrains the
        // schema with "enum". Flags written as strings are a comma-separated list System.Text.Json
        // parses but the host will not try to compose, so those degrade to a text box.
        if (!flags)
        {
            schema["enum"] = members;
            return "enum";
        }

        return asString ? "text" : "flags";
    }

    private static string DescribeString(PropertyInfo? clr, JsonObject schema, JsonObject hint)
    {
        schema["type"] = "string";

        if (clr?.GetCustomAttribute<StringLengthAttribute>() is { } length)
        {
            if (length.MinimumLength > 0)
                schema["minLength"] = length.MinimumLength;

            schema["maxLength"] = length.MaximumLength;
        }

        if (clr?.GetCustomAttribute<MinLengthAttribute>() is { } minimum)
            schema["minLength"] = minimum.Length;

        if (clr?.GetCustomAttribute<MaxLengthAttribute>() is { } maximum)
            schema["maxLength"] = maximum.Length;

        if (clr?.GetCustomAttribute<RegularExpressionAttribute>() is { } pattern)
            schema["pattern"] = pattern.Pattern;

        if (clr?.GetCustomAttribute<SrtColorAttribute>() is not null)
            return "color";

        if (clr?.GetCustomAttribute<SrtFontAttribute>() is not null)
            return "font";

        if (clr?.GetCustomAttribute<SrtHotkeyAttribute>() is not null)
            return "hotkey";

        if (clr?.GetCustomAttribute<SrtPathAttribute>() is { } file)
        {
            hint["pickDirectory"] = file.Directory;

            if (!string.IsNullOrWhiteSpace(file.Filter))
                hint["filter"] = file.Filter;

            return "path";
        }

        if (clr?.GetCustomAttribute<DataTypeAttribute>()?.DataType == DataType.MultilineText)
            return "multiline";

        return "text";
    }

    private static string DescribeNumber(PropertyInfo? clr, Type type, JsonObject schema, JsonObject hint)
    {
        bool integral = IsIntegral(type);

        schema["type"] = integral ? "integer" : "number";

        // A colour packed into an ARGB integer is still an integer on the wire; only the editor
        // changes.
        bool color = clr?.GetCustomAttribute<SrtColorAttribute>() is not null;

        if (clr?.GetCustomAttribute<RangeAttribute>() is { } range)
        {
            JsonNode? minimum = ToNumber(range.Minimum, integral);
            JsonNode? maximum = ToNumber(range.Maximum, integral);

            if (minimum is not null && maximum is not null)
            {
                schema["minimum"] = minimum;
                schema["maximum"] = maximum;

                if (!color)
                {
                    // A bounded number is the one case where a slider beats a spinner: the range is
                    // the information, and dragging within a known range is faster than typing.
                    hint["step"] = integral ? 1 : 0.01;
                    return "slider";
                }
            }
        }

        return color ? "color" : "number";
    }

    /// <summary>Describes an array element, which gets no hints of its own.</summary>
    private static string DescribePrimitive(Type type, JsonObject schema)
    {
        Type element = Nullable.GetUnderlyingType(type) ?? type;

        if (element == typeof(bool))
        {
            schema["type"] = "boolean";
            return "toggle";
        }

        if (element == typeof(string))
        {
            schema["type"] = "string";
            return "text";
        }

        if (IsIntegral(element))
        {
            schema["type"] = "integer";
            return "number";
        }

        if (IsFractional(element))
        {
            schema["type"] = "number";
            return "number";
        }

        return "json";
    }

    private static void DescribeDependencies(PropertyInfo? clr, JsonObject hint)
    {
        if (clr is null)
            return;

        JsonArray dependencies = [];

        foreach (SrtDependsOnAttribute dependency in clr.GetCustomAttributes<SrtDependsOnAttribute>())
        {
            dependencies.Add(new JsonObject
            {
                ["property"] = dependency.PropertyName,
                ["value"] = dependency.Value is null ? null : JsonValue.Create(dependency.Value),
                ["hide"] = dependency.HideWhenUnmet,
            });
        }

        if (dependencies.Count > 0)
            hint["dependsOn"] = dependencies;
    }

    private static bool TryGetTypeInfo(JsonTypeInfo owner, Type type, out JsonTypeInfo? typeInfo)
    {
        try
        {
            typeInfo = owner.Options.GetTypeInfo(type);
            return typeInfo.Kind == JsonTypeInfoKind.Object;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            // A source-generated context that does not know the nested type. It stays editable
            // through the JSON editor, which is the whole reason that fallback exists.
            typeInfo = null;
            return false;
        }
    }

    private static bool TryGetElementType(Type type, out Type element)
    {
        if (type.IsArray && type.GetElementType() is { } array)
        {
            element = array;
            return true;
        }

        foreach (Type contract in type.GetInterfaces().Prepend(type))
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                element = contract.GetGenericArguments()[0];
                return true;
            }
        }

        element = typeof(object);
        return false;
    }

    private static JsonNode? ToNumber(object? value, bool integral)
    {
        try
        {
            return value is null
                ? null
                : integral
                    ? JsonValue.Create(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture))
                    : JsonValue.Create(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // [Range] can carry a Type plus two strings, and those strings need not be numbers.
            return null;
        }
    }

    private static string MemberLabel(Type type, string name)
        => type.GetField(name)?.GetCustomAttribute<DisplayAttribute>()?.Name ?? Humanize(name);

    /// <summary>
    /// Turns <c>ShowHealthBar</c> into <c>Show health bar</c>, for a property with no
    /// <c>[Display(Name)]</c>.
    /// </summary>
    /// <remarks>
    /// A guess, and a deliberate one: an author who cares writes the label, and the alternative for
    /// everyone else is a form of run-together identifiers. Acronyms are left alone - <c>UIScale</c>
    /// becomes <c>UI scale</c> rather than <c>U I Scale</c> - because a run of capitals is the one
    /// case where splitting on every capital is obviously wrong.
    /// </remarks>
    private static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        System.Text.StringBuilder builder = new(name.Length + 8);

        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];

            bool boundary = index > 0
                && char.IsUpper(current)
                && (!char.IsUpper(name[index - 1])
                    || (index + 1 < name.Length && char.IsLower(name[index + 1])));

            if (boundary)
                builder.Append(' ').Append(char.ToLowerInvariant(current));
            else if (index == 0)
                builder.Append(char.ToUpperInvariant(current));
            else
                builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool IsIntegral(Type type)
        => type == typeof(byte) || type == typeof(sbyte)
        || type == typeof(short) || type == typeof(ushort)
        || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong);

    private static bool IsFractional(Type type)
        => type == typeof(float) || type == typeof(double) || type == typeof(decimal);
}
