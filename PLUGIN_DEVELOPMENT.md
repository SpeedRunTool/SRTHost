# Writing a plugin for SRT Host 5

This is the guide for contract **generation 5**. It replaces everything you know about
`IPluginProvider` and `IPluginUI`: generations 1 through 4 are not loadable by this host, and the
break is deliberate — see [Porting a generation-1 plugin](#porting-a-generation-1-plugin).

Everything described here is demonstrated by four plugins in this repository, under `src/`. They are
built and deployed by an ordinary solution build, so you can run them before you write anything.

| Example | What it is for |
|---|---|
| `SpeedRunTool.Demo.Contracts` | A payload-contract assembly: DTOs, a channel id, a `JsonSerializerContext`, nothing else |
| `SpeedRunTool.Demo.Producer` | The smallest realistic producer |
| `SpeedRunTool.Demo.Consumer` | A configurable consumer, and an exhaustive tour of the settings form |
| `SpeedRunTool.Demo.ConsumerWindow` | A consumer that opens its own **Avalonia window** — the WinForms migration reference |
| `SpeedRunTool.Demo.ConsumerJson` | A **schemaless** consumer: subscribes to `*`, references no payload type |

---

## 1. How the host runs your plugin

`SRTHost.exe` never loads a plugin assembly. Each plugin gets its **own process** —
`SRTHost.PluginRunner64.exe` or `SRTHost.PluginRunner32.exe` — connected back to the host by a named
pipe. The host routes payloads between those processes and shows you what is happening; it never
deserialises a payload, and it never holds a reference to one of your types.

```
                       ┌──────────────────────────────────┐
                       │  SRTHost.exe  (AnyCPU)           │
                       │  UI · router · supervisor        │
                       └───┬───────────┬───────────┬──────┘
        named pipe per plugin          │           │
            ┌──────────────┘           │           └──────────────┐
┌───────────▼───────────┐  ┌───────────▼───────────┐  ┌───────────▼───────────┐
│ PluginRunner64.exe    │  │ PluginRunner64.exe    │  │ PluginRunner32.exe    │
│  your producer        │  │  a consumer           │  │  a 32-bit producer    │
│  reads game memory    │  │  draws / forwards     │  │  reads a 32-bit game  │
└───────────────────────┘  └───────────────────────┘  └───────────────────────┘
```

What follows from that, and is worth internalising before you start:

- **A crash is contained.** A plugin that faults takes down its own runner. The host restarts it
  with a backoff and everything else keeps running.
- **Bitness is per plugin.** A producer for a 32-bit game runs in the 32-bit runner; a consumer of
  its data can be 64-bit. Nothing has to agree with anything else.
- **A consumer never references a producer.** It binds to a *channel id* at run time. Any producer
  publishing that channel will do — which is the entire point, and the one thing the previous
  generation could not express.
- **Payloads cross a process boundary as bytes.** Usually UTF-8 JSON. You do not get to send an
  object graph, and a payload type with behaviour on it is a payload type you will have to rewrite.

---

## 2. A plugin project, end to end

### The project file

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <Nullable>enable</Nullable>

    <!-- These three become the plugin's Name, Description and Author in the host's UI. -->
    <Product>RE4R Producer</Product>
    <Description>Reads Resident Evil 4 Remake's game memory.</Description>
    <Authors>SpeedrunTooling</Authors>

    <!-- Without this, SRTPluginBase.dll is not copied beside your plugin and it will not load. -->
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SRTPluginBase" Version="5.0.0" />
  </ItemGroup>

</Project>
```

`CopyLocalLockFileAssemblies` is the line nobody expects. The SDK defaults it to `false` for a
library, on the reasonable grounds that whatever consumes a library resolves its own packages. A
plugin has no such consumer: it is loaded out of a directory by a runner that knows nothing about
NuGet, so anything not in that directory must come from the runner's own closure or it does not
resolve at all. Without it your plugin fails to load with a `FileNotFoundException` naming the base
class you derived from.

**`net10.0`, not `net10.0-windows`.** The runner does not carry the Windows Desktop framework, so a
plugin referencing `System.Windows.Forms` or `PresentationFramework` fails assembly resolution at
load no matter what its own project targets. See [UI from inside a plugin](#7-ui-from-inside-a-plugin).

### The identity attribute

Exactly one attribute is mandatory, applied at assembly level:

```csharp
[assembly: SrtPluginAssembly("SpeedrunTooling.RE4R.Producer", typeof(RE4RProducer))]
```

Everything else the host needs is already in assembly metadata, so nothing is stated twice: name from
`<Product>`, description from `<Description>`, author from `<Authors>`, version from the assembly
version, **kind from which interface your type implements**, architecture from the PE header. Only
the id has to be declared, because an identity should never be inferred.

The attribute also has three optional properties:

| Property | Use it when |
|---|---|
| `Architecture` | The PE header lies about what you need — an AnyCPU assembly that reads a 32-bit game through an AnyCPU helper. `X86`, `X64`, or `Any` (the default; the host picks 64-bit) |
| `RequiresUiThread` | Your plugin needs the runner to give it an STA thread with a Win32 message pump. Not for Avalonia — see §7 |
| `Generation` | Never. It exists as an override and means "infer" when left at zero, which is what you want |

### The id

`<Author>.<Subject>.<Name>`, like a NuGet package id — `SpeedrunTooling.RE4R.Producer`,
`JohnDoe.RE4R.Overlay`. Not reverse-DNS: that is a Java and Apple convention, and .NET's own answer
to a globally unique dotted identifier is this shape, which your assemblies and namespaces already
follow.

- **The leading segment names you**, the author — in practice, the GitHub organisation that owns the
  plugin's repository. A `SRT.` prefix is wrong: it carries no information and makes a third-party
  plugin read as first-party.
- **It is immutable.** It keys your configuration file, your state directory, your catalog entry and
  your subscriptions. Changing it in a later version orphans all of them.
- **Nothing parses it.** The trailing segment is a free-form distinguisher, *not* a declaration of
  kind — kind comes from the interface you implement precisely so it cannot be misdeclared. Calling
  a producer `...Producer` is fine; relying on that being true is not.
- **It becomes a file name**, so it is letters, digits, `.`, `_` and `-`, with no empty segments.
  Violations are caught at build time as `SRT1007` and again by the host when it reads the manifest
  off disk.

Convention, not a rule, but follow it anyway: **make the project name, the assembly name, the plugin
folder and the id the same string.** The host already requires `plugins\<Name>\<Name>.dll`, so the
folder and the assembly have to match each other; matching the id too means `config\<id>.json` and
`state\<id>\` need no mapping back to anything on disk.

### What the build produces

Building writes `srtplugin.json` next to your assembly. You never write it by hand — it is generated
from the attribute and the assembly metadata by an MSBuild task that ships inside the `SRTPluginBase`
package, so the host's view of your plugin cannot drift from the plugin's own:

```json
{
  "schemaVersion": 1,
  "id": "SpeedRunTool.Demo.Producer",
  "name": "Demo Producer",
  "description": "A synthetic producer used to smoke-test the SRT Host plugin pipeline.",
  "author": "SpeedRunTool",
  "version": "1.0.0.0",
  "contractGeneration": 5,
  "kind": "Producer",
  "architecture": "Any",
  "requiresUiThread": false,
  "requiresWindowsDesktop": false,
  "entryAssembly": "SpeedRunTool.Demo.Producer.dll",
  "entryType": "SpeedRunTool.Demo.Producer.DemoProducer"
}
```

The manifest is what lets an **AnyCPU host decide which runner to start without loading your
assembly**, which it must never do just to find out.

### Installing it

```
SRTHost.exe
plugins\
  SpeedrunTooling.RE4R.Producer\
    SpeedrunTooling.RE4R.Producer.dll        ← must match the folder name
    SpeedrunTooling.RE4R.Producer.deps.json  ← ship it if you have one
    srtplugin.json
    SRTPluginBase.dll
    ...your private dependencies...
    runtimes\win-x64\native\...              ← native assets keep this layout
```

The DLL name must equal its directory name, or the plugin is invisible. Everything else in the folder
is treated as your private dependencies. Ship your `.deps.json`: the runner's
`AssemblyDependencyResolver` reads it, and it is the only thing that finds assets under `runtimes\`.
Without one, resolution falls back to probing the folder root, which works for plain managed
dependencies and not for anything else.

Do not write into your own plugin folder at run time. An in-app update replaces it wholesale. Use
`context.StateDirectory` (`%LOCALAPPDATA%\SRTHost\state\<id>\`), which survives updates.

---

## 3. Producers

A producer reads something external — usually another process's memory — and publishes payloads on
one channel.

```csharp
public sealed class DemoProducer : ProducerPluginBase<DemoPayload>
{
    private readonly DemoPayload payload = new();

    public override IPluginInfo Info { get; } = PluginInfo.FromAssembly(typeof(DemoProducer).Assembly);

    public override PayloadChannelDescriptor Channel { get; } = new()
    {
        ChannelId = DemoChannel.Id,
        ContractVersion = DemoChannel.ContractVersion,
    };

    public override bool IsSourceAvailable => true;   // a real one: is the game process attached?

    protected override JsonTypeInfo<DemoPayload> PayloadTypeInfo
        => DemoPayloadJsonContext.Default.DemoPayload;

    protected override ValueTask<DemoPayload?> RefreshAsync(CancellationToken cancellationToken)
    {
        // Mutate and return the same instance: the base class serialises before returning, so no
        // reference escapes, and a producer ticking 30 times a second should not allocate per tick.
        payload.Tick++;
        return ValueTask.FromResult<DemoPayload?>(payload);
    }
}
```

Three things the base class does for you, each of which you would otherwise get wrong:

- **`IsSourceAvailable` gates the whole loop.** When it is false the host stops polling entirely and
  tells subscribers the channel has gone idle, so a producer with no game running costs no CPU. (The
  3.x host polled unconditionally.)
- **Unchanged payloads are not published.** The serialised bytes are compared with the previous
  tick's and an identical payload is skipped — a free win on a paused game. Set
  `SuppressUnchangedPayloads` to `false` if your consumers need a heartbeat.
- **Returning `null` skips the tick.** Cheaper than publishing a duplicate, and the expected result
  when the source was not ready.

### Channels and versions

A `PayloadChannelDescriptor` is a channel id, a contract version, and optionally a codec and a JSON
Schema for the host's data inspector.

Channel ids look like `srt/re4r/gamememory` — lowercase, slash-separated, and unique. Put the id and
the version in your contracts assembly as constants so producer and consumer cannot disagree.

**Bump `ContractVersion`'s major when you remove or repurpose a field.** Consumers declare the
minimum they can read, and the host refuses to wire a producer that publishes less — up front, with
a line in the log, rather than letting it fail at deserialisation thirty times a second.

### Codecs

`PayloadCodec.Json` unless you have measured a reason. `Utf8Raw` is opaque text.

`Blittable` — an unmanaged, sequentially-laid-out struct copied as raw memory, with no serialisation
at all — is **carried by the wire but not yet supported by the base classes**, which publish JSON
only. Using it today means implementing `IProducerPlugin.TryProduceAsync` yourself and owning the
layout agreement between the two sides: there is no per-frame layout hash yet, so a consumer built
against a different version of the struct reads misaligned garbage rather than failing. `MessagePack`
is reserved and not implemented.

---

## 4. Payload contracts

**Put your DTOs in their own assembly**, depending on nothing — not on your producer, not on
`SRTPluginBase`:

```
SpeedrunTooling.RE4R.Contracts     ← DTOs, channel constants, JsonSerializerContext
    ↑                    ↑
SpeedrunTooling.RE4R.Producer      JohnDoe.RE4R.Overlay
```

Name it after the **subject, not the producer** — `SpeedrunTooling.RE4R.Contracts`, never
`...RE4R.Producer.Contracts`. A consumer binds to a channel, so naming the DTOs after one producer
would put that producer in every consumer's reference list and force a second producer for the same
game to depend on a package named after a rival.

Use a source-generated `JsonSerializerContext` and share it. Producer and consumer then encode and
decode through the same metadata, and neither pays reflection cost per tick:

```csharp
[JsonSerializable(typeof(DemoPayload))]
public partial class DemoPayloadJsonContext : JsonSerializerContext;
```

### Two JSON traps worth knowing before you hit them

**Property initialisers are dropped for `init` and record properties.** Measured on .NET 10.0.12, not
assumed:

| Shape | Field absent from the JSON | Result |
|---|---|---|
| `class` with `{ get; set; } = default` | omitted | **initialiser kept** |
| `class` or `record` with `{ get; init; } = default` | omitted | **initialiser lost**, you get `default(T)` |
| Reflection-based (`JsonSerializer.Deserialize<T>(json)`) | omitted | initialiser kept in both shapes |

This is the failure a versioned payload or settings type exists to survive: a document written by an
older version omits exactly the fields a newer version added. Keep settings and payload types as
plain classes with `set` accessors and it does not arise; if you want `init` or a record, either
apply `[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]` (which only works when
every property has a `set` accessor, and which makes collections *append* to the default rather than
replace it) or deserialise reflectively.

**Source generation is case-sensitive by default.** `{"tick": 1}` does not populate `Tick` unless you
set a naming policy or `PropertyNameCaseInsensitive` on the context's options. Your producer and
consumer sharing one context is what keeps this from ever mattering; hand-editing a file is when it
bites.

---

## 5. Consumers

```csharp
public sealed class DemoConsumer : ConsumerPluginBase<DemoPayload>
{
    public override IPluginInfo Info { get; } = PluginInfo.FromAssembly(typeof(DemoConsumer).Assembly);

    public override IReadOnlyList<ChannelSubscription> Subscriptions { get; } =
    [
        new ChannelSubscription
        {
            ChannelId = DemoChannel.Id,
            MinimumContractVersion = DemoChannel.ContractVersion,
        },
    ];

    protected override JsonTypeInfo<DemoPayload> PayloadTypeInfo
        => DemoPayloadJsonContext.Default.DemoPayload;

    protected override ValueTask OnPayloadAsync(DemoPayload payload, CancellationToken cancellationToken)
    {
        // …render it, log it, forward it.
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnChannelClosedAsync(string channelId, CancellationToken cancellationToken)
    {
        // The game exited. Clear the display: a HUD showing the last frame of a closed game looks
        // exactly like a working HUD, which is worse than a blank one.
        return ValueTask.CompletedTask;
    }
}
```

- **`ChannelId = "*"`** subscribes to every channel, present and future. That is how a generic
  consumer — a JSON writer, a web bridge, an OBS source — works without referencing a single payload
  type. See `SpeedRunTool.Demo.ConsumerJson`.
- **Return promptly.** A slow consumer does not stall the producer: the host drops stale frames for
  it rather than applying backpressure, and drops are reported in the log. It will simply lag.
- **Sequence numbers skip.** That is dropping working as designed, not an error.
- **`OnChannelClosedAsync` is not optional in practice.** It is the only thing that stops a stale
  overlay, and it fires when the producer stops, crashes, or its source closes.

### The one rule you cannot break

If you implement `IConsumerPlugin` yourself, `PayloadFrame.Payload` points into a **pooled buffer
that is recycled the moment `ConsumeAsync` returns**. Copy or decode it before your first `await`.
Handing that memory to another thread, storing it, or reading it after an await gets you whatever the
next frame put there. `ConsumerPluginBase<T>` deserialises before awaiting anything, which is most of
why it exists.

---

## 6. Base classes — which one to derive from

C# gives a type one base class, so the combinations that matter are all provided:

| Base class | Produces | Consumes | Configurable |
|---|:-:|:-:|:-:|
| `PluginBase` | | | |
| `ProducerPluginBase<TPayload>` | ● | | |
| `ConsumerPluginBase<TPayload>` | | ● | |
| `ConfigurablePluginBase<TConfiguration>` | | | ● |
| `ConfigurableProducerPluginBase<TPayload, TConfiguration>` | ● | | ● |
| `ConfigurableConsumerPluginBase<TPayload, TConfiguration>` | | ● | ● |

`PluginBase` gives you `Context`, a lazily-created `Logger` named after your plugin id, and a
correctly chained `DisposeAsync` — override `DisposeAsyncCore`, not `DisposeAsync`.

**A plugin is a producer or a consumer, never both.** Implementing both interfaces is a build error
(`SRT1004`); split it into two plugins sharing a contracts assembly.

**The base classes are conveniences; the interfaces are the contract.** The host looks only for
`IProducerPlugin` and `IConsumerPlugin`, so when no base class fits, compose it yourself.
`SpeedRunTool.Demo.ConsumerJson` is the worked example: it needs settings but must *not* deserialise,
so it derives from `ConfigurablePluginBase<T>` and implements `IConsumerPlugin` by hand.

---

## 7. UI from inside a plugin

**WinForms and WPF do not work.** The runner targets plain `net10.0`, its `runtimeconfig.json` lists
only `Microsoft.NETCore.App`, and a plugin referencing `System.Windows.Forms` fails to resolve it at
load. Targeting `-windows` yourself does not help — the framework has to be in the *host* process,
and it is not. (`requiresWindowsDesktop` is reserved in the manifest schema and rejected by
discovery, so a plugin that declares it is refused with a legible reason rather than failing
mysteriously.)

**Avalonia works, and it is what the reference example uses.** `SpeedRunTool.Demo.ConsumerWindow`
opens a real top-level window from inside its runner process, binds it to the incoming payload, and
closes it cleanly on stop. Copy `AvaloniaUiThread.cs` from it; the shape is:

```csharp
// On a dedicated STA thread — Avalonia brings its own message loop and must own the thread.
AppBuilder.Configure<PayloadApp>()
    .UseWin32()
    .UseSkia()
    .UseHarfBuzz()          // Skia rasterises glyphs but does not shape text. Forget this and
    .SetupWithoutStarting();// Setup() throws "No text shaping system configured".

Dispatcher.UIThread.MainLoop(shutdownToken);   // blocks until you cancel
```

Then marshal each payload with `Dispatcher.UIThread.Post` — post, do not await, or a slow repaint
becomes a slow consumer.

Three things about that:

- **Do not set `RequiresUiThread` for an Avalonia plugin.** That flag asks the *runner* for a bare
  Win32 `GetMessage` pump, which is what an in-frame overlay wants. The runner wakes its pump with
  `PostThreadMessage`, and thread messages — having no window — are dropped by Avalonia's loop, so
  sharing the thread deadlocks every call the runner marshals onto it. Give Avalonia a thread of its
  own and leave the flag alone.
- **`SetupWithoutStarting`, not `StartWithClassicDesktopLifetime`.** The `StartWith*` helpers own the
  thread and exit the process when the last window closes. The process belongs to the runner.
- **Your plugin brings its own Avalonia.** Nothing in the runner supplies a UI framework, so the
  packages land in your plugin folder — including native assets under `runtimes\win-x64\native\`,
  which is why shipping your `.deps.json` matters.

A user closing your window should mean "hide this", not "stop consuming" — cancel the close and
`Hide()`, as the example does.

---

## 8. Settings

Declare a settings class and the host renders the form. It never loads your assembly, so the runner
reflects over your `JsonTypeInfo` once at load, ships a JSON Schema plus UI hints across the wire,
and the host draws from that.

```csharp
public sealed class OverlayConfiguration
{
    [SrtSetting(Group = "General", Order = 1, HelpText = "Turn the overlay off without unloading it.")]
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; } = true;

    [SrtSetting(Group = "Appearance", Order = 1)]
    [SrtColor]
    [Display(Name = "Text colour")]
    public string Foreground { get; set; } = "#FF20C020";

    [SrtSetting(Group = "Appearance", Order = 2)]
    [Range(6, 72)]
    [Display(Name = "Font size")]
    public int FontSize { get; set; } = 14;
}

[JsonSerializable(typeof(OverlayConfiguration))]
public partial class OverlayConfigurationJsonContext : JsonSerializerContext;
```

Requirements: a class, a public parameterless constructor, `set` accessors (not `init` — see
[the initialiser trap](#two-json-traps-worth-knowing-before-you-hit-them)).

### What the form is built from

Standard `DataAnnotations` first; the `Srt*` attributes only where DataAnnotations has nothing to
say.

| Attribute | Effect |
|---|---|
| `[Display(Name, Description)]` | Label and help text. Without it the property name is humanised |
| `[Range]` | Bounds — and on a number, a bounded range becomes a **slider** rather than a spinner |
| `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]` | Validation, enforced before your plugin is asked to apply |
| `[Required]` | Marks the setting required |
| `[DataType(DataType.MultilineText)]` | A multi-line text box |
| `[SrtSetting(Group, Order, Advanced, HelpText)]` | Grouping and ordering. `Order` ranks *within* a group; `Advanced` hides it behind the advanced toggle |
| `[SrtColor]` | Colour editor (a hex string, or an ARGB integer) |
| `[SrtFont]`, `[SrtHotkey]` | Font picker, shortcut capture |
| `[SrtPath(Directory, Filter)]` | File or folder picker |
| `[SrtDependsOn(property, value, HideWhenUnmet)]` | Disable (or hide) this setting unless another has a given value |
| `[SrtHidden]` | Keep it out of the form entirely |

Types map to editors on their own: `bool` → toggle, `enum` → drop-down, `[Flags]` enum → checkboxes,
`TimeSpan` → duration, `List<T>` → list editor, a nested class → a sub-group.

**Anything the schema cannot express degrades to a per-property raw JSON box, not to a broken form.**
A `Dictionary<string, string>` is perfectly serialisable and completely undrawable; the rest of the
form keeps working around it. `SpeedRunTool.Demo.Consumer` carries one on purpose to prove it.

### How settings reach you, and who owns the file

```
user edits the form → host sends the document → runner deserialises with YOUR JsonTypeInfo
    → ApplyConfigurationAsync → OnConfigurationChangedAsync(TConfiguration)
    → returns without throwing → THEN the host writes %LOCALAPPDATA%\SRTHost\config\<id>.json
