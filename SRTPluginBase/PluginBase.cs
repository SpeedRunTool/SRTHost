using System.Reflection;

namespace SRTPluginBase
{
    public abstract class PluginBase : IPlugin
    {
        public abstract IPluginInfo Info { get; }

        public abstract int Startup();

        public abstract int Shutdown();

        public string GetConfigFile(Assembly a) => a.GetConfigFile();

        public virtual T LoadConfiguration<T>() where T : class, new() => Extensions.LoadConfiguration<T>(null);
        public T LoadConfiguration<T>(string? configFile = null) where T : class, new() => Extensions.LoadConfiguration<T>(configFile);

        public virtual void SaveConfiguration<T>(T configuration) where T : class, new() => configuration.SaveConfiguration(null);
        public void SaveConfiguration<T>(T configuration, string? configFile = null) where T : class, new() => configuration.SaveConfiguration(configFile);

        public bool Equals(IPlugin? other) => (this as IPlugin).Equals(other);
    }
}
