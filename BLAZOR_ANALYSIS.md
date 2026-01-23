# SRTHost Blazor/Razor Plugin Support Analysis

## Executive Summary

This document provides a comprehensive analysis of the Blazor/Razor plugin support implementation in SRTHost, focusing on the architecture that enables plugins to provide their own Razor/Blazor pages viewable through the SRTHost Web UI.

---

## Architecture Overview

### Plugin System Architecture

SRTHost uses a plugin-based architecture where:
1. **Producer Plugins** - Read game memory and provide data
2. **Consumer Plugins** - Display/consume data from producers
3. **Host (SRTHost)** - Manages plugins and provides Web UI

### Blazor/Razor Integration Components

The main components enabling plugin Razor/Blazor page support are:

1. **PluginViewCompiler** - Custom IViewCompiler implementation
2. **PluginViewCompilerProvider** - Provides the custom view compiler
3. **PluginLoadContext** - Custom AssemblyLoadContext for plugin isolation
4. **PluginHost** - Manages plugin lifecycle and view registration

---

## Key Issues and Concerns

### 1. **Router Configuration Missing AdditionalAssemblies**

**Severity: High**

**Location:** `App.razor`

**Issue:**
The Blazor Router only scans `@typeof(App).Assembly` for routable components. Plugin assemblies with `@page` directives are loaded into the `ApplicationPartManager` via `PluginViewCompiler.LoadModuleCompiledViews()` but are NOT added to the Router's `AdditionalAssemblies` property.

```csharp
// Current implementation
<Router AppAssembly="@typeof(App).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)" />
    </Found>
</Router>
```

**Impact:**
- Plugin Razor pages with `@page` directives will NOT be discoverable by the Blazor router
- Plugins cannot provide navigable pages via the standard Blazor routing mechanism
- Users cannot navigate to `/PluginName/PageName` routes defined in plugins

**Current Workaround:**
The system uses API endpoints (`/api/v1/Plugin/{Plugin}/{Command}`) as a workaround, but this bypasses the Blazor router entirely and uses `RegisteredPages` dictionary instead.

**Recommendation:**
Either:
1. **Use AdditionalAssemblies** (Preferred for true Blazor integration):
   ```csharp
   <Router AppAssembly="@typeof(App).Assembly" 
           AdditionalAssemblies="@GetPluginAssemblies()">
   ```
   And implement:
   ```csharp
   @code {
       private Assembly[] GetPluginAssemblies()
       {
           return pluginHost.LoadedPlugins.Values
               .Where(p => p.PluginType?.Assembly != null)
               .Select(p => p.PluginType!.Assembly)
               .Distinct()
               .ToArray();
       }
   }
   ```

2. **Or document clearly** that plugin pages are NOT intended to work with standard Blazor routing and must use the `RegisteredPages` API-based approach.

---

### 2. **Security: Unvalidated Plugin Code Execution**

**Severity: Critical**

**Location:** `PluginViewCompiler.cs`, `PluginHost.cs`

**Issue:**
Plugin assemblies are loaded with full trust and their Razor views are compiled and executed without any sandboxing or validation. While plugins are loaded in isolated `AssemblyLoadContext`s, they still execute in the same process with full access to the host application's memory and resources.

**Code Reference:**
```csharp
// PluginHost.cs lines 196-201
pluginStateValue.LoadContext = new PluginLoadContext(pluginFileInfo.Directory!);
Assembly? pluginAssembly = pluginStateValue.LoadContext.LoadFromAssemblyPath(pluginFileInfo.FullName);
pluginStateValue.PluginType = GetPluginType(pluginAssembly);
PluginViewCompiler.Current?.LoadModuleCompiledViews(pluginAssembly);
```

**Risks:**
- Malicious plugins could inject arbitrary HTML/JavaScript into pages
- Plugin code runs with same privileges as host application
- Cross-site scripting (XSS) vulnerabilities if plugin-provided content isn't sanitized
- Potential for plugins to access/modify other plugins' data
- Memory corruption or crashes from poorly written plugins

**Recommendations:**
1. **Code Signing Validation:** Currently `GetSigningInfo()` checks signatures but doesn't enforce trust. Consider:
   - Only load plugins with valid signatures from trusted publishers
   - Implement a whitelist of approved plugin publishers
   - Log security warnings for unsigned plugins

2. **Content Security Policy (CSP):** Add CSP headers to prevent inline scripts:
   ```csharp
   app.Use(async (context, next) =>
   {
       context.Response.Headers.Add("Content-Security-Policy", 
           "default-src 'self'; script-src 'self' 'unsafe-eval'");
       await next();
   });
   ```

3. **Input Validation:** Any plugin-provided content rendered in Razor views should be sanitized
4. **Permissions System:** Consider implementing a plugin permissions system
5. **Documentation:** Clearly document security considerations in plugin development guide

---

### 3. **Memory Leak Risk: View Compiler Lifecycle**

**Severity: Medium**

