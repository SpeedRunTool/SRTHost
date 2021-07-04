using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SRTPluginBase
{
    public static class Extensions
    {
        public static bool ByteArrayEquals(this byte[] first, byte[] second)
        {
            // Check to see if the have the same reference.
            if (first == second)
                return true;

            // Check to make sure neither are null.
            if (first == null || second == null)
                return false;

            // Ensure the array lengths match.
            if (first.Length != second.Length)
                return false;

            // Check each element side by side for equality.
            for (int i = 0; i < first.Length; i++)
                if (first[i] != second[i])
                    return false;

            // We made it past the for loop, we're equals!
            return true;
        }

        private static readonly JsonSerializerOptions jso = new JsonSerializerOptions() { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip, WriteIndented = true };
        public static string GetConfigFile(this Assembly a) => Path.Combine(new FileInfo(a.Location).DirectoryName, string.Format("{0}.cfg", Path.GetFileNameWithoutExtension(new FileInfo(a.Location).Name)));

        public static T LoadConfiguration<T>() where T : class, new() => LoadConfiguration<T>(null);
        public static T LoadConfiguration<T>(IPluginHostDelegates hostDelegates = null) where T : class, new() => LoadConfiguration<T>(null, hostDelegates);
        public static T LoadConfiguration<T>(string? configFile = null, IPluginHostDelegates hostDelegates = null) where T : class, new()
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

        public static void SaveConfiguration<T>(this T configuration) where T : class, new() => SaveConfiguration<T>(configuration, null);
        public static void SaveConfiguration<T>(this T configuration, IPluginHostDelegates hostDelegates = null) where T : class, new() => SaveConfiguration<T>(configuration, null, hostDelegates);
        public static void SaveConfiguration<T>(this T configuration, string? configFile = null, IPluginHostDelegates hostDelegates = null) where T : class, new()
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
