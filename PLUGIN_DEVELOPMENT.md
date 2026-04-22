# Plugin Development Guide for SRTHost

## Overview

This guide explains how to develop plugins for SRTHost.

## Table of Contents

1. [Getting Started](#getting-started)
2. [Plugin Structure](#plugin-structure)
3. [Plugin Types](#plugin-types)
4. [Security Considerations](#security-considerations)
5. [Best Practices](#best-practices)
6. [Troubleshooting](#troubleshooting)

---

## Getting Started

### Prerequisites

- .NET 10 SDK
- Visual Studio 2026 (recommended) or compatible IDE
- Reference to `SRTPluginBase` NuGet package (version 5.0.0 or later)

### Basic Plugin Project Setup

1. Create a new .NET 10 Class Library project
2. Target framework: `net10.0` or `net10.0-windows` (if using Windows-specific features)
3. Add NuGet package reference:
   ```xml
   <PackageReference Include="SRTPluginBase" Version="5.0.0-*" />
   ```

---

## Plugin Structure

### Directory Layout

Plugins must follow this directory structure:

```
plugins/
└── YourPluginName/
    ├── YourPluginName.dll          # Main plugin assembly (required)
    └── Dependencies.dll            # Any plugin-specific dependencies
```

**Important:** The DLL filename must match the directory name exactly.

### Example Directory Structure

```
plugins/
└── SRTPluginProducerRE2/
    ├── SRTPluginProducerRE2.dll
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

## Security Considerations

### Code Signing and Trust

SRTHost checks for code signatures on plugins but does not enforce them. Code signing certificates can be difficult to obtain for open-source developers and individual contributors.

**Important:** Only install plugins from developers and sources you trust. While SRTHost provides assembly isolation, plugins run in-process with significant access to system resources.

If you have access to a code signing certificate, signing your plugin is recommended.

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
5. Ensure target framework is compatible (`net10.0`)

### Architecture Mismatch

**Problem:** Error about incorrect architecture (x86/x64)

**Solution:**
- Ensure your plugin is compiled for the same architecture as SRTHost
- Use "Any CPU" or explicitly target x64/x86 to match the host

### Memory Issues After Reloading

**Problem:** Memory usage grows with each plugin reload

**Solution:**
- Implement proper `Dispose()` pattern
- Unsubscribe from events in `Shutdown()`
- Clear any static references to plugin data
- Ensure all timers/threads are stopped

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

*Last Updated: 2026-04-22*
*Compatible with SRTHost 3.5.0 and SRTPluginBase 5.0.0*
*Note: Examples use '5.0.0-*' to include pre-release versions during development*
