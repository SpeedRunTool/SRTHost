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
        private Dictionary<PluginProducerStateValue, PluginUIStateValue[]> pluginProducersAndDependentUIs = null;
        private PluginUIStateValue[] pluginUIsAgnostic = null;

        public IReadOnlyCollection<IPlugin> Plugins => new ReadOnlyCollection<IPlugin>(allPlugins);
        public IReadOnlyDictionary<PluginProducerStateValue, PluginUIStateValue[]> PluginProducersAndDependentUIs => new ReadOnlyDictionary<PluginProducerStateValue, PluginUIStateValue[]>(pluginProducersAndDependentUIs);
        public IReadOnlyCollection<PluginUIStateValue> PluginUIsAgnostic => new ReadOnlyCollection<PluginUIStateValue>(pluginUIsAgnostic);

        // Misc. variables
        private IList<PluginLoadContext> pluginLoadContexts;
        private ManualResetEventSlim pluginReinitializeEvent;
        private ManualResetEventSlim pluginReadEvent;
        private string loadSpecificProducer = string.Empty; // TODO: Allow IConfiguration settings.
        private int settingUpdateRate = 33; // Default to 33ms. TODO: Allow IConfiguration settings.

        public string LoadSpecificProducer => loadSpecificProducer;

        public PluginSystem(ILogger<PluginSystem> logger, params string[] args)
        {
            this.logger = logger;
            pluginLoadContexts = new List<PluginLoadContext>();
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
                            LogCommandLineHelpEntryValue("Producer", "Enables single producer mode where the given producer is the only one loaded", "SRTPluginProducerRE2");
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
                    case "PRODUCER":
                        {
                            loadSpecificProducer = kvp.Value;
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

                    foreach (KeyValuePair<PluginProducerStateValue, PluginUIStateValue[]> pluginKeys in pluginProducersAndDependentUIs)
                    {
                        if (pluginKeys.Key.Startup && pluginKeys.Key.Plugin.ProcessRunning) // Producer is started and process is running.
                        {
                            object pluginData = pluginKeys.Key.Plugin.PullData();
                            pluginKeys.Key.LastData = pluginData;
                            foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic.Concat(pluginKeys.Value))
                                PluginReceiveData(pluginUIStateValue, pluginData);
                        }
                        else if (pluginKeys.Key.Startup && !pluginKeys.Key.Plugin.ProcessRunning) // Producer is started and process is not running.
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
                throw;
            }
            catch (Exception ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
                throw;
            }
        }

        public async Task ReloadPlugins(CancellationToken cancellationToken)
        {
            // Don't (re-)initialize the plugins until the read operation completes.
            pluginReadEvent.Wait(cancellationToken);

            // Signal that we're (re-)initializing plugins to block reads until we're done.
            pluginReinitializeEvent.Reset();

            await StopPlugins(cancellationToken);
            await InitPlugins(cancellationToken);
            await StartPlugins(cancellationToken);

            pluginReinitializeEvent.Set();
        }


        private async Task InitPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                DirectoryInfo pluginsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "plugins"));

                // Create the folder if it is missing. We will eventually throw an exception due to no plugins exists but... yeah.
                if (!pluginsDir.Exists)
                    pluginsDir.Create();

                // (Re-)discover plugins.
                if (loadSpecificProducer == string.Empty)
                {
                    allPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Select((FileInfo pluginAssemblyFileInfo) =>
                        {
                            PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo.Directory);
                            pluginLoadContexts.Add(pluginLoadContext);
                            return LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                        })
                        .Where((Assembly pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly pluginAssembly) => CreatePlugins(pluginAssembly)).ToArray();
                }
                else
                {
                    allPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Where((FileInfo pluginAssemblyFileInfo) => !pluginAssemblyFileInfo.Name.Contains("Producer", StringComparison.InvariantCultureIgnoreCase) || (pluginAssemblyFileInfo.Name.Contains("Producer", StringComparison.InvariantCultureIgnoreCase) && pluginAssemblyFileInfo.Name.Equals(string.Format("{0}.dll", loadSpecificProducer), StringComparison.InvariantCultureIgnoreCase)))
                        .Select((FileInfo pluginAssemblyFileInfo) =>
                        {
                            PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo.Directory);
                            pluginLoadContexts.Add(pluginLoadContext);
                            return LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                        })
                        .Where((Assembly pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly pluginAssembly) => CreatePlugins(pluginAssembly)).ToArray();
                }

                if (allPlugins.Length == 0)
                    LogNoPlugins();

                pluginProducersAndDependentUIs = new Dictionary<PluginProducerStateValue, PluginUIStateValue[]>();
                foreach (IPluginProducer pluginProducer in allPlugins.Where(a => typeof(IPluginProducer).IsAssignableFrom(a.GetType())).Select(a => (IPluginProducer)a))
                    pluginProducersAndDependentUIs.Add(new PluginProducerStateValue() { Plugin = pluginProducer, Startup = false }, allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => new PluginUIStateValue() { Plugin = (IPluginUI)a, Startup = false }).Where(a => a.Plugin.RequiredProducer == pluginProducer.GetType().Name).ToArray());
                pluginUIsAgnostic = allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => new PluginUIStateValue() { Plugin = (IPluginUI)a, Startup = false }).Where(a => a.Plugin.RequiredProducer == null || a.Plugin.RequiredProducer == string.Empty).ToArray();
            }, cancellationToken);
        }

        private async Task StartPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Startup producers.
                foreach (KeyValuePair<PluginProducerStateValue, PluginUIStateValue[]> pluginKeys in pluginProducersAndDependentUIs)
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
                if (pluginUIsAgnostic != null)
                    foreach (PluginUIStateValue pluginUIStateValue in pluginUIsAgnostic)
                        PluginShutdown(pluginUIStateValue);

                if (pluginProducersAndDependentUIs != null)
                {
                    foreach (KeyValuePair<PluginProducerStateValue, PluginUIStateValue[]> pluginKeys in pluginProducersAndDependentUIs)
                    {
                        foreach (PluginUIStateValue pluginUI in pluginKeys.Value)
                            PluginShutdown(pluginUI);
                        PluginShutdown(pluginKeys.Key);
                    }
                }

                // Unload the load contexts.
                foreach (PluginLoadContext pluginLoadContext in pluginLoadContexts)
                    pluginLoadContext.Unload();

                // Clear the load contexts.
                pluginLoadContexts.Clear();
            }, cancellationToken);
        }

        private void PluginStartup<T>(IPluginStateValue<T> plugin) where T : IPlugin
        {
            if (!plugin.Startup)
            {
                int pluginStatusResponse = 0;
                try
                {
                    pluginStatusResponse = plugin.Plugin.Startup();

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

        private void PluginReceiveData<T>(IPluginStateValue<T> plugin, object pluginData) where T : IPluginUI
        {
            // If the UI plugin isn't started, start it now.
            if (!plugin.Startup)
                PluginStartup(plugin);

            if (pluginData != null)
            {
                int uiPluginReceiveDataStatus = 0;
                try
                {
                    uiPluginReceiveDataStatus = plugin.Plugin.ReceiveData(pluginData);

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
                    if (type.GetInterface(nameof(IPluginProducer)) != null)
                    {
                        IPluginProducer result = (IPluginProducer)Activator.CreateInstance(type); // If this throws an exception, the plugin may be targeting a different version of SRTPluginBase.
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
