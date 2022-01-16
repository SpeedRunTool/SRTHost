using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace SRTHost
{
    public partial class PluginSystem : BackgroundService
    {
        private bool running = true;
        private PluginHostDelegates hostDelegates = new PluginHostDelegates();
        private string loadSpecificProvider = string.Empty;
        private int settingUpdateRate = 33; // Default to 33ms.

        private readonly ILogger<PluginSystem> logger;

        [LoggerMessage((int)LogLevel.Information * 1000 + 0, LogLevel.Information, "{productName} v{productVersion} {productArchitecture}", EventName = "Version Banner")]
        private partial void LogVersionBanner(string? productName, string? productVersion, string? productArchitecture);

        public PluginSystem(ILogger<PluginSystem> logger, params string[] args)
        {
            this.logger = logger;

            Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
            {
                e.Cancel = true;
                running = false;
            };

            FileVersionInfo srtHostFileVersionInfo;
#if x64
            srtHostFileVersionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "SRTHost64.exe"));
#else
            srtHostFileVersionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, "SRTHost32.exe"));
#endif
            LogVersionBanner(srtHostFileVersionInfo.ProductName, srtHostFileVersionInfo.ProductVersion, (Environment.Is64BitProcess) ? "64-bit (x64)" : "32-bit (x86)");

            foreach (KeyValuePair<string, string?> kvp in new CommandLineProcessor(args))
            {
                Console.WriteLine("Command-line arguments:");
                if (kvp.Value != null)
                    Console.WriteLine("{0}: {1}", kvp.Key, kvp.Value);
                else
                    Console.WriteLine("{0}", kvp.Key);
                Console.WriteLine();

                switch (kvp.Key.ToUpperInvariant())
                {
                    case "HELP":
                        {
                            string helpTemplate = "  --{0}=<Value>: {1}. Default: {2}\r\n    Example: --{0}={2}";
                            Console.WriteLine("Arguments and examples");
                            Console.WriteLine(helpTemplate, "UpdateRate", "Sets the time in milliseconds between memory value updates", "66");
                            return;
                        }
                    case "UPDATERATE":
                        {
                            if (int.TryParse(kvp.Value, out settingUpdateRate))
                            {
                                // If we successfully parsed the value, ensure it is within range. If not, reset it.
                                if (settingUpdateRate < 16 || settingUpdateRate > 2000)
                                {
                                    Console.WriteLine("Error: {0} cannot be less than 16ms or greater than 2000ms. Resetting to default (66ms).", kvp.Key);
                                    settingUpdateRate = 66;
                                }
                            }
                            break;
                        }
                    case "PROVIDER":
                        {
                            loadSpecificProvider = kvp.Value;
                            break;
                        }
                    default:
                        {
                            break;
                        }
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            IPlugin[] allPlugins = null;
            Dictionary<PluginProviderStateValue, PluginUIStateValue[]> pluginProvidersAndDependentUIs = null;
            PluginUIStateValue[] pluginUIsAgnostic = null;

            bool criticalFailure = false; // Used if we should fail right past the finally clause.
            try
            {
#if x64
                Console.WriteLine("  Loaded host: {0}", Path.GetRelativePath(AppContext.BaseDirectory, "SRTHost64.exe"));
                ShowSigningInfo(Path.Combine(AppContext.BaseDirectory, "SRTHost64.exe"));
#else
                    Console.WriteLine("  Loaded host: {0}", Path.GetRelativePath(AppContext.BaseDirectory, "SRTHost32.exe"));
                    ShowSigningInfo(Path.Combine(AppContext.BaseDirectory, "SRTHost32.exe"));
#endif

                if (loadSpecificProvider == string.Empty)
                {
                    allPlugins = new DirectoryInfo("plugins")
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Select((FileInfo pluginAssemblyFileInfo) => LoadPlugin(new PluginLoadContext(pluginAssemblyFileInfo.Directory), pluginAssemblyFileInfo.FullName))
                        .Where((Assembly pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly pluginAssembly) => CreatePlugins(pluginAssembly)).ToArray();
                }
                else
                {
                    allPlugins = new DirectoryInfo("plugins")
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Where((FileInfo pluginAssemblyFileInfo) => !pluginAssemblyFileInfo.Name.Contains("Provider", StringComparison.InvariantCultureIgnoreCase) || (pluginAssemblyFileInfo.Name.Contains("Provider", StringComparison.InvariantCultureIgnoreCase) && pluginAssemblyFileInfo.Name.Equals(string.Format("{0}.dll", loadSpecificProvider), StringComparison.InvariantCultureIgnoreCase)))
                        .Select((FileInfo pluginAssemblyFileInfo) => LoadPlugin(new PluginLoadContext(pluginAssemblyFileInfo.Directory), pluginAssemblyFileInfo.FullName))
                        .Where((Assembly pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly pluginAssembly) => CreatePlugins(pluginAssembly)).ToArray();
                }
                Console.WriteLine();

                if (allPlugins.Length == 0)
                {
                    HandleException(new ApplicationException("Unable to find any plugins located in the \"plugins\" folder that implement IPlugin"));
                    Environment.Exit(1); // Critical error. Handle better. Only one provider allowed.
                }

                pluginProvidersAndDependentUIs = new Dictionary<PluginProviderStateValue, PluginUIStateValue[]>();
                foreach (IPluginProvider pluginProvider in allPlugins.Where(a => typeof(IPluginProvider).IsAssignableFrom(a.GetType())).Select(a => (IPluginProvider)a))
                    pluginProvidersAndDependentUIs.Add(new PluginProviderStateValue() { Plugin = pluginProvider, Startup = false }, allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => new PluginUIStateValue() { Plugin = (IPluginUI)a, Startup = false }).Where(a => a.Plugin.RequiredProvider == pluginProvider.GetType().Name).ToArray());
                pluginUIsAgnostic = allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => new PluginUIStateValue() { Plugin = (IPluginUI)a, Startup = false }).Where(a => a.Plugin.RequiredProvider == null || a.Plugin.RequiredProvider == string.Empty).ToArray();

                // Startup providers.
                foreach (KeyValuePair<PluginProviderStateValue, PluginUIStateValue[]> pluginKeys in pluginProvidersAndDependentUIs)
                    PluginStartup(pluginKeys.Key);

                // Startup agnotic UIs.
                foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic)
                    PluginStartup(pluginUIStateValue);

                Console.WriteLine("Press CTRL+C in this console window to shutdown the SRT.");
                while (running)
                {
                    foreach (KeyValuePair<PluginProviderStateValue, PluginUIStateValue[]> pluginKeys in pluginProvidersAndDependentUIs)
                    {
                        if (pluginKeys.Key.Startup && pluginKeys.Key.Plugin.GameRunning) // Provider is started and game is running.
                        {
                            object gameMemory = pluginKeys.Key.Plugin.PullData();
                            foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic.Concat(pluginKeys.Value))
                                PluginReceiveData(pluginUIStateValue, gameMemory);
                        }
                        else if (pluginKeys.Key.Startup && !pluginKeys.Key.Plugin.GameRunning) // Provider is started and game is not running.
                        {
                            // Loop through this plugin's UIs and shut them down if they're running. Only shuts down dependent UIs. Agnostic UIs such as JSON shouldn't be touched.
                            foreach (PluginUIStateValue pluginUIStateValue in pluginKeys.Value)
                                if (pluginUIStateValue.Startup)
                                    PluginShutdown(pluginUIStateValue);
                        }
                    }
                    //Thread.Sleep(settingUpdateRate);
                    await Task.Delay(settingUpdateRate).ConfigureAwait(false);
                }
            }
            catch (FileLoadException ex)
            {
                HandleException(ex);
                HandleIncorrectArchitecture(null, ex.Source, ex.FileName);
                criticalFailure = true;
            }
            catch (Exception ex)
            {
                HandleException(ex);
            }
            finally
            {
                // Shutdown all if we haven't critically failed.
                if (!criticalFailure && allPlugins != null)
                {
                    foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic)
                        PluginShutdown(pluginUIStateValue);

                    foreach (KeyValuePair<PluginProviderStateValue, PluginUIStateValue[]> pluginKeys in pluginProvidersAndDependentUIs)
                    {
                        foreach (PluginUIStateValue pluginUI in pluginKeys.Value)
                            PluginShutdown(pluginUI);
                        PluginShutdown(pluginKeys.Key);
                    }
                }
            }
        }

        private void PluginStartup<T>(IPluginStateValue<T> plugin) where T : IPlugin
        {
            if (!plugin.Startup)
            {
                int pluginStatusResponse = 0;
                try
                {
                    pluginStatusResponse = plugin.Plugin.Startup(hostDelegates);

                    if (pluginStatusResponse == 0)
                        Console.WriteLine("[{0}] successfully started.", plugin.Plugin.Info.Name);
                    else
                        Console.WriteLine("[{0}] failed to startup properly with status {1}.", plugin.Plugin.Info.Name, pluginStatusResponse);
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }
                plugin.Startup = true;
            }
        }

        private void PluginReceiveData<T>(IPluginStateValue<T> plugin, object gameMemory) where T : IPluginUI
        {
            // If the UI plugin isn't started, start it now.
            if (!plugin.Startup)
                PluginStartup(plugin);

            if (gameMemory != null)
            {
                int uiPluginReceiveDataStatus = 0;
                try
                {
                    uiPluginReceiveDataStatus = plugin.Plugin.ReceiveData(gameMemory);
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }
            }
        }

        private void PluginShutdown<T>(IPluginStateValue<T> plugin) where T : IPlugin
        {
            if (plugin.Startup)
            {
                int pluginStatusResponse = 0;
                try
                {
                    pluginStatusResponse = plugin.Plugin.Shutdown();

                    if (pluginStatusResponse == 0)
                        Console.WriteLine("[{0}] successfully shutdown.", plugin.Plugin.Info.Name);
                    else
                        Console.WriteLine("[{0}] failed to shutdown properly with status {1}.", plugin.Plugin.Info.Name, pluginStatusResponse);
                }
                catch (Exception ex)
                {
                    HandleException(ex);
                }
                plugin.Startup = false;
            }
        }

        private void ShowSigningInfo(string location)
        {
            X509Certificate2 cert2;
            if ((cert2 = SigningInfo.GetSigningInfo2(location)) != null)
            {
                if (cert2.Verify())
                    Console.WriteLine("\tDigitally signed and verified: {0} [Thumbprint: {1}]", cert2.GetNameInfo(X509NameType.SimpleName, false), cert2.Thumbprint);
                else
                    Console.WriteLine("\tDigitally signed but NOT verified: {0} [Thumbprint: {1}]", cert2.GetNameInfo(X509NameType.SimpleName, false), cert2.Thumbprint);
            }
            else
                Console.WriteLine("\tNo digital signature found.");
        }

        private void ShowVersionInfo(string location)
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(location);
            Console.WriteLine("\tVersion v{0}.{1}.{2}.{3}", versionInfo.ProductMajorPart, versionInfo.ProductMinorPart, versionInfo.ProductBuildPart, versionInfo.ProductPrivatePart);
        }

        private Assembly LoadPlugin(PluginLoadContext loadContext, string pluginPath)
        {
            Assembly returnValue = null;

            try
            {
                returnValue = loadContext.LoadFromAssemblyPath(pluginPath);
                Console.WriteLine("  Loaded plugin: {0}", Path.GetRelativePath(Environment.CurrentDirectory, pluginPath));
                ShowSigningInfo(pluginPath);
                ShowVersionInfo(pluginPath);
            }
            catch (FileLoadException ex)
            {
                HandleIncorrectArchitecture(pluginPath);
                ShowSigningInfo(pluginPath);
                ShowVersionInfo(pluginPath);
            }
            catch (Exception ex)
            {
                HandleException(ex);
            }

            return returnValue;
        }

        private IEnumerable<IPlugin> CreatePlugins(Assembly assembly)
        {
            int count = 0;
            Type[] typesInAssembly = null;

            try
            {
                typesInAssembly = assembly.GetTypes();
            }
            catch (Exception ex)
            {
                HandleException(ex);
                yield break;
            }

            if (typesInAssembly != null)
            {
                foreach (Type type in typesInAssembly)
                {
                    if (type.GetInterface(nameof(IPluginProvider)) != null)
                    {
                        IPluginProvider result = (IPluginProvider)Activator.CreateInstance(type); // If this throws an exception, the plugin may be targeting a different version of SRTPluginBase.
                        count++;
                        yield return result;
                    }
                    else if (type.GetInterface(nameof(IPluginUI)) != null)
                    {
                        IPluginUI result = (IPluginUI)Activator.CreateInstance(type);
                        count++;
                        yield return result;
                    }
                    else if (type.GetInterface(nameof(IPlugin)) != null)
                    {
                        IPlugin result = (IPlugin)Activator.CreateInstance(type);
                        count++;
                        yield return result;
                    }
                }
            }
        }

        private void HandleException(Exception exception)
        {
            try
            {
                string exceptionMessage = string.Format("[{0}] {1}", exception?.GetType()?.Name, exception?.ToString());
                Console.WriteLine(exceptionMessage);
            }
            catch
            {
                Console.WriteLine("FATAL ERROR IN HandleException(Exception exception);");
            }
        }

        private void HandleIncorrectArchitecture(string? pluginPath = null, string? sourcePlugin = null, string? assemblyName = null)
        {
            if (pluginPath != null)
                Console.WriteLine("! Failed plugin: {0}\r\n\tIncorrect architecture. {1}.", Path.GetRelativePath(Environment.CurrentDirectory, pluginPath), (Environment.Is64BitProcess) ? "SRT Host 64-bit (x64) cannot load a 32-bit (x86) DLL" : "SRT Host 32-bit (x86) cannot load a 64-bit (x64) DLL");
            else if (sourcePlugin != null && assemblyName != null)
                Console.WriteLine("! Failed plugin: plugins\\{0}\\{0}.dll\r\n\tIncorrect architecture in referenced assembly \"{2}\". {1}.", sourcePlugin, (Environment.Is64BitProcess) ? "SRT Host 64-bit (x64) cannot load a 32-bit (x86) DLL" : "SRT Host 32-bit (x86) cannot load a 64-bit (x64) DLL", assemblyName);
        }
    }
}
