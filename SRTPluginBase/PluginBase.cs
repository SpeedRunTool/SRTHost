using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

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
        public virtual T LoadConfiguration<T>(IPluginHostDelegates hostDelegates) where T : class, new()
        {
            FileInfo configFile = new FileInfo(GetConfigFile(Assembly.GetCallingAssembly()));
            try
            {
                if (configFile.Exists)
                    using (FileStream fs = new FileStream(configFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
        public virtual void SaveConfiguration<T>(T configuration, IPluginHostDelegates hostDelegates) where T : class, new()
        {
            // Only save if configuration is not null.
            if (configuration != null)
            {
                try
                {
                    string configFile = GetConfigFile(Assembly.GetCallingAssembly());
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
