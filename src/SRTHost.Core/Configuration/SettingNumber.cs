using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SRTHost.Core.Configuration;

/// <summary>
/// Reads a JSON number whatever it is backed by.
/// </summary>
/// <remarks>
/// <see cref="JsonValue"/> does not convert between numeric types on the way out:
/// <c>JsonValue.Create(30).TryGetValue(out double _)</c> is <see langword="false"/>, because that
/// node is backed by an <see cref="int"/> and the accessor is an unboxing cast rather than a
/// conversion. A node parsed from text is backed by a <see cref="JsonElement"/> instead and answers
/// every numeric type, which is what makes this so easy to get wrong: the parsing path works and the
/// constructed path silently does not.
/// <para>
/// Both paths meet in this code. Defaults and constructed values come from
/// <c>JsonValue.Create</c>, current values come from <c>JsonNode.Parse</c>, and a control has no
/// idea which one it is holding - so every numeric read goes through here. Caught by
/// <c>SettingValidatorTests</c>, where a range check quietly passed everything.
/// </para>
/// </remarks>
public static class SettingNumber
{
    /// <summary>Reads <paramref name="node"/> as a number.</summary>
    /// <returns>Whether it is one.</returns>
    public static bool TryRead(JsonNode? node, out double value)
    {
        value = 0;

        if (node is not JsonValue number || number.GetValueKind() != JsonValueKind.Number)
            return false;

        if (number.TryGetValue(out double asDouble))
        {
            value = asDouble;
            return true;
        }

        if (number.TryGetValue(out long asLong))
        {
            value = asLong;
            return true;
        }

        if (number.TryGetValue(out decimal asDecimal))
        {
            value = (double)asDecimal;
            return true;
        }

        // Whatever numeric type it is backed by, its JSON text is a number by definition - so this
        // catches int, float, ulong and anything else without a case per type.
        return double.TryParse(
            number.ToJsonString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>Reads <paramref name="node"/> as a whole number.</summary>
    /// <returns>Whether it is one.</returns>
    public static bool TryRead(JsonNode? node, out long value)
    {
        if (TryRead(node, out double number))
        {
            value = (long)number;
            return true;
        }

        value = 0;
        return false;
    }
}
