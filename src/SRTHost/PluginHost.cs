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
    public partial class PluginHost : BackgroundService, IHostedService, IPluginHost
    {
        // Constants
        private const string APP_NAME = "SRT Host";
        private const string APP_ARCHITECTURE_X64 = "64-bit (x64)";
        private const string APP_ARCHITECTURE_X86 = "32-bit (x86)";
#if x64
        private const string APP_EXE_NAME = "SRTHost64.exe";
        private const string APP_ARCHITECTURE = APP_ARCHITECTURE_X64;
#else
        private const string APP_EXE_NAME = "SRTHost32.exe";
        private const string APP_ARCHITECTURE = APP_ARCHITECTURE_X86;
#endif
        private const string APP_DISPLAY_NAME = APP_NAME + " " + APP_ARCHITECTURE;

        // *** IPluginHost
        private IDictionary<string, IPlugin> LoadedPlugins = new Dictionary<string, IPlugin>(StringComparer.OrdinalIgnoreCase);

        public T? GetPluginReference<T>(string pluginName) where T : class, IPlugin
        {
            // If the plugin is not loaded, return default.
            if (!LoadedPlugins.ContainsKey(pluginName))
                return default;

            return LoadedPlugins[pluginName] as T;
        }
        // *** IPluginHost

        // Misc. variables
        private readonly IList<PluginLoadContext> pluginLoadContexts;
        private readonly ManualResetEventSlim pluginReinitializeEvent;
        private readonly ManualResetEventSlim pluginReadEvent;
        private readonly string? loadSpecificProducer = null; // TODO: Allow IConfiguration settings.
        private readonly int settingUpdateRate = 33; // Default to 33ms. TODO: Allow IConfiguration settings.

        // Plugins
        private IDictionary<string, IPlugin> allPlugins = new Dictionary<string, IPlugin>(); // What was said on the next line but with more than just string. Multiple dictionaries? idk...
        private IDictionary<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>> pluginProducersAndDependentConsumers = new Dictionary<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>>(); // TODO: Make a collection where plugins can be looked up by their name or type without per-lookup reflection. For example, build collection with these details at plugin load.
        private IList<PluginConsumerStateValue> pluginConsumersAgnostic = new List<PluginConsumerStateValue>(); // ^^^

        /// <summary>
        /// All plugins which are currently loaded by the plugin system.
        /// </summary>
        public IReadOnlyDictionary<string, IPlugin> Plugins => new ReadOnlyDictionary<string, IPlugin>(allPlugins);

        /// <summary>
        /// All plugin producers and their dependent plugin Consumers which are currently loaded by the plugin system.
        /// </summary>
        public IReadOnlyDictionary<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>> PluginProducersAndDependentConsumers => new ReadOnlyDictionary<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>>(pluginProducersAndDependentConsumers);

        /// <summary>
        /// All plugin Consumers that are not dependent on a specific plugin producer which are currently loaded by the plugin system.
        /// </summary>
        public IReadOnlyCollection<PluginConsumerStateValue> PluginConsumersAgnostic => new ReadOnlyCollection<PluginConsumerStateValue>(pluginConsumersAgnostic);

        /// <summary>
        /// The specific plugin provider that was loaded. This will be null if all plugins are loaded. This value is read-only and provided via the --Provider command-line argument.
        /// </summary>
        public string? LoadSpecificProducer => loadSpecificProducer;

        public PluginHost(ILogger<PluginHost> logger, params string[] args)
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

                    foreach (KeyValuePair<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>> pluginKeys in pluginProducersAndDependentConsumers)
                    {
                        if (pluginKeys.Key.Startup && pluginKeys.Key.Plugin.Available) // Producer is started and available for requests.
                        {
                            object? pluginData = pluginKeys.Key.Plugin.PullData();
                            pluginKeys.Key.LastData = pluginData;
                            foreach (PluginConsumerStateValue pluginConsumerStateValue in pluginConsumersAgnostic.Concat(pluginKeys.Value))
                                PluginReceiveData(pluginConsumerStateValue, pluginData);
                        }
                        else if (pluginKeys.Key.Startup && !pluginKeys.Key.Plugin.Available) // Producer is started but is not available for requests.
                        {
                            // Loop through this plugin's Consumers and shut them down if they're running. Only shuts down dependent Consumers. Agnostic Consumers such as JSON shouldn't be touched.
                            foreach (PluginConsumerStateValue pluginConsumerStateValue in pluginKeys.Value)
                                if (pluginConsumerStateValue.Startup)
                                    PluginShutdown(pluginConsumerStateValue);
                        }
                    }

                    pluginReadEvent.Set();

                    try { await Task.Delay(settingUpdateRate, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                LogAppShutdown(APP_DISPLAY_NAME);
            }
            catch (FileLoadException ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
                LogIncorrectArchitecturePluginReference(ex?.Source, ex?.FileName);
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
                if (string.IsNullOrWhiteSpace(loadSpecificProducer))
                {
                    allPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo? pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Select((FileInfo? pluginAssemblyFileInfo) =>
                        {
                            PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo!.Directory!);
                            pluginLoadContexts.Add(pluginLoadContext);
                            return LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                        })
                        .Where((Assembly? pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly? pluginAssembly) => CreatePlugins(pluginAssembly!))
                        .ToDictionary((IPlugin plugin) => plugin.TypeName, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    allPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo? pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Where((FileInfo? pluginAssemblyFileInfo) => !pluginAssemblyFileInfo!.Name.Contains("Producer", StringComparison.InvariantCultureIgnoreCase) || (pluginAssemblyFileInfo!.Name.Contains("Producer", StringComparison.InvariantCultureIgnoreCase) && pluginAssemblyFileInfo!.Name.Equals(string.Format("{0}.dll", loadSpecificProducer), StringComparison.InvariantCultureIgnoreCase)))
                        .Select((FileInfo? pluginAssemblyFileInfo) =>
                        {
                            PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo!.Directory!);
                            pluginLoadContexts.Add(pluginLoadContext);
                            return LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                        })
                        .Where((Assembly? pluginAssembly) => pluginAssembly != null)
                        .SelectMany((Assembly? pluginAssembly) => CreatePlugins(pluginAssembly!))
                        .ToDictionary((IPlugin plugin) => plugin.TypeName, StringComparer.OrdinalIgnoreCase);
                }

                if (allPlugins.Count == 0)
                    LogNoPlugins();

                pluginProducersAndDependentConsumers = new Dictionary<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>>();
                foreach (IPluginProducer pluginProducer in allPlugins.Where(a => typeof(IPluginProducer).IsAssignableFrom(a.Value.GetType())).Select(a => (IPluginProducer)a.Value))
                    pluginProducersAndDependentConsumers.Add(new PluginProducerStateValue(pluginProducer, false), allPlugins.Where(a => typeof(IPluginConsumer).IsAssignableFrom(a.GetType())).Select(a => new PluginConsumerStateValue((IPluginConsumer)a.Value, false)).Where(a => a.Plugin.RequiredProducer == pluginProducer.TypeName).ToArray());
                pluginConsumersAgnostic = allPlugins.Where(a => typeof(IPluginConsumer).IsAssignableFrom(a.GetType())).Select(a => new PluginConsumerStateValue((IPluginConsumer)a.Value, false)).Where(a => string.IsNullOrWhiteSpace(a.Plugin.RequiredProducer)).ToArray();
            }, cancellationToken);
        }

        private async Task StartPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                // Startup producers.
                foreach (KeyValuePair<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>> pluginKeys in pluginProducersAndDependentConsumers)
                    PluginStartup(pluginKeys.Key);

                // Startup agnotic Consumers.
                foreach (PluginConsumerStateValue pluginConsumerStateValue in pluginConsumersAgnostic)
                    PluginStartup(pluginConsumerStateValue);
            }, cancellationToken);
        }

        private async Task StopPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                if (pluginConsumersAgnostic != null)
                    foreach (PluginConsumerStateValue pluginConsumerStateValue in pluginConsumersAgnostic)
                        PluginShutdown(pluginConsumerStateValue);

                if (pluginProducersAndDependentConsumers != null)
                {
                    foreach (KeyValuePair<PluginProducerStateValue, IReadOnlyCollection<PluginConsumerStateValue>> pluginKeys in pluginProducersAndDependentConsumers)
                    {
                        foreach (PluginConsumerStateValue pluginConsumer in pluginKeys.Value)
                            PluginShutdown(pluginConsumer);
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
                try
                {
                    int pluginStatusResponse = plugin.Plugin.Startup();

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

        private void PluginReceiveData<T>(IPluginStateValue<T> plugin, object? pluginData) where T : IPluginConsumer
        {
            // If the Consumer plugin isn't started, start it now.
            if (!plugin.Startup)
                PluginStartup(plugin);

            if (pluginData is not null)
            {
                try
                {
                    int uiPluginReceiveDataStatus = plugin.Plugin.ReceiveData(pluginData);

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
                try
                {
                    int pluginStatusResponse = plugin.Plugin.Shutdown();

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

        private Assembly? LoadPlugin(PluginLoadContext loadContext, string pluginPath)
        {
            Assembly? returnValue = null;

            try
            {
                returnValue = loadContext.LoadFromAssemblyPath(pluginPath);
                LogLoadedPlugin(pluginPath.Replace(AppContext.BaseDirectory, string.Empty));
                GetSigningInfo(pluginPath);
                LogPluginVersion(FileVersionInfo.GetVersionInfo(pluginPath).ProductVersion);
            }
#pragma warning disable CS0168 // Variable is declared but never used
            catch (FileLoadException ex)
#pragma warning restore CS0168 // Variable is declared but never used
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
            Type[]? typesInAssembly = null;

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
                        IPluginProducer result = (IPluginProducer)Activator.CreateInstance(type)!; // If this throws an exception, the plugin may be targeting a different version of SRTPluginBase.
                        count++;
                        yield return result;
                    }
                    else if (type.GetInterface(nameof(IPluginConsumer)) != null)
                    {
                        IPluginConsumer result = (IPluginConsumer)Activator.CreateInstance(type)!;
                        count++;
                        yield return result;
                    }
                    else if (type.GetInterface(nameof(IPlugin)) != null)
                    {
                        IPlugin result = (IPlugin)Activator.CreateInstance(type)!;
                        count++;
                        yield return result;
                    }
                }
            }
        }

        private void GetSigningInfo(string location)
        {
            X509Certificate2? cert2;
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
