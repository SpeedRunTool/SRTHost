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
                LogCommandLineBanner();
                if (kvp.Value != null)
                    LogCommandLineKeyValue(kvp.Key, kvp.Value);
                else
                    LogCommandLineKey(kvp.Key);

                switch (kvp.Key.ToUpperInvariant())
                {
                    case "HELP":
                        {
                            LogCommandLineHelpBanner();
                            LogCommandLineHelpEntryValue("Provider", "Enables single provider mode where the given provider is the only one loaded", "SRTPluginProviderRE2");
                            LogCommandLineHelpEntryValue("UpdateRate", "Sets the time in milliseconds between memory value updates", "66");
                            return;
                        }
                    case "UPDATERATE":
                        {
                            if (int.TryParse(kvp.Value, out settingUpdateRate))
                            {
                                // If we successfully parsed the value, ensure it is within range. If not, reset it.
                                if (settingUpdateRate < 16 || settingUpdateRate > 2000)
                                {
                                    LogCommandLineHelpUpdateRateOutOfRange(kvp.Key);
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
                LogLoadedHost(Path.GetRelativePath(AppContext.BaseDirectory, "SRTHost64.exe"));
                GetSigningInfo(Path.Combine(AppContext.BaseDirectory, "SRTHost64.exe"));
#else
                LogLoadedHost(Path.GetRelativePath(AppContext.BaseDirectory, "SRTHost32.exe"));
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

                if (allPlugins.Length == 0)
                {
                    Exception ex = new ApplicationException("Unable to find any plugins located in the \"plugins\" folder that implement IPlugin");
                    LogException(ex?.GetType()?.Name, ex?.ToString());
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

                LogExitHelper();
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
                LogException(ex?.GetType()?.Name, ex?.ToString());
                LogIncorrectArchitecturePluginReference(ex.Source, ex.FileName);
                criticalFailure = true;
            }
            catch (Exception ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
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
                        LogPluginStartupSuccess(plugin.Plugin.Info.Name);
                    else
                        LogPluginStartupFailure(plugin.Plugin.Info.Name, pluginStatusResponse);
                }
                catch (Exception ex)
                {
                    LogException(ex?.GetType()?.Name, ex?.ToString());
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

                    if (uiPluginReceiveDataStatus == 0)
                        LogPluginReceiveDataSuccess(plugin.Plugin.Info.Name);
                    else
                        LogPluginReceiveDataFailure(plugin.Plugin.Info.Name, uiPluginReceiveDataStatus);
                }
                catch (Exception ex)
                {
                    LogException(ex?.GetType()?.Name, ex?.ToString());
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
                        LogPluginShutdownSuccess(plugin.Plugin.Info.Name);
                    else
                        LogPluginShutdownFailure(plugin.Plugin.Info.Name, pluginStatusResponse);
                }
                catch (Exception ex)
                {
                    LogException(ex?.GetType()?.Name, ex?.ToString());
                }
                plugin.Startup = false;
            }
        }

        private Assembly LoadPlugin(PluginLoadContext loadContext, string pluginPath)
        {
            Assembly returnValue = null;

            try
            {
                returnValue = loadContext.LoadFromAssemblyPath(pluginPath);
                LogLoadedPlugin(Path.GetRelativePath(Environment.CurrentDirectory, pluginPath));
                GetSigningInfo(pluginPath);
                LogPluginVersion(FileVersionInfo.GetVersionInfo(pluginPath).ProductVersion);
            }
            catch (FileLoadException ex)
            {
                LogIncorrectArchitecturePlugin(pluginPath);
                GetSigningInfo(pluginPath);
                LogPluginVersion(FileVersionInfo.GetVersionInfo(pluginPath).ProductVersion);
            }
            catch (Exception ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
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
                LogException(ex?.GetType()?.Name, ex?.ToString());
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

        private void GetSigningInfo(string location)
        {
            X509Certificate2 cert2;
            if ((cert2 = SigningInfo.GetSigningInfo2(location)) != null)
            {
                if (cert2.Verify())
                    LogSigningInfoVerified(cert2.GetNameInfo(X509NameType.SimpleName, false), cert2.Thumbprint);
                else
                    LogSigningInfoNotVerified(cert2.GetNameInfo(X509NameType.SimpleName, false), cert2.Thumbprint);
            }
            else
                LogSigningInfoNotFound();
        }
    }
}
