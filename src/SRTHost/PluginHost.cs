using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using System.Text.RegularExpressions;
using System.Runtime.Loader;

namespace SRTHost
{
    public partial class PluginHost : IHostedService, IPluginHost
    {
		// Constants
		public const string APP_NAME = "SRT Host";
		public const string APP_ARCHITECTURE_X64 = "64-bit (x64)";
		public const string APP_ARCHITECTURE_X86 = "32-bit (x86)";
#if x64
		public const string APP_EXE_NAME = "SRTHost64.exe";
		public const string APP_ARCHITECTURE = APP_ARCHITECTURE_X64;
#else
        public const string APP_EXE_NAME = "SRTHost32.exe";
        public const string APP_ARCHITECTURE = APP_ARCHITECTURE_X86;
#endif
		public const string APP_DISPLAY_NAME = APP_NAME + " " + APP_ARCHITECTURE;

        private IDictionary<string, IPluginStateValue<IPlugin>> loadedPlugins = new Dictionary<string, IPluginStateValue<IPlugin>>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, IPluginStateValue<IPlugin>> LoadedPlugins => loadedPlugins.AsReadOnly();

        private HashSet<string> failedPlugins = new HashSet<string>();
        public IReadOnlySet<string> FailedPlugins => failedPlugins;

        public T? GetPluginReference<T>(string pluginName) where T : class, IPlugin
        {
            // If the plugin is not loaded, return default.
            if (!LoadedPlugins.ContainsKey(pluginName))
                return default;

            return LoadedPlugins[pluginName].Plugin as T;
        }

        // Misc. variables
        private readonly IServiceProvider serviceProvider;
        private readonly IConfiguration configuration;
        private readonly string? loadSpecificProducer = null; // TODO: Allow IConfiguration settings.
        private readonly Timer failedPluginRetryTimer;

        public PluginHost(ILogger<PluginHost> logger, IServiceProvider serviceProvider, IConfiguration configuration, params string[] args)
        {
            this.logger = logger;
            this.serviceProvider = serviceProvider;
            this.configuration = configuration;
            this.failedPluginRetryTimer = new Timer(RetryFailedPluginsAsync, FailedPlugins, 0, 5 * 1000);

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
                            return;
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

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            string appExePath = Path.Combine(AppContext.BaseDirectory, APP_EXE_NAME);
            LogLoadedHost(appExePath.Replace(AppContext.BaseDirectory, string.Empty));
            GetSigningInfo(appExePath);

            // Create plugins directory if it does not exist.
			DirectoryInfo pluginsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "plugins"));
			if (!pluginsDir.Exists)
				pluginsDir.Create();

