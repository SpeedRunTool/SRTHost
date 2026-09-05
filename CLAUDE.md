# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## The single most important fact: two generations live in this repo

`main` carries the 3.x host and **is where the current work is happening** — it was dormant from 2021
until 2026-09, when it picked up a WiX installer, a framework-dependent publish and a live signed
release pipeline. `Issue/35` (based on `develop`, 218 commits ahead of `main`, last touched
2026-04-30) is the 4.x rewrite, and is **expected to be redone or revisited** in light of the current
`main` work rather than treated as settled. When the two disagree, `main` is the newer guidance.

Check `git branch --show-current` before doing anything — the two generations share almost no file
paths, framework, or API, and `main` contains commits `Issue/35` does not (and vice versa).

| | `main` (checked out by default) | `Issue/35` / `develop` (active) |
|---|---|---|
| Layout | everything under `src/` | everything under `src/` |
| Solution | `src/SRTHost.slnx` | `src/SRTHost.slnx` |
| TFM | `net5.0-windows`, WinForms enabled | `net10.0`, `Microsoft.NET.Sdk.Web` |
| Version | `version.txt` (`3.1.0`) + CI build number | `version.txt` (`3.5.0`) + CI build number |
| Contracts | **vendored** `src/SRTPluginBase/` in this repo (netstandard2.1) | sibling repo / NuGet `SRTPluginBase 5.0.0-*` |
| Roles | `IPluginProvider` / `IPluginUI` | `IPluginProducer` / `IPluginConsumer` |
| Shape | static `Program` with a manual polling loop | generic host + DI + `IHostedService` + Kestrel |
| Deployment | WiX installer (`src/SRTHostSetup/`), framework-dependent | Inno Setup (`src/SRTHostSetup/`), framework-dependent |
| Shared props | `src/Directory.Build.props` + `src/Directory.Build.targets` (both also apply to the wixprojs) | `src/Directory.Build.props` |

The vendored `SRTPluginBase/` on `main` is *not* the same code as `S:\SpeedRunTool\SRTPluginBase`.
Never sync one to the other.

## Build

Both generations are Windows-only and **platform-specific — AnyCPU is wrong**. `$(Platform)` is
appended to `DefineConstants`, and `#if x64` selects the assembly name (`SRTHost64` vs `SRTHost32`),
the log file name, and the exe path the host reads its own `FileVersionInfo` from. Building a single
platform is fine during development; releases ship both.

```powershell
# main — the .NET 10 SDK builds this fine (NETSDK1138 warns the TFM is out of support).
# .NET 5 SDK 5.0.408 is also installed locally.
dotnet build src/SRTHost.slnx -c Release -p:Platform=x64  # -> src\SRTHost\bin\Release\x64\net5.0-windows\win-x64\SRTHost64.exe

# Publish is FRAMEWORK-DEPENDENT as of 2026-09 (~550 KB each, was ~150 MB self-contained).
# PublishProfile alone does NOT set Configuration/Platform — pass them explicitly or you get a
# Debug/AnyCPU build named SRTHost.dll instead of SRTHost64.exe.
dotnet publish src/SRTHost.slnx -c Release -p:Platform=x64 -p:PublishProfile=x64
# -> src\SRTHost\bin\Release\x64\net5.0-windows\publish\  (and ...\x86\... for x86)
# PublishDir carries $(Platform), so the two platforms do NOT share a folder; the installer is
# handed both paths. It lives in Directory.Build.targets, not .props: .props is imported before
# the project body sets $(TargetFramework), so the segment would silently expand to nothing.
# OutputPath does not have that problem - the SDK appends the TFM and RID to it by itself.

# Issue/35 — .NET 10
dotnet build src/SRTHost.slnx -c Debug -p:Platform=x64
dotnet publish src/SRTHost.slnx -c Release -p:Platform=x64 -p:PublishProfile=win-x64
```

