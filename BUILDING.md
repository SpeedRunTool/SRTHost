# Building

## To build this source code you'll need the following items

* [Git](https://git-scm.com/)
* [Visual Studio 2025](https://visualstudio.microsoft.com/downloads/)
* [.NET 10 SDK (x64)](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

When installing `Visual Studio 2025` (herein referred to as `VS2025`), you will minimally need the following workloads selected:

* ASP.NET and web development
* .NET desktop development

When developing locally and targeting the `Debug` configuration, our project is setup to reference the dependency `SRTPluginBase` locally rather than the nuget package release. This is so we can develop them in tandem without having to release a new nuget package as we're making changes and testing. As a result, you'll want to clone the `SRTPluginBase` repo as well and ensure you're on the same branch (`develop` for new mainline work) for both repos.

## Example steps to build on a new Windows 11 installation

Install the edition of `VS2025` you prefer with the previously mentioned workloads.

Open a command-line terminal to a new folder and enter:

<!-- List of code fence languages supported by GitHub: https://github.com/github-linguist/linguist/blob/master/lib/linguist/languages.yml -->
```pwsh
New-Item -Path .\SpeedRunTool -ItemType Directory
Set-Location .\SpeedRunTool
git clone https://github.com/SpeedRunTool/SRTHost --branch develop
git clone https://github.com/SpeedRunTool/SRTPluginBase --branch develop
Set-Location .\SRTHost\src
.\SRTHost.slnx
```

Select `Build Solution` from the `Build` menu. It should build successfully unless someone broke the branch you're working on or you're missing dependencies. `SRTHost` uses ASP.NET Core, Razor, and Blazor components and so those options must be selected when installing `VS2025`. They should be automatically included if you selected the workloan `ASP.NET and web development`.
