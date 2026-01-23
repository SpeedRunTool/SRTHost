# Plugin Development Guide for SRTHost

## Overview

This guide explains how to develop plugins for SRTHost, including how to create plugins with custom Razor/Blazor pages that integrate with the SRTHost Web UI.

## Table of Contents

1. [Getting Started](#getting-started)
2. [Plugin Structure](#plugin-structure)
3. [Plugin Types](#plugin-types)
4. [Registered Pages API](#registered-pages-api)
5. [Razor/Blazor Page Support](#razorblazor-page-support)
6. [Security Considerations](#security-considerations)
7. [Best Practices](#best-practices)
8. [Troubleshooting](#troubleshooting)

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
    ├── YourPluginName.Views.dll    # Compiled Razor views (if using Razor pages)
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

    public Dictionary<RegisteredPagesKey, Func<ControllerBase, Task<IActionResult>>> RegisteredPages { get; }
        = new Dictionary<RegisteredPagesKey, Func<ControllerBase, Task<IActionResult>>>();

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

    public Dictionary<RegisteredPagesKey, Func<ControllerBase, Task<IActionResult>>> RegisteredPages { get; }
        = new Dictionary<RegisteredPagesKey, Func<ControllerBase, Task<IActionResult>>>();

    public int Startup(IPluginHost host)
    {
        // Subscribe to producer data
        return 0;
    }
}
```

---

## Registered Pages API

The `RegisteredPages` dictionary allows plugins to expose HTTP endpoints accessible through the Web UI.

### Registering a Page Handler

```csharp
public class MyPlugin : IPlugin
{
    public MyPlugin()
    {
        // Register a simple text response
        RegisteredPages.Add("Status", async (controller) =>
        {
            return new OkObjectResult("Plugin is running!");
        });

        // Register a JSON response
        RegisteredPages.Add("Data", async (controller) =>
        {
            var data = GetCurrentData();
            return new JsonResult(data);
        });

        // Register a file download
        RegisteredPages.Add("Export", async (controller) =>
        {
            byte[] fileData = GenerateExportFile();
            return new FileContentResult(fileData, "application/octet-stream")
            {
                FileDownloadName = "export.dat"
            };
        });
    }
}
```

### Accessing Plugin Pages

Plugin pages are accessible via:
```
GET http://localhost:5000/api/v1/Plugin/{PluginName}/{Command}
```

Example:
```
GET http://localhost:5000/api/v1/Plugin/MyPlugin/Status
GET http://localhost:5000/api/v1/Plugin/MyPlugin/Data
```

### Hiding Pages from UI

You can hide pages from the UI while keeping them accessible via direct URL:

```csharp
RegisteredPages.Add(new RegisteredPagesKey("InternalAPI", hidden: true), async (controller) =>
{
    return new OkObjectResult("This won't show in the UI buttons");
});
```

---

## Razor/Blazor Page Support

### Important Note on Routing

**Current Implementation:** Plugin Razor pages are NOT integrated with the standard Blazor router. Instead, they must be accessed through the Registered Pages API described above.

### Creating Plugin Razor Views

#### Step 1: Configure Your Plugin Project

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

#### Step 2: Create Razor Views

Create a `Views` folder in your plugin project:

```
YourPlugin/
├── Views/
│   └── Status.cshtml
├── YourPlugin.cs
└── YourPlugin.csproj
```

**Views/Status.cshtml:**
```cshtml
@model YourPlugin.Models.StatusViewModel

<h1>Plugin Status</h1>
<p>Current Status: @Model.Status</p>
<p>Data Points: @Model.DataCount</p>
```

#### Step 3: Register the View

```csharp
public class YourPlugin : IPlugin
{
    public YourPlugin()
    {
        RegisteredPages.Add("Status", async (controller) =>
        {
            var model = new StatusViewModel
            {
                Status = "Running",
                DataCount = 42
            };
            return await Task.FromResult(controller.View("Status", model));
        });
    }
}
```

### Limitations

1. **No Direct Blazor Routing:** You cannot use `@page "/MyPage"` directives for plugin pages
2. **No NavigationManager:** Standard Blazor navigation doesn't work for plugin pages
3. **API-Based Access:** All plugin pages must be accessed via the Registered Pages API
4. **No Hot Reload:** Changes to plugin Razor pages require plugin reload

---

## Security Considerations

### Code Signing (Recommended)

SRTHost checks for code signatures on plugins. While not currently enforced, it's recommended to sign your plugins:

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
RegisteredPages.Add("RiskyOperation", async (controller) =>
{
    try
    {
        var result = PerformRiskyOperation();
        return new OkObjectResult(result);
    }
    catch (Exception ex)
    {
        // Log the error
        logger.LogError(ex, "Failed to perform risky operation");
        return new ObjectResult(new { error = ex.Message })
        {
            StatusCode = 500
        };
    }
});
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

### Razor Views Not Found

**Problem:** Plugin page returns 404 or view not found error

**Solutions:**
1. Verify `.Views.dll` is present in plugin directory
2. Check that view file is marked as embedded resource
3. Ensure view path matches registration name
4. Verify Razor SDK is configured in `.csproj`

### RegisteredPages Not Working

**Problem:** Plugin pages don't appear as buttons in UI

**Solutions:**
1. Verify pages are added to `RegisteredPages` in constructor
2. Check that page keys are not marked as hidden
3. Ensure plugin is in `Instantiated` state (check plugin status icon)
4. Reload the plugin after making changes

### Memory Issues After Reloading

**Problem:** Memory usage grows with each plugin reload

**Solution:**
- Implement proper `Dispose()` pattern
- Unsubscribe from events in `Shutdown()`
- Clear any static references to plugin data
- Ensure all timers/threads are stopped

---

## Example: Complete Plugin with Razor View

### Project Structure
```
SRTPluginExample/
├── SRTPluginExample.csproj
├── ExamplePlugin.cs
├── Models/
│   └── ExampleModel.cs
└── Views/
    └── Dashboard.cshtml
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
using Microsoft.AspNetCore.Mvc;
using SRTPluginBase;
using SRTPluginBase.Interfaces;

public class ExamplePlugin : IPlugin
{
    public IPluginInfo Info => new PluginInfo
    {
        Name = "Example Plugin",
        Version = "1.0.0",
        Description = "Demonstrates plugin with Razor views"
    };

    public Dictionary<RegisteredPagesKey, Func<ControllerBase, Task<IActionResult>>> RegisteredPages { get; }
        = new Dictionary<RegisteredPagesKey, Func<ControllerBase, Task<IActionResult>>>();

    public ExamplePlugin()
    {
        RegisteredPages.Add("Dashboard", async (controller) =>
        {
            var model = new ExampleModel
            {
                Status = "Running",
                LastUpdate = DateTime.Now
            };
            return await Task.FromResult(controller.View("Dashboard", model));
        });

        RegisteredPages.Add("Data", async (controller) =>
        {
            var data = new { value = 42, timestamp = DateTime.Now };
            return new JsonResult(data);
        });
    }

    public int Startup(IPluginHost host) => 0;
    public int Shutdown(IPluginHost host) => 0;
}
```

### Views/Dashboard.cshtml
```cshtml
@model ExampleModel

<!DOCTYPE html>
<html>
<head>
    <title>Example Plugin Dashboard</title>
    <style>
        .dashboard { padding: 20px; font-family: Arial, sans-serif; }
        .status { color: green; font-weight: bold; }
    </style>
</head>
<body>
    <div class="dashboard">
        <h1>Example Plugin Dashboard</h1>
        <p>Status: <span class="status">@Model.Status</span></p>
        <p>Last Update: @Model.LastUpdate.ToString("yyyy-MM-dd HH:mm:ss")</p>
    </div>
</body>
</html>
```

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
2. Review the [BLAZOR_ANALYSIS.md](BLAZOR_ANALYSIS.md) document for known issues
3. Join the Discord community for support
4. Open an issue on the GitHub repository with:
   - SRTHost version
   - Plugin name and version
   - Error messages from logs
   - Steps to reproduce the issue

---

*Last Updated: 2024-01-23*
*Compatible with SRTHost 4.0.0 and SRTPluginBase 4.0.0*
*Note: Examples use '4.0.0-*' to include pre-release versions during development*