			// Create .plugindb directory if it does not exist.
			DirectoryInfo pluginDbDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, ".plugindb"));
			if (!pluginDbDir.Exists)
				pluginDbDir.Create();

			// Initialize and start plugins.
			await InitPlugins(cancellationToken);

            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:DevelopmentHttp:Url"));
            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:DevelopmentHttps:Url"));
            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:ProductionHttp:Url"));
            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:ProductionHttps:Url"));
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await UnloadPlugins(cancellationToken);
        }

        [GeneratedRegex(@"^(?<Protocol>https?)://(?<Host>.*?):?(?<Port>\d+)?$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
        private static partial Regex RegexGetConfigurationURLDetails();

        private void ReportURL(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            Match urlMatch = RegexGetConfigurationURLDetails().Match(url);
            string? protocol = urlMatch.Groups["Protocol"].Success ? urlMatch.Groups["Protocol"].Value : default;
            string? host = urlMatch.Groups["Host"].Success ? urlMatch.Groups["Host"].Value : default;
            string? port = urlMatch.Groups["Port"].Success ? urlMatch.Groups["Port"].Value : default;
            
            if (string.Equals(host, "+") || string.Equals(host, "*"))
                host = Helpers.GetIPv4() ?? Helpers.GetIPv6();

            if (string.IsNullOrWhiteSpace(host))
                host = "localhost";

            string returnValue;
            if (string.IsNullOrWhiteSpace(port))
                returnValue = $"{protocol}://{host}";
            else
                returnValue = $"{protocol}://{host}:{port}";

            LogApplicationWebSeverURL(returnValue);
        }

        private void RetryFailedPluginsAsync(object? state)
        {
            foreach (string pluginName in FailedPlugins)
                InitPlugin(pluginName, CancellationToken.None).GetAwaiter().GetResult();
        }

		public async Task ReloadPlugin(string pluginName, CancellationToken cancellationToken)
        {
            await UnloadPlugin(pluginName, cancellationToken);
            await InitPlugin(pluginName, cancellationToken);
        }

        public async Task ReloadPlugins(CancellationToken cancellationToken)
        {
            await UnloadPlugins(cancellationToken);
            await InitPlugins(cancellationToken);
        }

        private async Task InitPlugin(string pluginName, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                DirectoryInfo pluginsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "plugins", pluginName));

                if (!pluginsDir.Exists)
                    throw new DirectoryNotFoundException($"{nameof(pluginName)} directory {pluginName} was not found in the plugins folder");

                // Load plugin.
                IEnumerable<IPluginStateValue<IPlugin>> pluginStateValues = pluginsDir
                .EnumerateFiles(string.Format("{0}.dll", pluginName), SearchOption.TopDirectoryOnly)
                .Select((FileInfo pluginAssemblyFileInfo) =>
                {
                    PluginLoadContext pluginLoadContext = new PluginLoadContext(pluginAssemblyFileInfo.Directory!);
                    Assembly? pluginAssembly = LoadPlugin(pluginLoadContext, pluginAssemblyFileInfo.FullName);
                    return ((pluginAssembly is not null) ? InstantiatePlugins(pluginLoadContext, pluginAssembly, pluginName, cancellationToken) : Enumerable.Empty<IPluginStateValue<IPlugin>>()).ToArray();
                })
                .SelectMany((IEnumerable<IPluginStateValue<IPlugin>> pluginStateValues) => pluginStateValues);

                foreach (IPluginStateValue<IPlugin> pluginStateValue in pluginStateValues.Where(psv => psv.IsInstantiated && psv.Plugin is not null))
                    loadedPlugins.Add(pluginStateValue.Plugin!.TypeName, pluginStateValue);

                if (loadedPlugins.Count == 0)
                    LogNoPlugins();
            }, cancellationToken);
        }

        private async Task InitPlugins(CancellationToken cancellationToken)
        {
			DirectoryInfo pluginsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "plugins"));

            // (Re-)discover plugins.
            if (!string.IsNullOrWhiteSpace(loadSpecificProducer))
                await InitPlugin(loadSpecificProducer!, cancellationToken);
            else
                foreach (DirectoryInfo pluginDir in pluginsDir.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                    await InitPlugin(pluginDir.Name, cancellationToken);

            if (loadedPlugins.Count == 0)
                LogNoPlugins();
        }

        private async Task UnloadPlugin(string pluginName, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                if (loadedPlugins.Remove(pluginName, out IPluginStateValue<IPlugin>? pluginStateValue) && pluginStateValue is not null)
                {
                    //pluginStateValue.Plugin.Dispose();
                    foreach (Assembly assembly in pluginStateValue.LoadContext.Assemblies)
                    {
                        try
                        {
                            PluginViewCompiler.Current?.UnloadModuleCompiledViews(assembly);
                        }
                        catch
                        {
                            throw;
                        }
                    }
                    pluginStateValue.LoadContext.Unload();
                }
            }, cancellationToken);
        }

        private async Task UnloadPlugin(PluginLoadContext plc, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                foreach (Assembly assembly in plc.Assemblies)
                {
                    try
                    {
                        PluginViewCompiler.Current?.UnloadModuleCompiledViews(assembly);
                    }
                    catch
                    {
                        throw;
                    }
                }
                plc.Unload();
            }, cancellationToken);
        }

        private async Task UnloadPlugins(CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                foreach (IPluginStateValue<IPlugin> pluginStateValue in loadedPlugins.Values)
                    await UnloadPlugin(pluginStateValue.LoadContext, cancellationToken);

                loadedPlugins.Clear();
            }, cancellationToken);
        }


        private Assembly? LoadPlugin(PluginLoadContext loadContext, string pluginPath)
        {
            Assembly? returnValue = null;

            try
            {
                returnValue = loadContext.LoadFromAssemblyPath(pluginPath);
                PluginViewCompiler.Current?.LoadModuleCompiledViews(returnValue);
				//LogLoadedPlugin(pluginPath.Replace(AppContext.BaseDirectory, string.Empty));
                //GetSigningInfo(pluginPath);
                //LogPluginVersion(FileVersionInfo.GetVersionInfo(pluginPath).ProductVersion);
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

        private IEnumerable<PluginStateValue<IPlugin>> InstantiatePlugins(PluginLoadContext plc, Assembly assembly, string pluginName, CancellationToken cancellationToken)
        {
            Type[]? typesInAssembly = null;

            try
            {
                typesInAssembly = assembly.GetTypes();
            }
            catch (Exception ex)
            {
                LogException(ex?.GetType()?.Name, ex?.ToString());
                typesInAssembly = null;
            }

            if (typesInAssembly != null)
            {
                List<PluginStateValue<IPlugin>> plugins = new List<PluginStateValue<IPlugin>>();
                try
                {
                    foreach (Type type in typesInAssembly)
                    {
                        if (type.GetInterface(nameof(IPluginProducer)) != null)
                        {
                            IPluginProducer result = (IPluginProducer)Activator.CreateInstance(type, CreatePluginCtorArgs(type))!; // If this throws an exception, the plugin may be targeting a different version of SRTPluginBase.
                            plugins.Add(new PluginStateValue<IPlugin>(plc, true, result));
                        }
                        else if (type.GetInterface(nameof(IPluginConsumer)) != null)
                        {
                            IPluginConsumer result = (IPluginConsumer)Activator.CreateInstance(type, CreatePluginCtorArgs(type))!;
                            plugins.Add(new PluginStateValue<IPlugin>(plc, true, result));
                        }
                        else if (type.GetInterface(nameof(IPlugin)) != null)
                        {
                            IPlugin result = (IPlugin)Activator.CreateInstance(type, CreatePluginCtorArgs(type))!;
                            plugins.Add(new PluginStateValue<IPlugin>(plc, true, result));
                        }
                    }
                    LogLoadedPlugin(assembly.Location.Replace(AppContext.BaseDirectory, string.Empty));
                    GetSigningInfo(assembly.Location);
                    LogPluginVersion(FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion);
                    return plugins;
                }
                catch (Exception ex) when (ex is TargetInvocationException or PluginInitializationException or PluginNotFoundException)
                {
                    failedPlugins.Add(pluginName);
                    UnloadPlugin(plc, cancellationToken).GetAwaiter().GetResult();
                }
            }

            return Enumerable.Empty<PluginStateValue<IPlugin>>();
        }

        private object?[]? CreatePluginCtorArgs(Type type)
        {
            object?[]? args = null;
            ParameterInfo[]? ctorArgTypes = type.GetConstructors().FirstOrDefault()?.GetParameters();
            if (ctorArgTypes is not null && ctorArgTypes!.Length > 0)
            {
                args = new object?[ctorArgTypes!.Length];
                for (int i = 0; i < args.Length; ++i)
                    args[i] = serviceProvider.GetService(ctorArgTypes![i].ParameterType);
            }
            return args;
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
