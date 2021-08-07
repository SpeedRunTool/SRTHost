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
        private DirectoryInfo thisPluginDirectory;
        private AssemblyDependencyResolver _thisPluginResolver;

        private DirectoryInfo rootPluginDirectory;
        private AssemblyDependencyResolver _rootPluginResolver;

        public PluginLoadContext(string thisPluginDirectory, string rootPluginDirectory)
        {
            this.thisPluginDirectory = new DirectoryInfo(thisPluginDirectory);
            this._thisPluginResolver = new AssemblyDependencyResolver(this.thisPluginDirectory.FullName);

            this.rootPluginDirectory = new DirectoryInfo(rootPluginDirectory);
            this._rootPluginResolver = new AssemblyDependencyResolver(this.rootPluginDirectory.FullName);

            base.Resolving += PluginLoadContext_Resolving;
        }

        private Assembly PluginLoadContext_Resolving(AssemblyLoadContext assemblyLoadContext, AssemblyName assemblyName)
        {
            return Load(assemblyName);
        }

        private string? DetectAssemblyLocation(string assemblyName)
        {
            // This is typicvally the plugin itself or a dependency that is included from its folder.
            FileInfo pluginLocation = this.thisPluginDirectory
                .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(a =>
                {
                    FileVersionInfo productVersion = FileVersionInfo.GetVersionInfo(a.FullName);
                    return productVersion.ProductMajorPart * 1000 +
                    productVersion.ProductMinorPart * 100 +
                    productVersion.ProductBuildPart * 10 +
                    productVersion.ProductPrivatePart;
                }).FirstOrDefault();

            // This is typically the provider a dependent plugin is looking for which may be outside its own folder.
            FileInfo rootPluginLocation = this.rootPluginDirectory
                .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .FirstOrDefault(a => a.Directory.Name == assemblyName);

            if (pluginLocation != null) // Always prefer assemblies found in the plugin's folder first.
                return pluginLocation.FullName;
            else if (rootPluginLocation != null) // Fallback to an assembly from the root plugins folder.
                return rootPluginLocation.FullName;
            else // We did not find what we were looking for.
                return null;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.FullName == typeof(IPlugin).Assembly.FullName)
                return null;

            string assemblyPath = _thisPluginResolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
                assemblyPath = _rootPluginResolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
                assemblyPath = DetectAssemblyLocation(assemblyName.Name);

            return (assemblyPath != null) ? LoadFromAssemblyPath(assemblyPath) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _thisPluginResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath == null)
                libraryPath = _rootPluginResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath == null)
                libraryPath = DetectAssemblyLocation(unmanagedDllName);

            return (libraryPath != null) ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
        }
    }
}
