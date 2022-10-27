using SRTPluginBase;
using System;
using System.Diagnostics;

namespace SRTHost
{
    public interface IPluginStateValue<T> : IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        PluginLoadContext LoadContext { get; init; }
        T Plugin { get; init; }
    }

    [DebuggerDisplay("[{Plugin.TypeName,nq}]")]
    public class PluginStateValue<T> : IPluginStateValue<T>, IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        public PluginLoadContext LoadContext { get; init; }
        public T Plugin { get; init; }

        public PluginStateValue(PluginLoadContext loadContext, T plugin)
        {
            LoadContext = loadContext;
            Plugin = plugin;
        }

        public bool Equals(PluginStateValue<T>? other) => Plugin.Info.Name == other?.Plugin.Info.Name;

        public override bool Equals(object? obj) => Equals(obj as PluginStateValue<T>);

        public override int GetHashCode() => HashCode.Combine(LoadContext, Plugin);
    }
}
