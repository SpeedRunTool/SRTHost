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

        public virtual T LoadConfiguration<T>() where T : class, new()
        {
            string configFile = GetConfigFile(Assembly.GetCallingAssembly());
            try
            {
                using (FileStream fs = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return JsonSerializer.DeserializeAsync<T>(fs, jso).Result;
            }
            catch
            {
                return new T();
            }
        }

        public virtual void SaveConfiguration<T>(T configuration) where T : class, new()
        {
            string configFile = GetConfigFile(Assembly.GetCallingAssembly());
            using (FileStream fs = new FileStream(configFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                JsonSerializer.SerializeAsync<T>(fs, configuration, jso).Wait();
        }
    }
}
