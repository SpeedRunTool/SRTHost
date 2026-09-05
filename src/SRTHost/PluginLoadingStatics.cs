using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SRTHost
{
    public static class PluginLoadingStatics
    {
        public static Assembly LoadPlugin(PluginLoadContext loadContext, string pluginPath)
        {
            Assembly returnValue = null;

            try
            {
                returnValue = loadContext.LoadFromAssemblyPath(pluginPath);
                Console.WriteLine("  Loaded plugin: {0}", Path.GetRelativePath(Environment.CurrentDirectory, pluginPath));
                Program.ShowSigningInfo(pluginPath);
                Program.ShowVersionInfo(pluginPath);
            }
            catch (FileLoadException ex)
            {
                Program.HandleIncorrectArchitecture(pluginPath);
                Program.ShowSigningInfo(pluginPath);
                Program.ShowVersionInfo(pluginPath);
            }
            catch (Exception ex)
            {
                Program.HandleException(ex);
            }

            return returnValue;
        }

        public static IEnumerable<IPlugin> CreatePlugins(Assembly assembly)
        {
            int count = 0;
            Type[] typesInAssembly = null;

            try
            {
                typesInAssembly = assembly.GetTypes();
            }
            catch (Exception ex)
            {
                Program.HandleException(ex);
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
    }
}