**Location:** `PluginViewCompiler.cs`

**Issue:**
The `PluginViewCompiler` maintains several dictionaries that grow over plugin load/unload cycles:
- `CancellationTokenSources` (line 29)
- `NormalizedPathCache` (line 30)  
- `CompiledViews` (line 34)

While `UnloadModuleCompiledViews()` removes entries from `CompiledViews` and cancels tokens in `CancellationTokenSources`, the `NormalizedPathCache` is never cleared.

**Code Reference:**
```csharp
// PluginViewCompiler.cs
protected IDictionary<string, string> NormalizedPathCache { get; } // Never cleared!

public void UnloadModuleCompiledViews(Assembly moduleAssembly)
{
    // Removes from CompiledViews and CancellationTokenSources
    // But NormalizedPathCache continues to grow
}
```

**Impact:**
- Memory usage grows with each plugin reload
- Cache pollution from unloaded plugin paths
- Potential memory exhaustion in long-running hosts with frequent plugin reloads

**Recommendation:**
Clear the `NormalizedPathCache` when unloading plugins, or use a `ConditionalWeakTable` to allow garbage collection of unused entries.

```csharp
public void UnloadModuleCompiledViews(Assembly moduleAssembly)
{
    // ... existing code ...
    
    // Clear cache entries for unloaded assembly
    var keysToRemove = this.NormalizedPathCache
        .Where(kvp => /* determine if belongs to moduleAssembly */)
        .Select(kvp => kvp.Key)
        .ToList();
    foreach (var key in keysToRemove)
        this.NormalizedPathCache.Remove(key);
}
```

---

### 4. **Race Condition: PluginViewCompiler.Current Static Property**

**Severity: Medium**

**Location:** `PluginViewCompiler.cs` line 23, line 42

**Issue:**
The static `Current` property is set in the constructor without thread synchronization:

```csharp
public static PluginViewCompiler? Current { get; private set; }

public PluginViewCompiler(ApplicationPartManager applicationPartManager, ILoggerFactory loggerFactory)
{
    // ...
    Current = this; // No lock or thread-safety
}
```

**Impact:**
- Race condition if multiple instances are created simultaneously (unlikely but possible)
- The last instance wins, potentially causing references to wrong compiler
- No guarantee of proper initialization order in DI container

**Recommendation:**
Use dependency injection instead of static singleton:
- Inject `PluginViewCompiler` directly where needed
- Remove the static `Current` property
- Use proper scoping in DI container

---

### 5. **Error Handling: Silent Failures in Plugin Loading**

**Severity: Medium**

**Location:** `PluginHost.cs` lines 341-351

**Issue:**
Exception handling during view unloading re-throws without logging:

```csharp
foreach (Assembly assembly in pluginStateValue.LoadContext!.Assemblies)
{
    try
    {
        PluginViewCompiler.Current?.UnloadModuleCompiledViews(assembly);
    }
    catch
    {
        throw; // Re-throws without logging context
    }
}
```

**Impact:**
- Loss of diagnostic information when unloading fails
- Difficult to debug plugin unload issues
- Silent failures may leave system in inconsistent state

**Recommendation:**
Log exceptions before re-throwing:
```csharp
catch (Exception ex)
{
    logger.LogError(ex, "Failed to unload compiled views for assembly {AssemblyName}", assembly.FullName);
    throw;
}
```

---

### 6. **Plugin Discovery: Fragile Directory Structure Requirement**

**Severity: Low**

**Location:** `PluginHost.cs` line 370

**Issue:**
Plugin discovery assumes a rigid directory structure:

```csharp
private IEnumerable<string> GetPluginNames() => 
    new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "plugins"))
        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
        .SelectMany(d => d.EnumerateFiles($"{d.Name}.dll", SearchOption.TopDirectoryOnly))
        .Select(f => Path.GetFileNameWithoutExtension(f.Name));
```

**Requirements:**
- Must be in `plugins/` directory
- Plugin DLL must be in subfolder with same name
- Pattern: `plugins/PluginName/PluginName.dll`

**Impact:**
- Inflexible deployment options
- Cannot support alternative plugin layouts
- Difficult to support multiple versions of same plugin
- No support for plugin packages with different naming conventions

**Recommendation:**
1. Support plugin manifest files for flexible layouts
2. Allow configuration of plugin directories
3. Document the required directory structure clearly
4. Consider supporting a flat directory structure as alternative

---

### 7. **CSProj Workarounds: Non-standard SDK Configuration**

**Severity: Low**

**Location:** `SRTHost.csproj` lines 3-16, 106-117

**Issue:**
The project uses manual SDK imports instead of standard SDK attributes:

