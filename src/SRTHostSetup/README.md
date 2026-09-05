# SRTHostSetup

A WiX installer for SRT Host.

| | |
|---|---|
| Install location | `%LOCALAPPDATA%\SRTHost` |
| Elevation | **None**, unless a .NET runtime is missing (see below) |
| Upgrades | Yes — installing a newer build replaces the older one in place |
| Uninstall | Apps &amp; features, or the cached `SRTHostSetup-v<version>.exe /uninstall` |

## Layout

| Project | Output | What it is |
|---|---|---|
| `SRTHostPackage` | `SRTHost.msi` | Per-user MSI: the two host executables, LICENSE, shortcuts, `plugins\`. Installs standalone with no elevation, but performs **no** .NET runtime check. |
| `SRTHostBundle` | `SRTHostSetup-v<version>.exe` | Burn bundle: the .NET prerequisites plus the MSI. **This is the shippable artifact.** |

`Version.props` holds the versions and the path to the publish output for both projects.

## Building

The bundle embeds the MSI, and the MSI embeds the published files, so **publish both platforms
first**. Both publish into the same directory — `PublishDir` in `src/Directory.Build.targets` has
no `$(Platform)` segment — which is the layout the installer and the release zip both want:

```powershell
dotnet publish src\SRTHost.slnx -c Release -p:Platform=x64 -p:PublishProfile=x64
dotnet publish src\SRTHost.slnx -c Release -p:Platform=x86 -p:PublishProfile=x86
dotnet build src\SRTHostSetup\SRTHostBundle\SRTHostBundle.wixproj -c Release
```

The result lands in `src\SRTHostSetup\SRTHostBundle\bin\Release\`.

`SRTHostPublishDir` locates that directory and defaults to the path above. CI overrides it with the
folder the two matrix artifacts are merged into, which has the same shape.

These projects sit under `src/`, so they inherit `src/Directory.Build.props` along with the C#
projects. Anything C#-specific added there must be excluded for `.wixproj` or it breaks the build —
`DebugType` is passed to WiX as `-pdbType`, which accepts only `full` or `none`, so a `portable`
value is a hard error (WIX0268), and SourceLink has nothing to embed in an MSI or a bundle.

The wixproj files are deliberately **not** in `src\SRTHost.slnx`. They depend on publish output that a
plain solution build does not produce, so including them would break `dotnet build src\SRTHost.slnx`.

### Versioning

Two versions, because Windows Installer and Burn do not agree on how many fields matter:

* `SRTHostProductVersion` (default `1.0.0.0`) — four fields, used for the bundle and the output
  filename. Burn compares all four, so this is what drives upgrade detection in practice.
* `SRTHostMsiVersion` (default `1.0.0`) — three fields, used for the MSI `ProductVersion`.
  Windows Installer **ignores the fourth field entirely**, so `1.0.0.0` and `1.0.0.1` are the same
  version to it. `MajorUpgrade/@AllowSameVersionUpgrades` covers that case by removing and
  reinstalling rather than skipping.

### Payload

The host is published as a single file, but `SRTPluginBase.dll` is deliberately left **outside**
the bundle (`ExcludeFromSingleFile` on the ProjectReference), so plugin authors can reference the
exact assembly the host will load. Everything else — `System.Text.Json` and friends — stays
embedded in the executable.

That forces a second, non-obvious requirement: `SRTHost.csproj` also lists

```xml
<PublishReadyToRunExclude Include="SRTPluginBase.dll" />
```

ReadyToRun rewrites each published assembly into an **architecture-specific** R2R image
(`Machine=Amd64`, or `I386` with `Requires32Bit`). `SRTHost32.exe` and `SRTHost64.exe` share one
install directory and therefore one copy of this DLL, so an R2R build of it would be valid for
exactly one of them — the other dies at startup with `BadImageFormatException`. Excluding it leaves
plain AnyCPU IL (`Machine=I386`, `ILOnly`), byte-identical from both publishes. The host
executables themselves are still ReadyToRun.

### Symbols

`DebugType` is `portable` rather than `embedded`, so every assembly has a `.pdb` beside it instead
of symbols inside the binary. They ship, so a stack trace out of a user's log carries file and line
numbers. The installer and the portable zip lay them down in the same flat tree:

```
SRTHost64.exe   SRTHost64.pdb
SRTHost32.exe   SRTHost32.pdb
SRTPluginBase.pdb
```

`SRTPluginBase.pdb` is taken from the **x64** publish only. Both platforms produce one and the two
are byte-identical — `SRTPluginBase` has no platform-conditional code and the build is
deterministic — so one copy is enough, and the two matrix artifacts can be merged flat in CI
without caring which wins. Adding something like a `#if x64` to `SRTPluginBase` would break that
assumption silently, shipping x64 symbols for both.

