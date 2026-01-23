# Plugin Development Guide for SRTHost

## Overview

This guide explains how to develop plugins for SRTHost, including how to create plugins with Blazor pages that integrate with the SRTHost Web UI.

## Table of Contents

1. [Getting Started](#getting-started)
2. [Plugin Structure](#plugin-structure)
3. [Plugin Types](#plugin-types)
4. [Creating Blazor Pages](#creating-blazor-pages)
5. [Security Considerations](#security-considerations)
6. [Best Practices](#best-practices)
7. [Troubleshooting](#troubleshooting)

---

## Getting Started

### Prerequisites

- .NET 8 SDK
- Visual Studio 2022 (recommended) or compatible IDE
- Reference to `SRTPluginBase` NuGet package (version 4.0.0 or later)

### Basic Plugin Project Setup

1. Create a new .NET 8 Class Library project
2. Target framework: `net8.0` or `net8.0-windows` (if using Windows-specific features)
3. Add NuGet package reference:
   ```xml
   <PackageReference Include="SRTPluginBase" Version="4.0.0-*" />
   ```

---

## Plugin Structure

### Directory Layout

Plugins must follow this directory structure:

```
plugins/
└── YourPluginName/
    ├── YourPluginName.dll          # Main plugin assembly (required)
    ├── YourPluginName.Views.dll    # Compiled Razor views (if using Blazor pages)
    ├── Dependencies.dll            # Any plugin-specific dependencies
    └── wwwroot/                    # Static files (optional)
        ├── css/
        ├── js/
        └── images/
```

**Important:** The DLL filename must match the directory name exactly.

### Example Directory Structure

```
plugins/
└── SRTPluginProducerRE2/
    ├── SRTPluginProducerRE2.dll
    ├── SRTPluginProducerRE2.Views.dll
    └── GameSpecificLibrary.dll
```

---

## Plugin Types

### Producer Plugins

Producer plugins read game memory and provide data to the system.

```csharp
using SRTPluginBase;
using SRTPluginBase.Interfaces;

public class MyProducerPlugin : IPluginProducer
{
    public IPluginInfo Info => new PluginInfo
    {
        Name = "My Game Producer",
        Version = "1.0.0",
        Description = "Reads game data from memory"
    };

    public object? Refresh()
    {
        // Return current game state
        return gameState;
    }
}
```

### Consumer Plugins

Consumer plugins display or consume data from producers.

```csharp
public class MyConsumerPlugin : IPluginConsumer
{
    public IPluginInfo Info => new PluginInfo
    {
        Name = "My Display Plugin",
        Version = "1.0.0"
    };

    public int Startup(IPluginHost host)
    {
        // Subscribe to producer data
        return 0;
    }
}
```

---

## Creating Blazor Pages

Plugin assemblies are integrated with the Blazor router, allowing you to create interactive pages using standard Blazor components.

### URL Pattern

Plugin pages are accessed at: `/api/v1/Plugin/{PluginName}/{PageRoute}`

**Example:** A plugin named `SRTPluginProducerRE2R` with a page `@page "/Data/Enemies/{id:int}"` is accessible at:
```
http://localhost:5000/api/v1/Plugin/SRTPluginProducerRE2R/Data/Enemies/12
```

### Project Setup for Blazor Pages

Add the Razor SDK to your plugin's `.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AddRazorSupportForMvc>true</AddRazorSupportForMvc>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SRTPluginBase" Version="4.0.0-*" />
  </ItemGroup>
</Project>
```

### Creating a Blazor Page

Create Blazor pages in your plugin with standard `@page` directives:

**Pages/LiveData.razor:**
```razor
@page "/LiveData"
@using YourPlugin.Models

<h3>Live Game Data</h3>

@if (isLoading)
{
    <p><em>Loading...</em></p>
}
else
{
    <div class="data-display">
        <p>Health: <strong>@gameData.Health</strong> / @gameData.MaxHealth</p>
        <p>Ammo: <strong>@gameData.Ammo</strong></p>
        <p>Last Updated: @lastUpdate.ToString("HH:mm:ss")</p>
    </div>
    
    <button class="btn btn-primary" @onclick="RefreshData">Refresh</button>
}

@code {
    private GameData gameData = new();
    private bool isLoading = true;
    private DateTime lastUpdate = DateTime.Now;

    protected override async Task OnInitializedAsync()
    {
        await RefreshData();
    }

    private async Task RefreshData()
    {
        isLoading = true;
        await Task.Delay(100); // Simulate data fetch
        
        // Get data from your plugin's producer
        gameData.Health = 100;
        gameData.MaxHealth = 150;
        gameData.Ammo = 25;
        
        lastUpdate = DateTime.Now;
        isLoading = false;
    }
}

<style>
    .data-display {
        padding: 20px;
        background: #f0f0f0;
        border-radius: 5px;
        margin: 10px 0;
    }
</style>
```

### Routing with Parameters

Blazor routing supports parameters:

```razor
@page "/Enemies/{id:int}"

<h3>Enemy Details</h3>
<p>Enemy ID: @Id</p>

@code {
    [Parameter]
    public int Id { get; set; }
}
```

Accessible at: `http://localhost:5000/api/v1/Plugin/YourPlugin/Enemies/5`

### Capabilities

**What Works:**
- ✅ Full Blazor component lifecycle
- ✅ `@page` directives with route parameters
- ✅ Dependency injection
- ✅ Component state management
- ✅ Event handling and data binding
- ✅ CSS isolation

**Limitations:**
- ⚠️ Plugin pages must be accessed under `/api/v1/Plugin/{PluginName}/` prefix
- ⚠️ Changes to plugin pages require plugin reload (no hot reload)
- ⚠️ Dynamic plugin loading/unloading may require app restart for routing updates

---

## Security Considerations

### Code Signing and Trust

SRTHost checks for code signatures on plugins but does not enforce them. Code signing certificates can be difficult to obtain for open-source developers and individual contributors.

**Important:** Only install plugins from developers and sources you trust. While SRTHost provides assembly isolation, plugins run in-process with significant access to system resources.

If you have access to a code signing certificate, signing your plugin is recommended:
1. Obtain a code signing certificate
2. Sign your plugin DLL:
   ```bash
   signtool sign /f certificate.pfx /p password /t http://timestamp.server.com YourPlugin.dll
   ```

### Content Security

⚠️ **Important:** Any HTML/JavaScript in your plugin views executes with full trust in the user's browser.

**Best Practices:**
- Never trust user input - always sanitize
- Validate all data before rendering
- Use parameterized queries if accessing databases
- Avoid `eval()` or similar dynamic code execution
- Don't store sensitive data in client-side code

### Plugin Isolation

Plugins run in isolated `AssemblyLoadContext`s but still execute in the same process:
- Plugins can potentially access SRTHost internals
- Plugins share the same security context as the host
- Malicious plugins could compromise the entire application

**Recommendations:**
- Only install plugins from trusted sources
- Review plugin source code if available
- Keep plugins updated to latest versions

---

## Best Practices

### 1. Plugin Naming

- Use descriptive names: `SRTPluginProducerGameName` or `SRTPluginConsumerDisplayType`
- Follow the existing naming convention
- Avoid special characters in plugin names

### 2. Version Management

```csharp
public IPluginInfo Info => new PluginInfo
{
    Name = "My Plugin",
    Version = "1.2.3", // Use semantic versioning
    Description = "Clear description of what the plugin does"
};
```

### 3. Error Handling

```csharp
@code {
    protected override async Task OnInitializedAsync()
    {
        try
        {
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            // Log the error
            Console.Error.WriteLine($"Error loading data: {ex.Message}");
            errorMessage = "Failed to load data";
        }
    }
}
```

### 4. Resource Cleanup

```csharp
public class MyPlugin : IPlugin, IDisposable
{
    private Timer? updateTimer;

    public int Shutdown(IPluginHost host)
    {
        Dispose();
        return 0;
    }

    public void Dispose()
    {
        updateTimer?.Dispose();
        updateTimer = null;
    }
}
```

### 5. Dependency Management

- Keep plugin-specific dependencies in the plugin folder
- Shared dependencies (like SRTPluginBase) should match the host version
- Document any external dependencies required by your plugin

---

## Troubleshooting

### Plugin Not Loading

**Problem:** Plugin doesn't appear in SRTHost

**Solutions:**
1. Verify directory structure matches plugin name
2. Check that `YourPluginName.dll` filename matches folder name
3. Verify plugin implements `IPlugin`, `IPluginProducer`, or `IPluginConsumer`
4. Check SRTHost logs for loading errors
5. Ensure target framework is compatible (`net8.0`)

### Architecture Mismatch

**Problem:** Error about incorrect architecture (x86/x64)

**Solution:**
- Ensure your plugin is compiled for the same architecture as SRTHost
- Use "Any CPU" or explicitly target x64/x86 to match the host

### Blazor Pages Not Found

**Problem:** Plugin page returns 404 or not found error

**Solutions:**
1. Verify `.Views.dll` is present in plugin directory
2. Check that Razor SDK is configured in `.csproj`
3. Ensure page has `@page` directive
4. Verify plugin is in `Instantiated` state (check plugin status icon)
5. Reload the plugin after making changes

### Memory Issues After Reloading

**Problem:** Memory usage grows with each plugin reload

**Solution:**
- Implement proper `Dispose()` pattern
- Unsubscribe from events in `Shutdown()`
- Clear any static references to plugin data
- Ensure all timers/threads are stopped

---

## Complete Example

### Project Structure
```
SRTPluginExample/
├── SRTPluginExample.csproj
├── ExamplePlugin.cs
├── Pages/
│   └── Dashboard.razor
└── Models/
    └── GameData.cs
```

### SRTPluginExample.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AddRazorSupportForMvc>true</AddRazorSupportForMvc>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="SRTPluginBase" Version="4.0.0-*" />
  </ItemGroup>
</Project>
```

### ExamplePlugin.cs
```csharp
using SRTPluginBase;
using SRTPluginBase.Interfaces;

public class ExamplePlugin : IPlugin
{
    public IPluginInfo Info => new PluginInfo
    {
        Name = "Example Plugin",
        Version = "1.0.0",
        Description = "Demonstrates plugin with Blazor pages"
    };

    public int Startup(IPluginHost host) => 0;
    public int Shutdown(IPluginHost host) => 0;
}
```

### Pages/Dashboard.razor
```razor
@page "/Dashboard"

<h3>Example Plugin Dashboard</h3>

<div class="dashboard">
    <p>Status: <span class="status">@status</span></p>
    <p>Last Update: @lastUpdate.ToString("yyyy-MM-dd HH:mm:ss")</p>
</div>

@code {
    private string status = "Running";
    private DateTime lastUpdate = DateTime.Now;
}

<style>
    .dashboard { 
        padding: 20px; 
        font-family: Arial, sans-serif; 
    }
    .status { 
        color: green; 
        font-weight: bold; 
    }
</style>
```

**Accessible at:** `http://localhost:5000/api/v1/Plugin/SRTPluginExample/Dashboard`

---

## Additional Resources

- **SRTPluginBase Repository:** [https://github.com/SpeedRunTool/SRTPluginBase](https://github.com/SpeedRunTool/SRTPluginBase)
- **Example Plugins:** [https://github.com/SpeedRunTool/SRTPluginProducerExample](https://github.com/SpeedRunTool/SRTPluginProducerExample)
- **SRTHost Repository:** [https://github.com/SpeedRunTool/SRTHost](https://github.com/SpeedRunTool/SRTHost)
- **Discord Community:** [https://discord.gg/JZvYbZmy8v](https://discord.gg/JZvYbZmy8v)

---

## Getting Help

If you encounter issues:

1. Check the SRTHost logs (located in the application directory)
2. Join the Discord community for support
3. Open an issue on the GitHub repository with:
   - SRTHost version
   - Plugin name and version
   - Error messages from logs
   - Steps to reproduce the issue

---

*Last Updated: 2024-01-23*
*Compatible with SRTHost 4.0.0 and SRTPluginBase 4.0.0*
*Note: Examples use '4.0.0-*' to include pre-release versions during development*
