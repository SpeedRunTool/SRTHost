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

        private string GetConfigFile(Assembly a) => Path.Combine(new FileInfo(a.Location).DirectoryName, string.Format("{0}.cfg", Path.GetFileNameWithoutExtension(new FileInfo(a.Location).Name)));
        private JsonSerializerOptions jso = new JsonSerializerOptions() { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true };

        public virtual T LoadConfiguration<T>() where T : class, new() => LoadConfiguration<T>(null);
        public virtual T LoadConfiguration<T>(IPluginHostDelegates hostDelegates = null) where T : class, new() => LoadConfiguration<T>(null, hostDelegates);
        public T LoadConfiguration<T>(string? configFile = null, IPluginHostDelegates hostDelegates = null) where T : class, new()
        {
            if (configFile == null)
                configFile = GetConfigFile(typeof(T).Assembly);

            try
            {
                if (File.Exists(configFile))
                    using (FileStream fs = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        return JsonSerializer.DeserializeAsync<T>(fs, jso).Result;
                else
                    return new T(); // File did not exist, just return a new instance.
            }
            catch (Exception ex)
            {
                if (hostDelegates != null)
                {
                    try { hostDelegates.ExceptionMessage.Invoke(ex); }
                    catch { }
                }
                return new T(); // An exception occurred when reading the file, return a new instance.
            }
        }

        public virtual void SaveConfiguration<T>(T configuration) where T : class, new() => SaveConfiguration<T>(configuration, null);
        public virtual void SaveConfiguration<T>(T configuration, IPluginHostDelegates hostDelegates = null) where T : class, new() => SaveConfiguration<T>(configuration, null, hostDelegates);
        public void SaveConfiguration<T>(T configuration, string? configFile = null, IPluginHostDelegates hostDelegates = null) where T : class, new()
        {
            if (configFile == null)
                configFile = GetConfigFile(typeof(T).Assembly);

            if (configuration != null) // Only save if configuration is not null.
            {
                try
                {
                    using (FileStream fs = new FileStream(configFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        JsonSerializer.SerializeAsync<T>(fs, configuration, jso).Wait();
                }
                catch (Exception ex)
                {
                    if (hostDelegates != null)
                    {
                        try { hostDelegates.ExceptionMessage.Invoke(ex); }
                        catch { }
                    }
                }
            }
        }
    }
}