## Command line

```powershell
SRTHostSetup-v1.0.0.0.exe                          # interactive
SRTHostSetup-v1.0.0.0.exe /install /quiet /norestart
SRTHostSetup-v1.0.0.0.exe /uninstall /quiet
SRTHostSetup-v1.0.0.0.exe DesktopShortcut=1        # also create desktop shortcuts
SRTHostSetup-v1.0.0.0.exe /layout <dir>            # download payloads without installing
```

Start Menu shortcuts are always created. Desktop shortcuts are off by default, matching the
unchecked "Create a desktop icon" task in the old Inno script.

## The .NET prerequisites

The host is published **framework-dependent**, so the setup carries no runtime of its own — this
is why the executables are ~550 KB each instead of ~150 MB. The bundle chains four packages:

| Package | Needed by |
|---|---|
| .NET Desktop Runtime 5.0.17 x86 | `SRTHost32.exe` |
| ASP.NET Core Runtime 5.0.17 x86 | `SRTHost32.exe` |
| .NET Desktop Runtime 5.0.17 x64 | `SRTHost64.exe` (64-bit Windows only) |
| ASP.NET Core Runtime 5.0.17 x64 | `SRTHost64.exe` (64-bit Windows only) |

A 64-bit machine needs both architectures because the 32-bit host exists to read 32-bit game
memory and runs on the x86 frameworks.

ASP.NET Core is required even though the host has no ASP.NET code of its own. It is a platform
guarantee for plugins, declared by `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in
`src\SRTHost\SRTHost.csproj`; without it a plugin referencing `Microsoft.AspNetCore.*` fails assembly
resolution at runtime.

Payloads are **not** embedded. Each is downloaded on demand from `builds.dotnet.microsoft.com` and
verified against the SHA-512 published in
[`releases.json`](https://builds.dotnet.microsoft.com/dotnet/release-metadata/5.0/releases.json)
before it runs, which keeps the setup executable at ~1.4 MB.

### Elevation

The bundle and the MSI are both per-user, so a machine that already has the required .NET installs,
upgrades and uninstalls SRT Host with **no UAC prompt at all**. The runtime installers are inherently
per-machine and carry their own `requireAdministrator` manifest, so they raise a single UAC prompt
— but only when a framework is genuinely missing.

### Detection

Detection is an exact-version probe for the framework directory under the default .NET install
location, e.g. `%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\5.0.17`. An exact version
is correct rather than fragile here: **5.0.17 is the final .NET 5 release** (2022-05-10) and no
further one is expected to exist.

A machine with .NET installed somewhere non-default, or reached only through `DOTNET_ROOT`, will
fail detection and re-run the runtime installer. That is wasteful but harmless — the runtime
installers are idempotent and exit quickly when already satisfied.

> **.NET 5 is out of support.** It reached end of life on 2022-05-10 and receives no security
> updates, so a framework-dependent install does *not* get serviced by Windows Update the way a
> supported .NET would. This is a known interim state until the planned migration to a current
> .NET; when that lands, the pinned URLs, hashes and `5.0.17` detection paths in
> `SRTHostBundle\Prerequisites.wxs` all need retargeting.

## Things that must not change casually

* The `UpgradeCode` on `Package` (`E840F490-…`) and on `Bundle` (`1E925787-…`). Changing either
  makes every future installer fail to see existing installs, leaving two copies side by side.
* The explicit component `Guid`s in `Package.wxs`. Those three components mix a registry KeyPath
  with a file, which rules out WiX's automatic GUIDs; changing them breaks upgrade and uninstall
  reference counting.

## Code signing

`.github/workflows/AutomatedRelease.yml` signs everything with Azure Trusted Signing, using the
same account and certificate profile as other SpeedRunTool repos.

Two things about this are easy to get wrong:

**Order matters.** Each file must be signed *before* whatever embeds it is built, or the outer
container captures an unsigned copy:

```
sign SRTHost32.exe, SRTHost64.exe
  -> build SRTHost.msi        (embeds the signed executables)
  -> sign SRTHost.msi
    -> build the bundle       (embeds the signed MSI)
