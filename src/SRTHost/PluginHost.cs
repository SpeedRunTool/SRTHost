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
using System.Drawing.Text;
using System.Runtime.CompilerServices;

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

        private IDictionary<string, PluginStateValue<IPlugin>> loadedPlugins = new Dictionary<string, PluginStateValue<IPlugin>>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, PluginStateValue<IPlugin>> LoadedPlugins => loadedPlugins.AsReadOnly();

        //private HashSet<string> failedPlugins = new HashSet<string>();
        //public IReadOnlySet<string> FailedPlugins => failedPlugins;

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
        //private readonly Timer failedPluginRetryTimer;

        public PluginHost(ILogger<PluginHost> logger, IServiceProvider serviceProvider, IConfiguration configuration, params string[] args)
        {
            this.logger = logger;
            this.serviceProvider = serviceProvider;
            this.configuration = configuration;
            //this.failedPluginRetryTimer = new Timer(RetryFailedPluginsAsync, FailedPlugins, 0, 5 * 1000);

            FileVersionInfo srtHostFileVersionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, APP_EXE_NAME));
            LogVersionBanner(srtHostFileVersionInfo.ProductName, srtHostFileVersionInfo.ProductVersion, APP_ARCHITECTURE);

            foreach (KeyValuePair<string, string?> kvp in new CommandLineProcessor(args))
            {
                LogCommandLineBanner();
                if (kvp.Value is not null)
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
            await foreach (PluginStateValue<IPlugin> pluginStateValue in LoadPluginsAsync(cancellationToken))
                await InitializeAsync(pluginStateValue, cancellationToken);

            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:DevelopmentHttp:Url"));
            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:DevelopmentHttps:Url"));
            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:ProductionHttp:Url"));
            ReportURL(configuration.GetValue<string>("Kestrel:Endpoints:ProductionHttps:Url"));
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await UnloadPluginsAsync(cancellationToken);
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

        private FileInfo GetPluginFileInfoByName(string pluginName) => new FileInfo(Path.Combine(Directory.GetCurrentDirectory(), "plugins", pluginName, $"{pluginName}.dll"));

        public async IAsyncEnumerable<PluginStateValue<IPlugin>> LoadPluginsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string pluginName in GetPluginNames())
                yield return await LoadPluginAsync(pluginName, cancellationToken);
        }

        public async Task<PluginStateValue<IPlugin>> LoadPluginAsync(string pluginName, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                PluginStateValue<IPlugin> pluginStateValue = new PluginStateValue<IPlugin>();

                FileInfo pluginFileInfo = GetPluginFileInfoByName(pluginName);
                if (pluginFileInfo.Exists)
                {
                    try
                    {
                        pluginStateValue.LoadContext = new PluginLoadContext(pluginFileInfo.Directory!);
                        Assembly? pluginAssembly = pluginStateValue.LoadContext.LoadFromAssemblyPath(pluginFileInfo.FullName);
                        pluginStateValue.Status = PluginStatusEnum.Loaded;
                        pluginStateValue.SubStatus = PluginSubStatusEnum.None;
                        pluginStateValue.PluginType = GetPluginType(pluginAssembly);
                        PluginViewCompiler.Current?.LoadModuleCompiledViews(pluginAssembly);
                        LogLoadedPlugin(pluginName);
                        GetSigningInfo(pluginFileInfo.FullName);
                        LogPluginVersion(FileVersionInfo.GetVersionInfo(pluginFileInfo.FullName).ProductVersion);
                    }
#pragma warning disable CS0168 // Variable is declared but never used
                    catch (FileLoadException ex)
#pragma warning restore CS0168 // Variable is declared but never used
                    {
                        pluginStateValue.Status = PluginStatusEnum.LoadingError;
                        pluginStateValue.SubStatus = PluginSubStatusEnum.IncorrectArchitecture;
                        LogIncorrectArchitecturePlugin(pluginFileInfo.FullName);
                        GetSigningInfo(pluginFileInfo.FullName);
                        LogPluginVersion(FileVersionInfo.GetVersionInfo(pluginFileInfo.FullName).ProductVersion);
                    }
                    catch (Exception ex)
                    {
                        pluginStateValue.Status = PluginStatusEnum.LoadingError;
                        pluginStateValue.SubStatus = PluginSubStatusEnum.None;
                        LogException(ex?.GetType()?.Name, ex?.ToString());
                    }
                }

                return pluginStateValue;
            }, cancellationToken);
        }

        private Type? GetPluginType(Assembly pluginAssembly)
        {
            Type[] pluginTypes = pluginAssembly.GetTypes();

            foreach (Type pluginType in pluginTypes)
                if (
                    pluginType.GetInterface(nameof(IPluginProducer)) is not null ||
                    pluginType.GetInterface(nameof(IPluginConsumer)) is not null ||
                    pluginType.GetInterface(nameof(IPlugin)) is not null
                    )
                    return pluginType;

            return default;
        }

        public Task<PluginStateValue<IPlugin>> InitializeAsync(PluginStateValue<IPlugin> pluginStateValue, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                switch (pluginStateValue.Status)
                {
                    case PluginStatusEnum.NotLoaded: // Not loaded, there is nothing to instantiate.
                    case PluginStatusEnum.LoadingError: // Loading error, retry loading.
                    case PluginStatusEnum.Instantiated: // Instantiated, we're already running.
                        return pluginStateValue; // Invalid state to continue;

                    case PluginStatusEnum.Loaded: // Loaded, normal condition.
                    case PluginStatusEnum.InstantiationError: // Instantiation error, we're likely retrying this plugin.
                        break;
                }

                object? pluginInstance = default;
                try
                {
                    pluginInstance = Activator.CreateInstance(pluginStateValue.PluginType!, CreatePluginCtorArgs(pluginStateValue.PluginType!.Name, pluginStateValue.PluginType!))!; // If this throws an exception, the plugin may be targeting a different version of SRTPluginBase.
                    if (pluginInstance is not null)
                    {
                        pluginStateValue.Status = PluginStatusEnum.Instantiated;
                        pluginStateValue.SubStatus = PluginSubStatusEnum.None;
                    }
                    else
                    {
                        pluginStateValue.Status = PluginStatusEnum.InstantiationError;
                        pluginStateValue.SubStatus = PluginSubStatusEnum.None;
                    }
                }
                catch (TargetInvocationException ex) when (ex.InnerException is PluginInitializationException)
                {
                    pluginStateValue.Status = PluginStatusEnum.InstantiationError;
                    pluginStateValue.SubStatus = PluginSubStatusEnum.PluginInitializationException;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is PluginNotFoundException)
                {
                    pluginStateValue.Status = PluginStatusEnum.InstantiationError;
                    pluginStateValue.SubStatus = PluginSubStatusEnum.PluginNotFoundException;
                }
                catch
                {
                    pluginStateValue.Status = PluginStatusEnum.InstantiationError;
                    pluginStateValue.SubStatus = PluginSubStatusEnum.UndefinedException;
                    throw;
                }

                if (pluginInstance is IPluginProducer)
                    pluginStateValue.Plugin = (IPluginProducer)pluginInstance;
                else if (pluginInstance is IPluginConsumer)
                    pluginStateValue.Plugin = (IPluginConsumer)pluginInstance;
                else if (pluginInstance is IPlugin)
                    pluginStateValue.Plugin = (IPlugin)pluginInstance;

                loadedPlugins.Add(pluginStateValue.PluginType!.Name, pluginStateValue);
                return pluginStateValue;
            }, cancellationToken);
        }

        private object?[]? CreatePluginCtorArgs(string pluginName, Type type)
        {
            object?[]? args = null;
            ParameterInfo[]? ctorArgTypes = type.GetConstructors().FirstOrDefault()?.GetParameters();
            if (ctorArgTypes is not null && ctorArgTypes!.Length > 0)
            {
                args = new object?[ctorArgTypes!.Length];
                for (int i = 0; i < args.Length; ++i)
                {
                    try
                    {
                        args[i] = serviceProvider.GetService(ctorArgTypes![i].ParameterType);
                        if (args[i] is null)
                            args[i] = Activator.CreateInstance(ctorArgTypes![i].ParameterType);
                    }
                    catch (Exception ex)
                    {
                        LogLoadPluginUnableToCreateCtorArgsType(pluginName, ctorArgTypes![i].ParameterType, ex);
                    }

                    if (args[i] is null)
                        LogLoadPluginUnableToCreateCtorArgsType(pluginName, ctorArgTypes![i].ParameterType, default);
                }
            }
            return args;
        }

        public async Task<PluginStateValue<IPlugin>?> UnloadPluginAsync(string pluginName, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                PluginStateValue<IPlugin>? pluginStateValue;
                if (loadedPlugins.Remove(pluginName, out pluginStateValue))
                {
                    if (pluginStateValue.Status == PluginStatusEnum.NotLoaded || pluginStateValue.Status == PluginStatusEnum.LoadingError)
                        return pluginStateValue;

                    foreach (Assembly assembly in pluginStateValue.LoadContext!.Assemblies)
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
                    pluginStateValue.Status = PluginStatusEnum.NotLoaded;
                    pluginStateValue.SubStatus = PluginSubStatusEnum.None;
                }

                return pluginStateValue;
            }, cancellationToken);
        }

        public async Task UnloadPluginsAsync(CancellationToken cancellationToken)
        {
            await Task.Run(async () =>
            {
                foreach (string pluginName in loadedPlugins.Keys.ToArray())
                    await UnloadPluginAsync(pluginName, cancellationToken);
            }, cancellationToken);
        }

        private IEnumerable<string> GetPluginNames() => new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "plugins")).EnumerateDirectories("*", SearchOption.TopDirectoryOnly).SelectMany(d => d.EnumerateFiles($"{d.Name}.dll", SearchOption.TopDirectoryOnly)).Select(f => Path.GetFileNameWithoutExtension(f.Name));

        public async Task ReloadPluginAsync(string pluginName, CancellationToken cancellationToken)
        {
            await UnloadPluginAsync(pluginName, cancellationToken);
            PluginStateValue<IPlugin> pluginStateValue;
            if ((pluginStateValue = await LoadPluginAsync(pluginName, cancellationToken)) is not null)
                await InitializeAsync(pluginStateValue, cancellationToken);
        }

        public async Task ReloadPluginsAsync(CancellationToken cancellationToken)
        {
            foreach (string pluginName in GetPluginNames())
                await ReloadPluginAsync(pluginName, cancellationToken);
        }

        private void GetSigningInfo(string location)
        {
            X509Certificate2? cert2;
            if ((cert2 = SigningInfo.GetSigningInfo2(location)) is not null)
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
