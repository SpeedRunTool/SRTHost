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

        public PluginLoadContext(DirectoryInfo thisPluginDirectory) : base(thisPluginDirectory.Name, false)
        {
            this.thisPluginDirectory = thisPluginDirectory;
            _thisPluginResolver = new AssemblyDependencyResolver(this.thisPluginDirectory.FullName);

            Resolving += PluginLoadContext_Resolving;
        }

        private Assembly PluginLoadContext_Resolving(AssemblyLoadContext assemblyLoadContext, AssemblyName assemblyName)
        {
            return Load(assemblyName);
        }

        private string? DetectAssemblyLocation(string assemblyName)
        {
            // This is typicvally the plugin itself or a dependency that is included from its folder.
            FileInfo pluginLocation = thisPluginDirectory
                .EnumerateFiles(assemblyName + ".dll", SearchOption.AllDirectories)
                .OrderByDescending(a =>
                {
                    FileVersionInfo productVersion = FileVersionInfo.GetVersionInfo(a.FullName);
                    return productVersion.ProductMajorPart * 1000 +
                    productVersion.ProductMinorPart * 100 +
                    productVersion.ProductBuildPart * 10 +
                    productVersion.ProductPrivatePart;
                }).FirstOrDefault();

            if (pluginLocation != null) // Always prefer assemblies found in the plugin's folder first.
                return pluginLocation.FullName;
            else // We did not find what we were looking for.
                return null;
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (assemblyName.FullName == typeof(IPlugin).Assembly.FullName)
                return null;

            string assemblyPath = _thisPluginResolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath == null)
                assemblyPath = DetectAssemblyLocation(assemblyName.Name);

            if (assemblyPath != null)
                return LoadFromAssemblyPath(assemblyPath);
            else if (All.Any(a => a.Name == assemblyName.Name)) // Are there any LoadContexts that match this AssemblyName? If so, maybe we can enlist their help!
                return All.First(a => a.Name == assemblyName.Name).LoadFromAssemblyName(assemblyName);
            else
                return Default.LoadFromAssemblyName(assemblyName);
        }
    }
}