```

The bundle build passes `-p:BuildProjectReferences=false`. Without it, the bundle rebuilds the MSI
from the wixproj ProjectReference and silently throws away the signature applied a step earlier.

**A Burn bundle cannot be signed by running signtool over the output.** The payload containers are
attached to the engine, so signing the whole file directly produces a bundle that fails its own
integrity check. The engine has to be detached, signed, and reattached first — the WiX v4+
replacement for v3's `insignia -ib` / `-ab`:

```powershell
wix burn detach   SRTHostSetup-v<version>.exe -engine engine.exe
# sign engine.exe
wix burn reattach SRTHostSetup-v<version>.exe -engine engine.exe -o final.exe
# sign final.exe
```

This needs the WiX CLI (`dotnet tool install --global wix --version 5.0.2`), which is separate
from the `WixToolset.Sdk` PackageReference used to build.

## WiX version

Pinned to **5.0.2**, the last release under plain MIT. WiX 6 and later require accepting the Open
Source Maintenance Fee EULA before the toolset will build (`error WIX7015`). The fee does not
apply to an MIT project like this one, but the acceptance gate does. Revisit later if wanted; the
authoring here uses the v4 schema that 6 and 7 still accept.

## Releasing

`AutomatedRelease.yml` is the only release workflow. There is no separate manual/beta workflow.

| Trigger | Release | Tags |
|---|---|---|
| Push to `main` | Always | `latest`, `vN`, `vN.M`, `vN.M.P`, `vN.M.P+B` |
| Dispatch against `main`, **Create Tags and Release** checked | Yes | `latest`, `vN`, `vN.M`, `vN.M.P`, `vN.M.P+B` |
| Dispatch against `main`, unchecked | No | none |
| Dispatch against any other branch, **checked** | Yes | `vN.M.P-<preReleaseTag>+B` only |
| Dispatch against any other branch, unchecked | No | none |

Every run builds, signs and uploads the installer as a workflow artifact; the table is only about
what gets *published*. **Every release is marked pre-release** — unchecking that is a manual step
once the build has been reviewed.

The rolling tags are force-moved, which is why they are restricted to the main line. A dispatch off
a side branch never touches them; its only tag is the `vN.M.P-<preReleaseTag>+B` that the release
step creates. That also makes a **pre-release tag mandatory off `main`**: `setup` fails fast if
`versionPreReleaseTag` is empty there, before anything is built or signed, since an empty one would
publish a stable-looking `vN.M.P+B` from a side branch.

`github.event_name == 'push'` is used as shorthand for "on `main`" in these conditions, which holds
because the push trigger is restricted to that branch. `createTagsAndRelease` is only consulted for
`workflow_dispatch`; a push to `main` ignores it.

There is deliberately **no `pull_request` trigger**. On a pull request `actions/checkout` resolves
an ephemeral merge ref that is not on any branch, so tagging it would point the rolling tags at a
commit outside `main`'s history.
