using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SRTHost.Core;

/// <summary>
/// Registers or unregisters the host as a per-user sign-in item.
/// </summary>
/// <remarks>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>, deliberately, rather than the
/// machine-wide <c>HKLM</c> hive or a scheduled task. The installer is per-user and writes to
/// <c>%LOCALAPPDATA%</c>, so a machine-wide entry would point at a path other accounts cannot read;
/// and both alternatives need elevation, which nothing else in this application does. A checkbox
/// that raises a UAC prompt is a checkbox people leave alone.
/// <para>
/// The command written is <c>"&lt;host&gt;" --minimized</c>: something launched at sign-in that
/// steals focus with a window is a thing users disable rather than configure.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The value name under the Run key. Also what Task Manager's Startup tab shows.</summary>
    public const string ValueName = "SRT Host";

    /// <summary>Whether the host is currently registered to start at sign-in.</summary>
    /// <remarks>
    /// Answers false rather than throwing on any registry failure. The registry can be locked down
    /// by policy, and a settings page that cannot render because it could not read a checkbox is
    /// worse than one showing the checkbox unticked.
    /// </remarks>
    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the sign-in entry.</summary>
    /// <param name="enabled">Whether the host should start at sign-in.</param>
    /// <returns>Null on success, or why it could not be changed.</returns>
    public static string? Set(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }

            key.SetValue(ValueName, $"\"{ExecutablePath()}\" --minimized", RegistryValueKind.String);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The path to write, which is the host executable rather than the managed assembly.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> is the apphost - <c>SRTHost.exe</c> - whereas
    /// <c>Assembly.Location</c> is <c>SRTHost.dll</c>, which Explorer cannot launch. The fallback
    /// reconstructs the same thing from the base directory for the case where <c>ProcessPath</c> is
    /// null, which happens when the runtime is hosted rather than launched.
    /// </remarks>
    private static string ExecutablePath()
        => Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, Process.GetCurrentProcess().ProcessName + ".exe");
}
