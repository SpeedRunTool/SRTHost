using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace SRTHost
{
    public partial class PluginSystem : BackgroundService, IHostedService
    {
        // Constants
        private const string APP_NAME = "SRT Host";
        private const string APP_ARCHITECTURE_x64 = "64-bit (x64)";
        private const string APP_ARCHITECTURE_x86 = "32-bit (x86)";
#if x64
        private const string APP_EXE_NAME = "SRTHost64.exe";
        private const string APP_ARCHITECTURE = APP_ARCHITECTURE_x64;
#else
        private const string APP_EXE_NAME = "SRTHost32.exe";
        private const string APP_ARCHITECTURE = APP_ARCHITECTURE_x86;
#endif
        private const string APP_DISPLAY_NAME = APP_NAME + " " + APP_ARCHITECTURE;

        // Plugins
        private IPlugin[] allPlugins = null;
        private Dictionary<PluginProviderStateValue, PluginUIStateValue[]> pluginProvidersAndDependentUIs = null;
        private PluginUIStateValue[] pluginUIsAgnostic = null;

        public IReadOnlyCollection<IPlugin> Plugins => new ReadOnlyCollection<IPlugin>(allPlugins);
        public IReadOnlyDictionary<PluginProviderStateValue, PluginUIStateValue[]> PluginProvidersAndDependentUIs => new ReadOnlyDictionary<PluginProviderStateValue, PluginUIStateValue[]>(pluginProvidersAndDependentUIs);
        public IReadOnlyCollection<PluginUIStateValue> PluginUIsAgnostic => new ReadOnlyCollection<PluginUIStateValue>(pluginUIsAgnostic);

        // Misc. variables
        private ManualResetEventSlim pluginReinitializeEvent;
        private ManualResetEventSlim pluginReadEvent;
        private PluginHostDelegates hostDelegates = new PluginHostDelegates();
        private string loadSpecificProvider = string.Empty; // TODO: Allow IConfiguration settings.
        private int settingUpdateRate = 33; // Default to 33ms. TODO: Allow IConfiguration settings.

        public string LoadSpecificProvider => loadSpecificProvider;

        public PluginSystem(ILogger<PluginSystem> logger, params string[] args)
        {
            this.logger = logger;
            pluginReinitializeEvent = new ManualResetEventSlim(true);
            pluginReadEvent = new ManualResetEventSlim(true);

            FileVersionInfo srtHostFileVersionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, APP_EXE_NAME));
            LogVersionBanner(srtHostFileVersionInfo.ProductName, srtHostFileVersionInfo.ProductVersion, APP_ARCHITECTURE);

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

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                string appExePath = Path.Combine(AppContext.BaseDirectory, APP_EXE_NAME);
                LogLoadedHost(appExePath.Replace(AppContext.BaseDirectory, string.Empty));
                GetSigningInfo(appExePath);

                // Initialize and start plugins.
                await InitPlugins(cancellationToken);
                await StartPlugins(cancellationToken);
            }, cancellationToken);

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await StopPlugins(cancellationToken);
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    // Don't read from the plugins until the (re-)initialize operation completes.
                    pluginReinitializeEvent.Wait(stoppingToken);

                    // Signal that we're reading from plugins to block (re-)initialization and stopping until we're done.
                    pluginReadEvent.Reset();

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
                    pluginReadEvent.Set();

                    await Task.Delay(settingUpdateRate).ConfigureAwait(false);
                }

                LogAppShutdown(APP_DISPLAY_NAME);
            }
            catch (FileLoadException ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
                LogIncorrectArchitecturePluginReference(ex.Source, ex.FileName);
                throw ex;
            }
            catch (Exception ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
                throw ex;
            }
        }

        public async Task ReloadPlugins(CancellationToken cancellationToken)
        {
            await StopPlugins(cancellationToken);
            await InitPlugins(cancellationToken);
            await StartPlugins(cancellationToken);
        }


        private async Task InitPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                // Don't (re-)initialize the plugins until the read operation completes.
                pluginReadEvent.Wait(cancellationToken);

                // Signal that we're (re-)initializing plugins to block reads until we're done.
                pluginReinitializeEvent.Reset();

                DirectoryInfo pluginsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "plugins"));

                // Create the folder if it is missing. We will eventually throw an exception due to no plugins exists but... yeah.
                if (!pluginsDir.Exists)
                    pluginsDir.Create();

                // (Re-)discover plugins.
                if (loadSpecificProvider == string.Empty)
                {
                    allPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Select((FileInfo pluginAssemblyFileInfo) => LoadPlugin(new PluginLoadContext(pluginAssemblyFileInfo.Directory), pluginAssemblyFileInfo.FullName))
                        .Where((Assembly pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly pluginAssembly) => CreatePlugins(pluginAssembly)).ToArray();
                }
                else
                {
                    allPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Where((FileInfo pluginAssemblyFileInfo) => !pluginAssemblyFileInfo.Name.Contains("Provider", StringComparison.InvariantCultureIgnoreCase) || (pluginAssemblyFileInfo.Name.Contains("Provider", StringComparison.InvariantCultureIgnoreCase) && pluginAssemblyFileInfo.Name.Equals(string.Format("{0}.dll", loadSpecificProvider), StringComparison.InvariantCultureIgnoreCase)))
                        .Select((FileInfo pluginAssemblyFileInfo) => LoadPlugin(new PluginLoadContext(pluginAssemblyFileInfo.Directory), pluginAssemblyFileInfo.FullName))
                        .Where((Assembly pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly pluginAssembly) => CreatePlugins(pluginAssembly)).ToArray();
                }

                if (allPlugins.Length == 0)
                    LogNoPlugins();

                pluginProvidersAndDependentUIs = new Dictionary<PluginProviderStateValue, PluginUIStateValue[]>();
                foreach (IPluginProvider pluginProvider in allPlugins.Where(a => typeof(IPluginProvider).IsAssignableFrom(a.GetType())).Select(a => (IPluginProvider)a))
                    pluginProvidersAndDependentUIs.Add(new PluginProviderStateValue() { Plugin = pluginProvider, Startup = false }, allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => new PluginUIStateValue() { Plugin = (IPluginUI)a, Startup = false }).Where(a => a.Plugin.RequiredProvider == pluginProvider.GetType().Name).ToArray());
                pluginUIsAgnostic = allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => new PluginUIStateValue() { Plugin = (IPluginUI)a, Startup = false }).Where(a => a.Plugin.RequiredProvider == null || a.Plugin.RequiredProvider == string.Empty).ToArray();

                pluginReinitializeEvent.Set();
            }, cancellationToken);
        }

        private async Task StartPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Startup providers.
                foreach (KeyValuePair<PluginProviderStateValue, PluginUIStateValue[]> pluginKeys in pluginProvidersAndDependentUIs)
                    PluginStartup(pluginKeys.Key);

                // Startup agnotic UIs.
                foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic)
                    PluginStartup(pluginUIStateValue);
            }, cancellationToken);
        }

        private async Task StopPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Don't stop the plugin until the read operation completes.
                pluginReadEvent.Wait(cancellationToken);

                if (pluginUIsAgnostic != null)
                    foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic)
                        PluginShutdown(pluginUIStateValue);

                if (pluginProvidersAndDependentUIs != null)
                {
                    foreach (KeyValuePair<PluginProviderStateValue, PluginUIStateValue[]> pluginKeys in pluginProvidersAndDependentUIs)
                    {
                        foreach (PluginUIStateValue pluginUI in pluginKeys.Value)
                            PluginShutdown(pluginUI);
                        PluginShutdown(pluginKeys.Key);
                    }
                }
            }, cancellationToken);
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
                LogLoadedPlugin(pluginPath.Replace(AppContext.BaseDirectory, string.Empty));
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
