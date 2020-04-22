using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace SRTHost
{
    public static class Program
    {
        public static bool running = true;
        private static PluginHostDelegates hostDelegates = new PluginHostDelegates();

        //[STAThread]
        public static async Task Main()
        {
            Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) =>
            {
                e.Cancel = true;
                running = false;
            };

            IPlugin[] allPlugins = null;
            IPluginProvider providerPlugin = null;
            IPluginUI[] uiPlugins = null;
            try
            {
                ShowSigningInfo(Assembly.GetExecutingAssembly(), false);
                allPlugins = new DirectoryInfo("plugins")
                    .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                    .Select((DirectoryInfo pluginDir) => pluginDir.EnumerateFiles(string.Format("{0}.dll", pluginDir.Name), SearchOption.TopDirectoryOnly).First())
                    .Select(a => a.FullName)
                    .SelectMany((string pluginPath) =>
                    {
                        Assembly pluginAssembly = LoadPlugin(pluginPath);
                        ShowSigningInfo(pluginAssembly);
                        return CreatePlugins(pluginAssembly);
                    }).ToArray();
                Console.WriteLine();

                if (allPlugins.Count(a => typeof(IPluginProvider).IsAssignableFrom(a.GetType())) > 1)
                    Environment.Exit(1); // Critical error. Handle better. Only one provider allowed.

                providerPlugin = (IPluginProvider)allPlugins.First(a => typeof(IPluginProvider).IsAssignableFrom(a.GetType()));
                uiPlugins = allPlugins.Where(a => typeof(IPluginUI).IsAssignableFrom(a.GetType())).Select(a => (IPluginUI)a).ToArray();

                // Startup.
                foreach (IPlugin plugin in allPlugins)
                {
                    int pluginStartupStatus = 0;
                    try
                    {
                        pluginStartupStatus = plugin.Startup(hostDelegates);

                        if (pluginStartupStatus == 0)
                            Console.WriteLine("[{0} v{1}.{2}.{3}.{4}] successfully started.", plugin.Info.Name, plugin.Info.VersionMajor, plugin.Info.VersionMinor, plugin.Info.VersionBuild, plugin.Info.VersionRevision);
                        else
                            Console.WriteLine("[{0} v{1}.{2}.{3}.{4}] failed to start properly with status {5}.", plugin.Info.Name, plugin.Info.VersionMajor, plugin.Info.VersionMinor, plugin.Info.VersionBuild, plugin.Info.VersionRevision, pluginStartupStatus);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[{0}] {1}", ex.GetType().Name, ex.ToString());
                    }
                }

                Console.WriteLine("Press CTRL+C in this console window to shutdown the SRT.");
                while (running)
                {
                    object gameMemory = providerPlugin.PullData();
                    if (gameMemory != null)
                    {
                        foreach (IPluginUI uiPlugin in uiPlugins)
                        {
                            int uiPluginReceiveDataStatus = 0;
                            try
                            {
                                uiPluginReceiveDataStatus = uiPlugin.ReceiveData(gameMemory);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("[{0}] {1}", ex.GetType().Name, ex.ToString());
                            }
                        }
                    }
                    await Task.Delay(16).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[{0}] {1}", ex.GetType().Name, ex.ToString());
            }
            finally
            {
                // Shutdown.
                foreach (IPlugin plugin in allPlugins)
                {
                    int pluginShutdownStatus = 0;
                    try
                    {
                        pluginShutdownStatus = plugin.Shutdown();
                        if (pluginShutdownStatus == 0)
                            Console.WriteLine("[{0} v{1}.{2}.{3}.{4}] successfully shutdown.", plugin.Info.Name, plugin.Info.VersionMajor, plugin.Info.VersionMinor, plugin.Info.VersionBuild, plugin.Info.VersionRevision);
                        else
                            Console.WriteLine("[{0} v{1}.{2}.{3}.{4}] failed to stop properly with status {5}.", plugin.Info.Name, plugin.Info.VersionMajor, plugin.Info.VersionMinor, plugin.Info.VersionBuild, plugin.Info.VersionRevision, pluginShutdownStatus);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[{0}] {1}", ex.GetType().Name, ex.ToString());
                    }
                }
            }
        }

        private static PluginLoadContext loadContext = new PluginLoadContext(Environment.CurrentDirectory + Path.DirectorySeparatorChar);
        private static Assembly LoadPlugin(string relativePath)
        {
            try
            {
                // Navigate up to the solution root
                string root = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(
                        Path.GetDirectoryName(
                            Path.GetDirectoryName(
                                Path.GetDirectoryName(
                                    Path.GetDirectoryName(typeof(Program).Assembly.Location)))))));

                string pluginLocation = Path.GetFullPath(Path.Combine(root, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
                return loadContext.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(pluginLocation)));
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private static IEnumerable<IPlugin> CreatePlugins(Assembly assembly)
        {
            int count = 0;
            Type[] typesInAssembly = null;

            try
            {
                typesInAssembly = assembly.GetTypes();
            }
            catch (Exception ex)
            {
                yield break;
            }

            if (typesInAssembly != null)
            {
                foreach (Type type in typesInAssembly)
                {
                    if (typeof(IPluginProvider).IsAssignableFrom(type))
                    {
                        IPluginProvider result = Activator.CreateInstance(type) as IPluginProvider;
                        if (result != null)
                        {
                            count++;
                            yield return result;
                        }
                    }
                    else if (typeof(IPluginUI).IsAssignableFrom(type))
                    {
                        IPluginUI result = Activator.CreateInstance(type) as IPluginUI;
                        if (result != null)
                        {
                            count++;
                            yield return result;
                        }
                    }
                    else if (typeof(IPlugin).IsAssignableFrom(type))
                    {
                        IPlugin result = Activator.CreateInstance(type) as IPlugin;
                        if (result != null)
                        {
                            count++;
                            yield return result;
                        }
                    }
                }
            }
        }

        public static void ShowSigningInfo(Assembly assembly, bool isPlugin = true)
        {
            Console.WriteLine("Loaded {0}: {1}", (isPlugin) ? "plugin" : "host", Path.GetRelativePath(Environment.CurrentDirectory, assembly?.Location));

            X509Certificate2 cert2;
            if ((cert2 = loadContext.GetSigningInfo2(assembly)) != null)
            {
                if (cert2.Verify())
                    Console.WriteLine("\tDigitally signed and verified: {0} [Thumbprint: {1}]", cert2.GetNameInfo(X509NameType.SimpleName, false), cert2.Thumbprint);
                else
                    Console.WriteLine("\tDigitally signed but NOT verified: {0} [Thumbprint: {1}]", cert2.GetNameInfo(X509NameType.SimpleName, false), cert2.Thumbprint);
            }
            else
                Console.WriteLine("\tNo digital signature found.");
        }
    }
}