**`Issue/35` Debug builds require the sibling repo `S:\SpeedRunTool\SRTPluginBase` to be cloned and
on a matching branch.** `src/SRTHost/SRTHost.csproj` has a `<Choose>`: Debug takes a
`ProjectReference` to `..\..\..\SRTPluginBase\src\SRTPluginBase\SRTPluginBase.csproj`, Release takes
the `SRTPluginBase 5.0.0-*` prerelease NuGet package. A Debug build therefore compiles against
whatever is checked out next door while a Release build compiles against a published package — they
can diverge. CI reproduces this by checking out `SpeedRunTool/SRTPluginBase@develop` and moving it
one directory up.

There are **no test projects** in either generation, though the CI workflow runs `dotnet test`.

*Local* code signing is gated on the `TJGutjahr` environment variable on `main` (post-build/post-publish
`signtool.exe` against a hardcoded thumbprint). Do not enable it to "fix" a build. `Issue/35` dropped
local signing entirely. **Both generations sign in CI via Azure Trusted Signing**, account
`AS-TJGutjahr` / profile `AS-CP-TJGutjahr`.

## Runtime layout and plugin discovery (both generations)

The host looks for `plugins\<Name>\<Name>.dll` — **the DLL filename must equal its directory name**,
or the plugin is invisible. Everything else in that folder is treated as the plugin's private
dependencies. Plugin repos (`SpeedrunTooling/SRTPlugin*`) build straight into
`src\SRTHost\bin\<Config>\...\plugins` via relative paths that resolve through `S:\`, so building a
plugin deploys it here.

## Assembly load contexts — the part that has caused the most bugs

`PluginLoadContext` (one per plugin folder, named after the folder) exists in both generations with
nearly identical resolution logic. The rules, in order, and why they exist:

1. If the requested assembly is `SRTPluginBase`, load it from `Default`. Contract types must be
   reference-identical across every plugin or casts fail with `InvalidCastException`.
2. If the requested assembly is a **producer/provider** and *this* context is not itself a
   producer/provider folder, refuse the local copy and delegate to the ALC already named for that
   assembly (`AssemblyLoadContext.All`). This is issue #26: a UI/consumer plugin that ships a copy of
   the provider DLL in its own folder would otherwise load a second instance of the provider types.
3. Otherwise `AssemblyDependencyResolver` → highest-`FileVersionInfo` match anywhere under the plugin
   folder → any existing ALC with a matching name → `Default`.

A `FileLoadException` during load means an architecture mismatch (x86 host with an x64 plugin or vice
versa) and is reported as such. `Issue/35` creates the ALC as **collectible** (`isCollectible: true`)
to support unload/reload; `main` does not.

## `main` architecture

`Program.Main` owns everything: it redirects `Console.Out`/`Error` into `LogTextWriter` (tees console
+ `SRTHost64.log`), parses `--Key=Value` args via `CommandLineProcessor` (`--UpdateRate`,
`--Provider`, `--Help`), loads every plugin, then runs a hand-rolled loop until Ctrl+C:

- Providers are paired with UIs by `IPluginUI.RequiredProvider == providerType.Name`; UIs with an
  empty `RequiredProvider` are "agnostic" (e.g. a JSON writer) and receive data from every provider.
- Each tick: for a started provider whose `GameRunning` is true, `PullData()` once and fan the result
  out to agnostic + dependent UIs via `ReceiveData(object)`. If the game stopped, only the dependent
  UIs are shut down.
- Plugins are started lazily and their state tracked in `PluginStateValue<T>.Startup`. All plugin
  calls return `int` where `0` means success, and exceptions are swallowed into the log.
- `--UpdateRate` is clamped to 16–2000ms; the field default is 33ms but the out-of-range reset and
  the help text both say 66ms.

## `Issue/35` architecture

`Program.Main` builds a `WebApplication` and registers two hosted services: `PluginHost` (also
registered as the `IPluginHost` singleton, so DI hands out the same instance by interface or by
implementation) and `WebServer` (currently a stub — Kestrel endpoints are configured but nothing is
mapped yet; `develop` carries MudBlazor work).

- `PluginHost.StartAsync` loads each plugin into its own ALC, then instantiates it. Constructor
  arguments are resolved **from the DI container** by parameter type, falling back to
  `Activator.CreateInstance`, so plugins can take `ILogger<T>`, `IPluginHost`, etc.
- Plugin lifecycle is a state machine: `PluginStatusEnum` (`NotLoaded`/`Loaded`/`Instantiated`/
  `LoadingError`/`InstantiationError`) plus `PluginSubStatusEnum` for the cause
  (`IncorrectArchitecture`, `PluginInitializationException`, `PluginNotFoundException`, …).
  `UnloadPluginAsync`/`ReloadPluginAsync` unload the collectible ALC and re-run load + initialize.
- Configuration is SQLite via `ConfigurationDB<T>` from SRTPluginBase (`HostConfiguration` model
  round-tripped through `ConfigDictionaryToModel`/`ModelToConfigDictionary`), not the JSON `.cfg`
  files `main` used.
- Logging: `Microsoft.Extensions.Logging` with a custom `FileLogger` provider under
  `LoggerImplementations/`. **All log messages are source-generated `[LoggerMessage]` partial methods**
  in the sibling `X - Logging.cs` files (`PluginHost - Logging.cs`, `WebServer - Logging.cs`), with
  IDs allocated from the ranges in `EventIds.cs` (PluginSystem 1000, WebServer 2000,
  APIControllerPlugin 2100, APIControllerDebug 2200). Add new messages there rather than calling
  `logger.Log…` inline.
- `appsettings.json` defines the Kestrel endpoints (`http://*:7190`, `https://*:7191`); `PluginHost`
  reads them back with `GetRequiredSection("Kestrel:Endpoints:…:Url")` to print a reachable URL, so
  removing either endpoint from config throws at startup.
