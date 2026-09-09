using System.Text.Json.Nodes;

namespace SRTHost.Core.Configuration;

/// <summary>
/// Reads and writes a value inside a settings document by its <c>Parent/Child</c> path.
/// </summary>
/// <remarks>
/// The form edits one <see cref="JsonObject"/> and every control knows only where its own value
/// lives, which is what keeps the editors independent of the document's shape. Paths use the same
/// segments the schema does - the plugin's own JSON keys - so no mapping is involved anywhere.
/// <para>
/// Not JSON Pointer, deliberately: pointer escaping (<c>~0</c>, <c>~1</c>) would have to be applied
/// by the runner and undone here for keys that in practice are C# identifiers, and getting that
/// half-right is worse than not doing it. Keys containing a slash are the one shape this cannot
/// address, and the runner has no way to produce one from a property name.
/// </para>
/// </remarks>
public static class SettingPath
{
    /// <summary>Reads the value at <paramref name="path"/>, or null when nothing is there.</summary>
    public static JsonNode? Get(JsonObject root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        JsonNode? current = root;

        foreach (string segment in path.Split('/'))
        {
            if (current is not JsonObject holder || !holder.TryGetPropertyValue(segment, out JsonNode? child))
                return null;

            current = child;
        }

        return current;
    }

    /// <summary>
    /// Writes <paramref name="value"/> at <paramref name="path"/>, creating the objects on the way.
    /// </summary>
    /// <remarks>
    /// Creating intermediates matters more than it looks: a settings file written before a nested
    /// group existed has no object to put the new value in, and a form that could not write into one
    /// would silently drop every edit under that group.
    /// </remarks>
    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] segments = path.Split('/');
        JsonObject holder = root;

        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (holder[segments[index]] is JsonObject existing)
            {
                holder = existing;
                continue;
            }

            JsonObject created = [];
            holder[segments[index]] = created;
            holder = created;
        }

        // Detached first, because a JsonNode belongs to exactly one parent and assigning one that
        // still has a parent throws - which is easy to hit here, since values come from schema
        // defaults and from other documents.
        holder[segments[^1]] = value?.DeepClone();
    }
}
