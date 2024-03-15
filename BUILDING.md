# Building

## To build this source code you'll need the following items

* [Git](https://git-scm.com/)
* [Visual Studio 2022](https://visualstudio.microsoft.com/downloads/)
* [.NET 8 SDK (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

When installing `Visual Studio 2022` (herein referred to as `VS2022`), you will minimally need the following workloads selected:

* ASP.NET and web development
* .NET desktop development

While `VSCode` would in theory work, at the time of this writing (2024-03-14) it has been having some razor parsing errors (<https://github.com/dotnet/razor/issues/9986> and <https://github.com/dotnet/razor/issues/10056>) as well as other issues with some of the workarounds we had to do in our CSProj files so it is recommended to use full blown `VS2022` for sanity.

When developing locally and targeting the `Debug` configuration, our project is setup to reference the dependency `SRTPluginBase` locally rather than the nuget package release. This is so we can develop them in tandem without having to release a new nuget package as we're making changes and testing. As a result, you'll want to clone the `SRTPluginBase` repo as well and ensure you're on the same branch (`develop` for new mainline work) for both repos.

## Example steps to build on a new Windows 11 installation

Install the edition of `VS2022` you prefer with the previously mentioned workloads.

Open a command-line terminal to a new folder and enter:

```pwsh
git clone https://github.com/SpeedRunTool/SRTHost --branch develop
git clone https://github.com/SpeedRunTool/SRTPluginBase --branch develop
```

Then open `VS2022` and select the `SRTHost.sln` solution at the root of the `SRTHost` repo folder you cloned.

Select `Build Solution` from the `Build` menu. It should build successfully unless someone broke the branch you're working on or you're missing dependencies. `SRTHost` uses ASP.NET Core, Razor, and Blazor components and so those options must be selected when installing `VS2022`. They should be automatically included if you selected the workloan `ASP.NET and web development`.
