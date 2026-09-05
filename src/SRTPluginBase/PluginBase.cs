using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SRTPluginBase
{
    public abstract class PluginBase : IPlugin
    {
        public abstract IPluginInfo Info { get; }

        public abstract int Startup(IPluginHostDelegates hostDelegates);

        public abstract int Shutdown();

        public string GetConfigFile(Assembly a) => a.GetConfigFile();

        public virtual T LoadConfiguration<T>() where T : class, new() => Extensions.LoadConfiguration<T>(null);
        public virtual T LoadConfiguration<T>(IPluginHostDelegates hostDelegates = null) where T : class, new() => Extensions.LoadConfiguration<T>(null, hostDelegates);
        public T LoadConfiguration<T>(string? configFile = null, IPluginHostDelegates hostDelegates = null) where T : class, new() => Extensions.LoadConfiguration<T>(configFile, hostDelegates);

        public virtual void SaveConfiguration<T>(T configuration) where T : class, new() => configuration.SaveConfiguration(null);
        public virtual void SaveConfiguration<T>(T configuration, IPluginHostDelegates hostDelegates = null) where T : class, new() => configuration.SaveConfiguration(null, hostDelegates);
        public void SaveConfiguration<T>(T configuration, string? configFile = null, IPluginHostDelegates hostDelegates = null) where T : class, new() => configuration.SaveConfiguration(configFile, hostDelegates);
    }
}
