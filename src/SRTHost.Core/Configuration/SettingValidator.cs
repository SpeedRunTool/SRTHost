using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SRTHost.Core.Configuration;

/// <summary>
/// Checks one setting's value against what the schema said about it.
/// </summary>
/// <remarks>
/// <b>Advisory, not authoritative.</b> The runner re-runs the plugin's own
/// <c>Validator.TryValidateObject</c> over the whole object before applying anything, and that is
/// the answer that counts - it sees the DataAnnotations this schema could only partly express, and
/// it is the only side that can see a custom <c>ValidationAttribute</c> at all. What this exists for
/// is immediacy: telling somebody the number is out of range while they are typing it, instead of
/// after a round trip to another process.
/// <para>
/// Being advisory is also why it never blocks a save. A value it dislikes is still sent; the runner
/// gets the final word, and its message is what the form shows if the two disagree.
/// </para>
/// </remarks>
public static class SettingValidator
{
    /// <summary>How long a pattern is given before it is abandoned.</summary>
    /// <remarks>
    /// The pattern comes from a plugin's <c>[RegularExpression]</c>, so it is somebody else's code
    /// running against the user's keystrokes on the UI thread. A catastrophically backtracking
    /// pattern is a plausible accident rather than an attack, and either way a settings box must not
    /// be able to freeze the window.
    /// </remarks>
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Validates <paramref name="value"/> for <paramref name="setting"/>.
    /// </summary>
    /// <returns>The problem, or null when there is none to report.</returns>
    public static string? Validate(SettingDescriptor setting, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(setting);

        JsonValueKind kind = value?.GetValueKind() ?? JsonValueKind.Null;

        if (setting.Required && kind is JsonValueKind.Null or JsonValueKind.Undefined)
            return $"{setting.Label} is required.";

        if (kind == JsonValueKind.String)
            return ValidateString(setting, value!.GetValue<string>());

        if (SettingNumber.TryRead(value, out double actual))
            return ValidateNumber(setting, actual);

        return null;
    }

    private static string? ValidateString(SettingDescriptor setting, string value)
    {
        if (setting.Required && string.IsNullOrWhiteSpace(value))
            return $"{setting.Label} is required.";

        if (setting.MinimumLength is { } minimum && value.Length < minimum)
            return $"{setting.Label} must be at least {minimum} characters.";

        if (setting.MaximumLength is { } maximum && value.Length > maximum)
            return $"{setting.Label} must be at most {maximum} characters.";

        if (setting.Editor == SettingEditor.Duration && !TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _))
            return $"{setting.Label} must be a duration, like 00:01:30.";

        if (setting.Pattern is { Length: > 0 } pattern)
        {
            try
            {
                if (!Regex.IsMatch(value, pattern, RegexOptions.None, PatternTimeout))
                    return $"{setting.Label} is not in the expected format.";
            }
            catch (Exception ex) when (ex is RegexMatchTimeoutException or ArgumentException)
            {
                // An unusable pattern is the plugin author's problem, and the runner will report it
                // properly. Silently declining to check is better than blaming the user's input.
                return null;
            }
        }

        return null;
    }

    private static string? ValidateNumber(SettingDescriptor setting, double value)
    {
        if (setting.Minimum is { } minimum && value < minimum)
            return $"{setting.Label} must be at least {Format(minimum, setting.Integral)}.";

        if (setting.Maximum is { } maximum && value > maximum)
            return $"{setting.Label} must be at most {Format(maximum, setting.Integral)}.";

        return null;
    }

    private static string Format(double value, bool integral)
        => integral
            ? ((long)value).ToString(CultureInfo.CurrentCulture)
            : value.ToString(CultureInfo.CurrentCulture);
}