```

- **Throw to reject.** The host surfaces your message against the form and does not write the
  document, so settings that would stop your plugin loading next time cannot be saved.
- **The host owns the file. Your plugin only reads it.** Do not write it yourself; two writers
  racing on one document means the user's settings are whichever process lost.
- **React in `OnConfigurationChangedAsync`**, do not poll `Configuration` per payload. Settings
  change a handful of times per session; payloads arrive thirty times a second.
- **Your serialiser is the only one.** The form's keys are by construction the keys you read —
  naming policy, `[JsonPropertyName]`, converters and enum representation included. Whether your
  enums are written as names or numbers is your choice and the host follows it.

---

## 9. Lifecycle and threading

| Call | When | Notes |
|---|---|---|
| `InitializeAsync(context, ct)` | Once, at load | Capture `context`. Throw to fail the load. Settings are already applied when this returns |
| `StartAsync(ct)` | On start, and after every restart | Attach to the game, open your window. Throw to fault |
| `TryProduceAsync` / `ConsumeAsync` | Per tick / per payload | Called on the runner's dispatch loop. Return promptly |
| `StopAsync(ct)` | On stop, and before unload | Release the external source here, not in dispose. `StartAsync` may follow |
| `DisposeAsyncCore()` | Once, at unload | Override this, never `DisposeAsync` |

Report failure by **throwing**. Generations 1–4 returned `int` status codes that callers ignored, so
a plugin could fail silently and appear to be running.

`context` gives you `Services` (resolve `ILogger<T>`; `PluginBase.Logger` already does),
`ProcessArchitecture` (what the runner actually is), `PluginDirectory` (read-only),
`StateDirectory` (writable, survives updates) and `Stopping`.

---

## 10. When it does not work

### Build errors

| Code | Meaning |
|---|---|
| `SRT1001` | No `[assembly: SrtPluginAssembly]`. Add it, or set `<SrtGenerateManifest>false</SrtGenerateManifest>` if this project is a contracts or helper library |
| `SRT1002` | The attribute could not be read. It is `[assembly: SrtPluginAssembly("Author.Subject.Name", typeof(YourPlugin))]` |
| `SRT1003` | The id is empty |
| `SRT1004` | The entry type implements both `IProducerPlugin` and `IConsumerPlugin` |
| `SRT1005` | The entry type implements neither |
| `SRT1006` | The declared contract generation is not the one being built against — remove the `Generation` override |
| `SRT1007` | The id is not usable as a file name |

### The host does not list your plugin

Every rejection is reported on the host's problems list rather than silently skipped. The reasons, in
the order they are checked: no `srtplugin.json`; unreadable manifest; wrong schema version; an unsafe
id; an id already claimed by another folder; a contract generation this host does not load;
`requiresWindowsDesktop`; no entry assembly or type; an entry assembly containing a path separator;
an entry assembly that is not in the folder.

The most common cause of the first one is the DLL name not matching the folder name.

### It loads, then faults

| Symptom | Cause |
|---|---|
| `FileLoadException` / `BadImageFormatException` at load | Architecture mismatch — a 32-bit plugin in the 64-bit runner or the reverse. Set `Architecture` on the attribute |
| `FileNotFoundException` naming your base class | `CopyLocalLockFileAssemblies` is not set, so `SRTPluginBase.dll` is not in the folder |
| `DllNotFoundException` for a native library | The `runtimes\` tree or the `.deps.json` did not ship |
| `InvalidCastException` naming the same type twice | Two copies of a contract assembly. `SRTPluginBase.Abstractions`, `Microsoft.Extensions.Logging.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions` always load from the runner so their types stay reference-identical; anything else you ship is yours alone |
| Wired but nothing arrives | The version check refused the edge — look for the router's warning naming both versions |

---

## 11. Porting a generation-1 plugin

### Producer

| Generation 1 | Generation 5 |
|---|---|
| `ProjectReference` into SRTHost's vendored `SRTPluginBase` | `PackageReference SRTPluginBase 5.0.0` |
| `net5.0` | `net10.0` |
| `int Startup(IPluginHostDelegates)` | `InitializeAsync` + `StartAsync`, throwing on failure |
| `int Shutdown()` | `StopAsync` + `DisposeAsyncCore` |
| `object PullData()` | `ValueTask<TPayload?> RefreshAsync(ct)` |
| `bool GameRunning` | `bool IsSourceAvailable` |
| `hostDelegates.OutputMessage(…)` | `Logger.LogInformation(…)` |
| Four `int` version parts | The assembly version, plus a new stable `Id` |
| DTOs beside the producer | A `<Author>.<Subject>.Contracts` assembly with a `JsonSerializerContext` and a channel constant |
| Identity from `GetType().Name` | `[assembly: SrtPluginAssembly(id, type)]` |

### Consumer

| Generation 1 | Generation 5 |
|---|---|
| `string RequiredProvider => "SRTPluginProviderRE4R"` | `Subscriptions => [ new() { ChannelId = RE4RChannel.Id } ]` |
| `RequiredProvider => ""` (agnostic) | Subscribe to `"*"` and consume the bytes |
| `int ReceiveData(object)` plus a cast | `OnPayloadAsync(GameMemoryRE4R, ct)` |
| `ProjectReference` on the producer | A reference on the **contracts** assembly, never the producer |
| Nothing | `OnChannelClosedAsync` — clear the HUD when the game exits |
| A WinForms window | An Avalonia window inside the plugin (§7) |

`PluginBase<SRTPluginProducerRE4R>` — a consumer with a compile-time dependency on a concrete
producer class — is the coupling this generation exists to remove. It could not survive a process
boundary, and it should not have survived in-process either.

Two more things that bite during a port:

- **`GetType().Name` was not unique.** The shipped RE8 overlay is a copy of the RE2 one and still
  declares both its namespace and its class as `SRTPluginUIRE2DirectXOverlay`, so the two collided
  whenever both were loaded. Rename it while porting, and give it a real id.
- **Your window is a rewrite, not a port.** It is also the cheapest part: these were label grids over
  producer data, and `SpeedRunTool.Demo.ConsumerWindow` is that same grid — one `Grid`, two columns,
  a row per field, bound to a view model instead of assigned to in an event handler.

---

## 12. Building the examples

```powershell
# Both platforms of the runner get built by a release; one is enough while developing.
dotnet build src\SRTHost.slnx -c Debug -p:Platform=x64

# The demo plugins deploy themselves into the host's plugins\ folder as part of that build.
cd src\SRTHost\bin\Debug\AnyCPU\net10.0
.\SRTHost.exe                                          # the shell
.\SRTHost.exe --headless --runner-log-level Warning     # router only, for OBS and CI
```

`--runner-log-level` is separate from `--log-level` on purpose: the demo consumer logs every payload,
which at 30 Hz drowns everything the host says about wiring, restarts and latency.
