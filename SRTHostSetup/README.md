# SRTHostSetup

A WiX installer for SRT Host, replacing the extract-and-run zip that shipped as
[3.1.0.1](https://github.com/SpeedRunTool/SRTHost/releases/download/3.1.0.1/SRTHost-v3.1.0.1.zip).

| | |
|---|---|
| Install location | `%LOCALAPPDATA%\SRTHost` |
| Elevation | **None**, unless a .NET 5 runtime is missing (see below) |
| Upgrades | Yes — installing a newer build replaces the older one in place |
| Uninstall | Apps &amp; features, or the cached `SRTHostSetup-v<version>.exe /uninstall` |

## Layout

| Project | Output | What it is |
|---|---|---|
| `SRTHostPackage` | `SRTHost.msi` | Per-user MSI: the two host executables, LICENSE, shortcuts, `plugins\`. Installs standalone with no elevation, but performs **no** .NET runtime check. |
| `SRTHostBundle` | `SRTHostSetup-v<version>.exe` | Burn bundle: the .NET 5 prerequisites plus the MSI. **This is the shippable artifact.** |

`Version.props` holds the versions and the path to the publish output for both projects.

## Building

The bundle embeds the MSI, and the MSI embeds the published executables, so **publish both
platforms first** — into the single shared publish directory, which is what the release zip did
too:

```powershell
dotnet publish SRTHost\SRTHost.csproj -c Release -p:Platform=x64 -p:PublishProfile=x64
dotnet publish SRTHost\SRTHost.csproj -c Release -p:Platform=x86 -p:PublishProfile=x86
dotnet build SRTHostSetup\SRTHostBundle\SRTHostBundle.wixproj -c Release
```

The result lands in `SRTHostSetup\SRTHostBundle\bin\Release\`.

The wixproj files are deliberately **not** in `SRTHost.sln`. They depend on publish output that a
plain solution build does not produce, so including them would break `dotnet build SRTHost.sln`.

### Versioning

Two versions, because Windows Installer and Burn do not agree on how many fields matter:

* `SRTHostProductVersion` (default `3.1.0.1`) — four fields, used for the bundle and the output
  filename. Burn compares all four, so this is what drives upgrade detection in practice.
* `SRTHostMsiVersion` (default `3.1.0`) — three fields, used for the MSI `ProductVersion`.
  Windows Installer **ignores the fourth field entirely**, so `3.1.0.1` and `3.1.0.2` are the same
  version to it. `MajorUpgrade/@AllowSameVersionUpgrades` covers that case by removing and
  reinstalling rather than skipping.

CI overrides both:

```powershell
dotnet build SRTHostSetup\SRTHostBundle\SRTHostBundle.wixproj -c Release -t:Rebuild `
  -p:SRTHostProductVersion=3.1.0.2 -p:SRTHostMsiVersion=3.1.0
```

Use `-t:Rebuild` when only the version changes. The output filename depends on the version but the
incremental up-to-date check does not, so a plain `build` will skip relinking and then fail to find
the renamed file.

## Command line

```powershell
SRTHostSetup-v3.1.0.1.exe                          # interactive
SRTHostSetup-v3.1.0.1.exe /install /quiet /norestart
SRTHostSetup-v3.1.0.1.exe /uninstall /quiet
SRTHostSetup-v3.1.0.1.exe DesktopShortcut=1        # also create desktop shortcuts
SRTHostSetup-v3.1.0.1.exe /layout <dir>            # download payloads without installing
```

Start Menu shortcuts are always created. Desktop shortcuts are off by default, matching the
unchecked "Create a desktop icon" task in the old Inno script.

## The .NET 5 prerequisites

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
`SRTHost.csproj`; without it a plugin referencing `Microsoft.AspNetCore.*` fails assembly
resolution at runtime.

Payloads are **not** embedded. Each is downloaded on demand from `builds.dotnet.microsoft.com` and
verified against the SHA-512 published in
[`releases.json`](https://builds.dotnet.microsoft.com/dotnet/release-metadata/5.0/releases.json)
before it runs, which keeps the setup executable at ~1.4 MB.

### Elevation

The bundle and the MSI are both per-user, so a machine that already has .NET 5 installs, upgrades
and uninstalls SRT Host with **no UAC prompt at all**. The runtime installers are inherently
per-machine and carry their own `requireAdministrator` manifest, so they raise a single UAC prompt
— but only when a framework is genuinely missing.

### Detection

Detection is an exact-version probe for the framework directory under the default .NET install
location, e.g. `%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\5.0.17`. An exact version
is correct rather than fragile here: **5.0.17 is the final .NET 5 release** (2022-05-10) and no
further one will exist.

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
same account and certificate profile as the other SpeedRunTool repos.

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

`AutomatedRelease.yml` is the only release workflow. There is no separate manual/beta workflow —
`ManualReleaseDevelop.yml` was a near-verbatim copy of the old automated one differing only in its
trigger, a hardcoded `ref: develop`, and its release tag, so it was removed in favour of
`workflow_dispatch` here.

| Trigger | What happens |
|---|---|
| Push to `main` | Builds, signs, uploads the installer as a workflow artifact. **No tags, no release.** |
| `workflow_dispatch` from `main` | Same, plus a GitHub release and the rolling tags when `createTagsAndRelease` is checked. |
| `workflow_dispatch` from any other branch | Same, plus a GitHub release — but **no rolling tags**. Pick the branch in the Run workflow dropdown; `versionPreReleaseTag` marks it `alpha`/`beta`/`RC`. |

Tags and the release are gated on `createTagsAndRelease`, which only has a value on a dispatched
run — that is what keeps a push to `main` from publishing on its own.

The tag step carries a second condition, `github.ref == 'refs/heads/main'`, because it
**force-moves** `latest`, `vN`, `vN.M` and `vN.M.P`. Without it a beta dispatched from a side
branch would repoint `latest` at a non-main build, which is exactly what the removed
`ManualReleaseDevelop.yml` avoided by publishing under its own `develop-build` tag.

The release step is deliberately *not* guarded that way. It creates its own distinct
`vN.M.P[-tag]+B` tag and is always marked pre-release, so a side-branch build still produces a
downloadable release without disturbing anything that points at `main`.