```xml
<!-- workaround for not using Microsoft.NET.Sdk.Web -->
<PropertyGroup>
    <UsingMicrosoftNETSdkWeb>true</UsingMicrosoftNETSdkWeb>
</PropertyGroup>

<Import Sdk="Microsoft.NET.Sdk" Project="Sdk.props" />
<Import Sdk="Microsoft.NET.Sdk.Razor" Project="Sdk.props" />
<!-- ... -->
<Import Sdk="Microsoft.NET.Sdk" Project="Sdk.targets" />
<Import Sdk="Microsoft.NET.Sdk.Razor" Project="Sdk.targets" />
```

**Reason (from BUILDING.md):**
VSCode has issues with Razor parsing and project file workarounds.

**Impact:**
- Non-standard project structure may confuse new developers
- Potential compatibility issues with future .NET SDK versions
- IDE tooling may not work as expected
- Build errors might be harder to diagnose

**Recommendation:**
1. Document WHY these workarounds exist in comments
2. Periodically test if standard SDK configuration works
3. Consider migrating to standard SDK when tooling improves
4. Add issue tracking link in comments for future reference

---

### 8. **Platform Targeting: Windows-Only Limitation**

**Severity: Low (by design)

**Location:** `SRTHost.csproj` line 40

**Issue:**
Project targets `net8.0-windows` and uses Windows Forms and WPF:

```xml
<TargetFramework>net8.0-windows</TargetFramework>
<UseWindowsForms>true</UseWindowsForms>
<UseWPF>true</UseWPF>
```

**Impact:**
- Cannot run on Linux/macOS despite using ASP.NET Core web stack
- Limits deployment options (no containers, cloud hosting)
- Web UI portion could be cross-platform but entire app is Windows-only

**Recommendation:**
If Windows-specific features (Forms/WPF) are not actually used for core functionality:
1. Consider splitting into separate projects: Core (cross-platform) + Windows UI
2. Or make Windows-specific features conditional
3. Document clearly that this is Windows-only by design

---

### 9. **Missing Plugin Assembly Discovery for Router**

**Severity: High**

**Issue:**
Even if `AdditionalAssemblies` is added to the Router, there's no mechanism to dynamically refresh the router when plugins are loaded/unloaded at runtime.

**Current Behavior:**
- Plugins can be loaded/reloaded via API while app is running
- Router is initialized once at app startup
- New plugin pages won't be discoverable until app restart

**Recommendation:**
1. Implement dynamic router refresh on plugin load/unload
2. Or document that plugin pages require app restart to become routable
3. Or continue with current API-based approach (not using Blazor router)

---

## Positive Aspects

### What Works Well:

1. **Isolated Plugin Loading**: `PluginLoadContext` provides good assembly isolation
2. **Dependency Resolution**: Smart handling of shared dependencies (SRTPluginBase)
3. **View Compilation**: Elegant integration with ASP.NET Core's view system
4. **Hot Reload Support**: Can reload plugins without restarting the host
5. **SigningInfo Checks**: Attempts to verify plugin signatures
6. **MudBlazor Integration**: Clean, modern UI framework usage
7. **CORS Configuration**: Properly configured for API access

---

## Recommendations Summary

### High Priority:
1. ✅ Fix Router to include plugin assemblies OR document API-only approach
2. ✅ Implement plugin code signing enforcement
3. ✅ Add CSP headers for XSS protection

### Medium Priority:
4. Fix memory leak in `NormalizedPathCache`
5. Replace static `Current` property with proper DI
6. Improve error logging in plugin unload

### Low Priority:
7. Document CSProj workarounds
8. Support flexible plugin directory structures
9. Consider cross-platform architecture (if applicable)

---

## Additional Observations

### Plugin Page API Approach
The current implementation uses `IPlugin.RegisteredPages` dictionary that maps command names to handler functions. This is accessed via:
```
GET /api/v1/Plugin/{PluginName}/{Command}
```

This approach:
- ✅ Works independently of Blazor router
- ✅ Allows custom HTTP handling per plugin
- ✅ Simple to implement in plugins
- ❌ Doesn't use standard Blazor routing/navigation
- ❌ Requires HTTP client calls instead of NavigationManager
- ❌ Loses Blazor's SPA navigation benefits

### Testing Considerations
- No tests found in repository
- Consider adding integration tests for plugin loading
- UI tests for Blazor components would be valuable
- Test plugin signing validation logic

---

## Conclusion

The Blazor/Razor plugin support implementation shows good architectural thinking with assembly isolation and dynamic view compilation. However, there are several issues that need attention:

1. **Architecture decision needed**: Choose between Blazor router integration vs. API-only approach
2. **Security hardening**: Enforce plugin signing and add XSS protections
3. **Maintenance improvements**: Fix memory leaks and improve error handling

The system appears to be in a transitional state where Blazor infrastructure is in place but the plugin integration story isn't complete. The decision of whether to fully embrace Blazor routing or continue with the API-based approach should be made explicitly and documented.

**Note**: The `CascadingStateChanger` class referenced in the codebase is provided by the `SRTPluginBase` dependency, not by SRTHost itself.

---

*Analysis Date: 2024-01-23*
*SRTHost Version: 4.0.0*
*Target Framework: .NET 8.0*
