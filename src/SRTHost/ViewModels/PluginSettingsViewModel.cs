using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SRTHost.Core;
using SRTHost.Core.Configuration;
using SRTHost.Core.Supervision;
using SRTHost.Ipc;

namespace SRTHost.ViewModels;

/// <summary>A heading and the controls under it.</summary>
/// <param name="Name">The heading, or empty for the settings declared with no group.</param>
/// <param name="Settings">Its controls, in order.</param>
public sealed record SettingGroupViewModel(string Name, IReadOnlyList<SettingNodeViewModel> Settings)
{
    /// <summary>Whether the heading is worth drawing.</summary>
    public bool HasName => Name.Length > 0;
}

/// <summary>
/// One plugin's settings: a generated form, and the same document as raw JSON.
/// </summary>
/// <remarks>
/// The host has never loaded the plugin's configuration type and never will, so everything here is
/// driven by the schema and hints its runner sent - see <c>SchemaExtractor</c> on the other side.
/// What the page holds is a <see cref="JsonObject"/>, and every control reads and writes its own
/// path within it. The two tabs are therefore two views of one document rather than two documents:
/// switching to JSON shows exactly what Save would send, which is what makes the fallback
/// trustworthy.
/// <para>
/// Saving asks the runner first. It deserialises into the real type and runs the plugin's own
/// annotations, so only settings the plugin accepted are written to disk - a document that would
/// stop the plugin loading next time cannot be saved by pressing Save.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class PluginSettingsViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly HostContext host;
    private readonly ILogger logger;
    private readonly List<SettingNodeViewModel> nodes = [];

    private JsonObject document = [];
    private bool updatingJson;

    /// <summary>Builds the page for one plugin.</summary>
    public PluginSettingsViewModel(HostContext host, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        this.host = host;
        PluginId = pluginId;
        logger = host.LoggerFactory.CreateLogger<PluginSettingsViewModel>();

        Reload();
    }

    /// <summary>The plugin these settings belong to.</summary>
    public string PluginId { get; }

    /// <summary>The form, by group.</summary>
    public ObservableCollection<SettingGroupViewModel> Groups { get; } = [];

    /// <summary>Whether there is a form to show at all.</summary>
    /// <remarks>
    /// False for a plugin with no settings, and for one whose schema described nothing the host
    /// could draw. The JSON tab is shown either way, which is what keeps a plugin configurable when
    /// its schema is empty - a stopped plugin, or one whose settings type defeated the extractor.
    /// </remarks>
    [ObservableProperty]
    public partial bool HasForm { get; set; }

    /// <summary>Whether this plugin has settings at all.</summary>
    [ObservableProperty]
    public partial bool IsConfigurable { get; set; }

    /// <summary>Whether advanced settings are shown.</summary>
    [ObservableProperty]
    public partial bool ShowAdvanced { get; set; }

    /// <summary>Whether the form has any advanced settings to show.</summary>
    [ObservableProperty]
    public partial bool HasAdvanced { get; set; }

    /// <summary>The document as text, which is what the JSON tab edits.</summary>
    [ObservableProperty]
    public partial string JsonText { get; set; } = "{}";

    /// <summary>Why the JSON tab's text is not usable, or null.</summary>
    [ObservableProperty]
    public partial string? JsonError { get; set; }

    /// <summary>Whether there is anything to save.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    /// <summary>Whether a save is in flight.</summary>
    [ObservableProperty]
    public partial bool Busy { get; set; }

    /// <summary>What happened to the last save.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    /// <summary>Whether <see cref="Status"/> is a failure rather than a confirmation.</summary>
    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    /// <summary>Where the settings file lives, shown under the form.</summary>
    public string FilePath => HostPaths.ConfigFile(PluginId);

    /// <summary>
    /// Rebuilds the page from what the runner declared, or from the stored file when it is not
    /// running.
    /// </summary>
    public void Reload()
    {
        ReadyMessage? ready = host.Runtime.Supervisor.Describe(PluginId);

        string? current = ready?.ConfigurationJson ?? PluginSupervisor.ReadStoredConfiguration(PluginId);

        // A running plugin that declared no schema has no settings; a stopped one may simply not be
        // able to say. Treating a stored file as evidence of settings is what keeps a stopped
        // plugin configurable.
        IsConfigurable = ready is null
            ? current is not null
            : ready.ConfigurationSchemaJson is not null;

        document = Parse(current) ?? [];

        IReadOnlyList<SettingGroup> groups = ready is null
            ? []
            : SchemaReader.Read(ready.ConfigurationSchemaJson, ready.ConfigurationHintsJson);

        Build(groups);
        RefreshJson();

        IsDirty = false;
        Status = null;
        StatusIsError = false;
    }

    /// <summary>Called by a control when the user changes its value.</summary>
    public void ValueChanged(SettingNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // A group writes nothing of its own; its children each write their own path.
        if (node is not ObjectSettingViewModel)
            SettingPath.Set(document, node.Path, node.ToJson());

        node.Error = SettingValidator.Validate(node.Descriptor, SettingPath.Get(document, node.Path));

        RefreshDependencies();
        RefreshJson();

        IsDirty = true;
        Status = null;
    }

    /// <summary>Sends the document to the plugin and, if it accepts it, saves it.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (JsonError is not null)
        {
            Status = "Fix the JSON before saving.";
            StatusIsError = true;
            return;
        }

        Busy = true;

        try
        {
            string? refusal = await host.Runtime.Supervisor
                .ApplyConfigurationAsync(PluginId, document.ToJsonString(Indented), CancellationToken.None)
                .ConfigureAwait(true);

            StatusIsError = refusal is not null;
            Status = refusal ?? "Saved.";
            IsDirty = refusal is not null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saving settings for {PluginId} failed.", PluginId);

            StatusIsError = true;
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Throws away unsaved edits.</summary>
    [RelayCommand]
    private void Revert() => Reload();

    /// <summary>Restores every setting to the value a fresh settings object would have.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        foreach (SettingNodeViewModel node in nodes)
        {
            if (node is ObjectSettingViewModel)
                continue;

            node.Load(node.Descriptor.Default);
            SettingPath.Set(document, node.Path, node.ToJson());
        }

        RefreshDependencies();
        RefreshJson();

        IsDirty = true;
        Status = null;
    }

    /// <summary>Re-indents whatever is in the JSON tab.</summary>
    [RelayCommand]
    private void FormatJson()
    {
        if (Parse(JsonText) is { } parsed)
            JsonText = parsed.ToJsonString(Indented);
    }

    /// <summary>Adopts what the JSON tab now says, and refills the form from it.</summary>
    /// <remarks>
    /// Called as the text changes rather than on a button, because the two tabs are one document and
    /// a form showing something different from the JSON beside it is worse than no form at all.
    /// While the text does not parse the form simply stops following - the last good document stays
    /// in place, so a stray keystroke cannot wipe the settings.
    /// </remarks>
    public void JsonEdited(string text)
    {
        if (updatingJson)
            return;

        if (Parse(text) is not { } parsed)
        {
            JsonError = "This is not valid JSON.";
            return;
        }

        JsonError = null;
        document = parsed;

        LoadValues();
        RefreshDependencies();

        IsDirty = true;
        Status = null;
    }

    private void Build(IReadOnlyList<SettingGroup> groups)
    {
        nodes.Clear();
        Groups.Clear();

        IReadOnlyList<string> fonts = SystemFonts();

        foreach (SettingGroup group in groups)
        {
            List<SettingNodeViewModel> built = [];

            foreach (SettingDescriptor descriptor in group.Settings)
                built.Add(Create(descriptor, fonts));

            Groups.Add(new SettingGroupViewModel(group.Name, built));
        }

        HasForm = nodes.Count > 0;
        HasAdvanced = nodes.Any(node => node.Advanced);

        LoadValues();
        RefreshDependencies();
    }

    private SettingNodeViewModel Create(SettingDescriptor descriptor, IReadOnlyList<string> fonts)
    {
        SettingNodeViewModel node = descriptor.Editor switch
        {
            SettingEditor.Toggle => new BooleanSettingViewModel(this, descriptor),
            SettingEditor.Enum => new EnumSettingViewModel(this, descriptor),
            SettingEditor.Flags => new FlagsSettingViewModel(this, descriptor),
            SettingEditor.Slider or SettingEditor.Number => new NumberSettingViewModel(this, descriptor),
            SettingEditor.Text or SettingEditor.Multiline or SettingEditor.Path
                => new TextSettingViewModel(this, descriptor),
            SettingEditor.Color => new ColorSettingViewModel(this, descriptor),
            SettingEditor.Font => new FontSettingViewModel(this, descriptor, fonts),
            SettingEditor.Hotkey => new HotkeySettingViewModel(this, descriptor),
            SettingEditor.Duration => new DurationSettingViewModel(this, descriptor),
            SettingEditor.List => new ListSettingViewModel(this, descriptor),
            SettingEditor.Object => new ObjectSettingViewModel(
                this, descriptor, [.. descriptor.Children.Select(child => Create(child, fonts))]),
            _ => new JsonSettingViewModel(this, descriptor),
        };

        // Every node goes in the flat list, children included - they validate, they depend on
        // siblings and Reset has to reach them. A group's children are already here, because the
        // recursion above created them before this line ran.
        nodes.Add(node);

        return node;
    }

    private void LoadValues()
    {
        foreach (SettingNodeViewModel node in nodes)
        {
            node.Load(SettingPath.Get(document, node.Path));
            node.Error = node is ObjectSettingViewModel
                ? null
                : SettingValidator.Validate(node.Descriptor, SettingPath.Get(document, node.Path));
        }
    }

    /// <summary>
    /// Applies every <c>[SrtDependsOn]</c> and the advanced filter to what is on screen.
    /// </summary>
    private void RefreshDependencies()
    {
        foreach (SettingNodeViewModel node in nodes)
        {
            bool met = true;

            foreach (SettingDependency dependency in node.Descriptor.DependsOn)
            {
                if (!JsonNode.DeepEquals(SettingPath.Get(document, dependency.PropertyPath), dependency.Value))
                {
                    met = false;
                    break;
                }
            }

            bool hidden = !met && node.Descriptor.DependsOn.Any(dependency => dependency.HideWhenUnmet);

            node.IsEnabled = met;
            node.IsVisible = !hidden && (!node.Advanced || ShowAdvanced);
        }
    }

    private void RefreshJson()
    {
        updatingJson = true;

        try
        {
            JsonText = document.ToJsonString(Indented);
            JsonError = null;
        }
        finally
        {
            updatingJson = false;
        }
    }

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
            return null;
        }
    }

    /// <summary>Every font family installed, for a <c>[SrtFont]</c> setting.</summary>
    /// <remarks>
    /// Read once per page rather than per control: a machine with a few hundred fonts would
    /// otherwise pay for the enumeration once for every font setting on the form.
    /// </remarks>
    private static IReadOnlyList<string> SystemFonts()
    {
        try
        {
            return [.. FontManager.Current.SystemFonts.Select(font => font.Name).Order(StringComparer.CurrentCulture)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // No font manager outside a running application - which is the case in a unit test.
            return [];
        }
    }

    partial void OnShowAdvancedChanged(bool value) => RefreshDependencies();

    partial void OnJsonTextChanged(string value) => JsonEdited(value);
}
