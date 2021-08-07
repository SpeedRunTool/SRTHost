using SRTPluginBase;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace SRTHost
{
    public class PluginLoadContext : AssemblyLoadContext
    {
        private DirectoryInfo pluginDirectory;
        private AssemblyDependencyResolver _pluginResolver;

        public PluginLoadContext(string pluginDirectory)
        {
            this.pluginDirectory = new DirectoryInfo(pluginDirectory);
            this._pluginResolver = new AssemblyDependencyResolver(this.pluginDirectory.FullName);

            base.Resolving += PluginLoadContext_Resolving;
        }

        private Assembly PluginLoadContext_Resolving(AssemblyLoadContext assemblyLoadContext, AssemblyName assemblyName)
        {
            return Load(assemblyName);
        }

        private string? DetectAssemblyLocation(string assemblyName)
        {
            FileInfo pluginLocation = this.pluginDirectory
                .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(a =>
                {
                    FileVersionInfo productVersion = FileVersionInfo.GetVersionInfo(a.FullName);
                    return productVersion.ProductMajorPart * 1000 +
                    productVersion.ProductMinorPart * 100 +
                    productVersion.ProductBuildPart * 10 +
                    productVersion.ProductPrivatePart;
                }).FirstOrDefault();

            if (pluginLocation != null)
                return pluginLocation.FullName;
            else
                return null;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.FullName == typeof(IPlugin).Assembly.FullName)
                return null;

            string assemblyPath = _pluginResolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
                assemblyPath = DetectAssemblyLocation(assemblyName.Name);

            return (assemblyPath != null) ? LoadFromAssemblyPath(assemblyPath) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _pluginResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath == null)
                libraryPath = DetectAssemblyLocation(unmanagedDllName);

            return (libraryPath != null) ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
        }
    }
}