- Worth knowing when debugging "plugin not found": the plugins directory is *created* under
  `AppContext.BaseDirectory` but *enumerated* from `Directory.GetCurrentDirectory()`.
- `src/SRTHostSetup/SRTHostSetup.iss` is an Inno Setup installer packaging both architectures from
  `..\SRTHost\bin\Release\net10.0\publish` (or CI's `artifact\` directory).

## The installer on `main` (`src/SRTHostSetup/`)

Added 2026-09. WiX **5.0.2** — pinned there because WiX 6+ refuses to build without accepting the
Open Source Maintenance Fee EULA (`error WIX7015`). The fee does not apply to an MIT project, but
the gate does. The authoring uses the v4 schema that 6/7 still accept, so a bump is cheap later.

Two projects: `SRTHostPackage` builds a **per-user** MSI into `%LOCALAPPDATA%\SRTHost`;
`SRTHostBundle` builds the shippable Burn bundle `SRTHostSetup-v<version>.exe`, which chains the
.NET 5 prerequisites plus that MSI. Read `src/SRTHostSetup/README.md` before touching either — it
records the constraints that are easy to violate. The load-bearing ones:

- **Publish both platforms first.** Both land in one directory - `PublishDir` (in
  `src/Directory.Build.targets`) has no `$(Platform)` segment, unlike `OutputPath` - and
  `SRTHostPublishDir` points the MSI at it. CI merges its two matrix artifacts into the same
  shape.
- **Symbols ship** flat, next to the executables, in both the installer and the portable zip.
  `SRTPluginBase.pdb` is taken from the x64 publish only: both platforms build one and they
  are byte-identical, which is also why the CI matrix artifacts can be merged flat.
- **`SRTPluginBase.dll` is published loose**, outside the single-file bundle, so plugin authors
  can reference the assembly the host actually loads. It **must** stay in
  `PublishReadyToRunExclude`: R2R would stamp it per architecture, and since both hosts share
  one directory and one copy, the other host would then fail with `BadImageFormatException`.
- **Two version properties.** Burn compares four version fields, Windows Installer only three, so
  `SRTHostProductVersion` (4-field) and `SRTHostMsiVersion` (3-field) are separate, and the MSI
  leans on `MajorUpgrade/@AllowSameVersionUpgrades` for revision-only releases.
- **Never change** the `UpgradeCode`s or the explicit component GUIDs in `Package.wxs`.
- The wixprojs are deliberately **not** in `src/SRTHost.slnx` — they need publish output a
  solution build does not produce.
- They *do* inherit `src/Directory.Build.props`. C#-only settings there must be excluded for
  `.wixproj`, or they break the build: `DebugType=portable` becomes an invalid `-pdbType`
  (WIX0268), and SourceLink has nothing to do in an MSI.

`main` now declares `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. ASP.NET Core is a
platform guarantee for plugins, not something the host itself uses; without the reference the
shared framework is absent from `runtimeconfig.json` and a plugin touching `Microsoft.AspNetCore.*`
fails assembly resolution even when the runtime is installed. Do not "simplify" it away.

**.NET 5 is out of support**, so the framework-dependent install is not serviced by Windows Update.
Known interim state until the planned move to a current .NET; the pinned 5.0.17 URLs, SHA-512
hashes and detection paths in `src/SRTHostSetup/SRTHostBundle/Prerequisites.wxs` all need retargeting then.

## Releases

`main`'s `AutomatedRelease.yml` is live as of 2026-09 and replaces the old dead-code workflow that
triggered on `master`. It mirrors the `Issue/35` shape: reads `version.txt`, appends the
`BUILD_NUMBER` repo variable and short SHA into a SemVer string, builds the x86/x64 matrix, signs
via Azure Trusted Signing, and publishes the installer plus a zip. Bump versions in `version.txt`,
not the csproj — csproj values are overridden by `-p:Version/FileVersion/AssemblyVersion` from CI.

Signing a Burn bundle has two traps, both handled in the workflow and explained in
`src/SRTHostSetup/README.md`: sign in containment order (exes, then MSI, then bundle) with
`-p:BuildProjectReferences=false` so the bundle build does not rebuild and unsign the MSI; and
detach/sign/reattach the Burn engine (`wix burn detach` / `reattach`) rather than running signtool
over the finished bundle.

A push to `main` **always** releases and moves the rolling tags (`latest`, `vN`, `vN.M`, `vN.M.P`,
`vN.M.P+B`); it ignores `createTagsAndRelease`, which is only consulted for `workflow_dispatch`. A
dispatch against `main` does the same when that box is checked. A dispatch against any other branch
releases only when checked, and gets **just** its own `vN.M.P-<preReleaseTag>+B` tag — the rolling
tags are force-moved and are therefore restricted to the main line. A pre-release tag is mandatory
off `main`; `setup` fails fast if it is empty, before anything is built or signed.

**Every release is published as a pre-release**; unchecking that is a manual step after review.

There is deliberately **no `pull_request` trigger** — on a PR `actions/checkout` resolves an
ephemeral merge ref that is not on any branch, so tagging it would point the rolling tags outside
`main`'s history. `ManualReleaseDevelop.yml` was removed on `main`; `workflow_dispatch` covers it.

`Issue/35` has its own `AutomatedRelease.yml` of the same shape, zipping as `SRTHost_<SemVer>.zip`
and reading versions through `-p:VERSION/FILEVERSION/ASSEMBLYVERSION` (uppercase, mapped by its
`Directory.Build.props`) rather than the standard MSBuild property names `main` uses. Its tag step
is **not** branch-guarded, and it still keeps a `ManualReleaseDevelop.yml` pinned to `develop`.
Both are candidates to be brought in line with `main` whenever `Issue/35` is revisited.
