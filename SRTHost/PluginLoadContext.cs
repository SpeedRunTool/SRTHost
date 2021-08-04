using SRTPluginBase;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;

namespace SRTHost
{
    class PluginLoadContext : AssemblyLoadContext
    {
        private DirectoryInfo rootDirectory;
        private DirectoryInfo pluginDirectory;
        private AssemblyDependencyResolver _rootResolver;
        private AssemblyDependencyResolver _pluginResolver;

        public PluginLoadContext(string rootDirectory)
        {
            this.rootDirectory = new DirectoryInfo(rootDirectory);
            this._rootResolver = new AssemblyDependencyResolver(this.rootDirectory.FullName);

            this.pluginDirectory = new DirectoryInfo(Path.Combine(this.rootDirectory.FullName, "plugins" + Path.DirectorySeparatorChar));
            if (!this.pluginDirectory.Exists)
            {
                this.pluginDirectory.Create();
                this.pluginDirectory.Refresh();
            }
            this._pluginResolver = new AssemblyDependencyResolver(this.pluginDirectory.FullName);

            base.Resolving += PluginLoadContext_Resolving;
        }

        private Assembly PluginLoadContext_Resolving(AssemblyLoadContext assemblyLoadContext, AssemblyName assemblyName)
        {
            return Load(assemblyName);
        }

        private string? DetectAssemblyLocation(string assemblyName)
        {
            FileInfo specificLocation = null;
            FileInfo pluginLocation = null;
            FileInfo rootLocation = null;

            // If we're loading a specific provider, try their folder first.
            if (Program.loadSpecificProvider != string.Empty)
                specificLocation = this.pluginDirectory
                    .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                    .FirstOrDefault(a => a.Directory.Name == Program.loadSpecificProvider && a.Directory.Parent.Name == "plugins");

            // If we are not doing specific provider OR it was not found in that folder, check all plugin folders.
            pluginLocation = this.pluginDirectory
                .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .FirstOrDefault(a => a.Directory.Name == assemblyName && a.Directory.Parent.Name == "plugins");

            // If we STILL haven't found it, check any folder.
            rootLocation = this.rootDirectory
                .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(a =>
                {
                    FileVersionInfo productVersion = FileVersionInfo.GetVersionInfo(a.FullName);
                    return productVersion.ProductMajorPart * 1000 +
                    productVersion.ProductMinorPart * 100 +
                    productVersion.ProductBuildPart * 10 +
                    productVersion.ProductPrivatePart;
                }).FirstOrDefault();

            if (specificLocation != null)
                return specificLocation.FullName;
            else if (pluginLocation != null)
                return pluginLocation.FullName;
            else if (rootLocation != null)
                return rootLocation.FullName;
            else
                return null;
        }

        public X509Certificate GetSigningInfo(string location)
        {
            try
            {
                return X509Certificate.CreateFromSignedFile(location);
            }
            catch (Exception ex)
            {
                return null;
            }
        }
        public X509Certificate2 GetSigningInfo2(string location)
        {
            try
            {
                return new X509Certificate2(GetSigningInfo(location));
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.FullName == typeof(IPlugin).Assembly.FullName)
                return null;

            string assemblyPath = _pluginResolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
                assemblyPath = _rootResolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
                assemblyPath = DetectAssemblyLocation(assemblyName.Name);

            return (assemblyPath != null) ? LoadFromAssemblyPath(assemblyPath) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _pluginResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath == null)
                libraryPath = _rootResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath == null)
                libraryPath = DetectAssemblyLocation(unmanagedDllName);

            return (libraryPath != null) ? LoadUnmanagedDllFromPath(libraryPath) : IntPtr.Zero;
        }
    }
}
