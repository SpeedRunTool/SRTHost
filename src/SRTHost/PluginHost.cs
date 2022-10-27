﻿using Microsoft.Extensions.Hosting;
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

        private IDictionary<string, IPluginStateValue<IPlugin>> loadedPlugins;
        public IReadOnlyDictionary<string, IPluginStateValue<IPlugin>> LoadedPlugins => loadedPlugins.AsReadOnly();

        public T? GetPluginReference<T>(string pluginName) where T : class, IPlugin
        {
            // If the plugin is not loaded, return default.
            if (!LoadedPlugins.ContainsKey(pluginName))
                return default;

            return LoadedPlugins[pluginName].Plugin as T;
        }

        // Misc. variables
        private readonly string? loadSpecificProducer = null; // TODO: Allow IConfiguration settings.
        private readonly int settingUpdateRate = 33; // Default to 33ms. TODO: Allow IConfiguration settings.

        public PluginHost(ILogger<PluginHost> logger, params string[] args)
        {
            this.logger = logger;

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
            }, cancellationToken);

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await UnloadPlugins(cancellationToken);
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
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
            await UnloadPlugins(cancellationToken);
            await InitPlugins(cancellationToken);
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
                    loadedPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo? pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Select((FileInfo? pluginAssemblyFileInfo) =>
                        {
                            PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo!.Directory!);
                            Assembly? pluginAssembly = LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                            return (pluginAssembly is not null) ? CreatePlugins(pluginAssembly).Select((IPlugin plugin) => new PluginStateValue<IPlugin>(pluginLoadContext, plugin)) : Enumerable.Empty<IPluginStateValue<IPlugin>>();
                        })
                        .SelectMany((IEnumerable<IPluginStateValue<IPlugin>> pluginStateValues) => pluginStateValues)
                        .ToDictionary((IPluginStateValue<IPlugin> pluginStateValue) => pluginStateValue.Plugin.TypeName, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    loadedPlugins = pluginsDir
                        .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                        .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).FirstOrDefault())
                        .Where((FileInfo? pluginAssemblyFileInfo) => pluginAssemblyFileInfo != null)
                        .Where((FileInfo? pluginAssemblyFileInfo) => !pluginAssemblyFileInfo!.Name.Contains("Producer", StringComparison.InvariantCultureIgnoreCase) || (pluginAssemblyFileInfo!.Name.Contains("Producer", StringComparison.InvariantCultureIgnoreCase) && pluginAssemblyFileInfo!.Name.Equals(string.Format("{0}.dll", loadSpecificProducer), StringComparison.InvariantCultureIgnoreCase)))
                        .Select((FileInfo? pluginAssemblyFileInfo) =>
                        {
                            PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo!.Directory!);
                            Assembly? pluginAssembly = LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                            return (pluginAssembly is not null) ? CreatePlugins(pluginAssembly).Select((IPlugin plugin) => new PluginStateValue<IPlugin>(pluginLoadContext, plugin)) : Enumerable.Empty<IPluginStateValue<IPlugin>>();
                        })
                        .SelectMany((IEnumerable<IPluginStateValue<IPlugin>> pluginStateValues) => pluginStateValues)
                        .ToDictionary((IPluginStateValue<IPlugin> pluginStateValue) => pluginStateValue.Plugin.TypeName, StringComparer.OrdinalIgnoreCase);
                }

                if (loadedPlugins.Count == 0)
                    LogNoPlugins();
            }, cancellationToken);
        }

        private async Task UnloadPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                foreach (IPluginStateValue<IPlugin> pluginStateValue in loadedPlugins.Values)
                {
                    pluginStateValue.Plugin.Dispose();
                    pluginStateValue.LoadContext.Unload();
                }

                loadedPlugins.Clear();
            }, cancellationToken);
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