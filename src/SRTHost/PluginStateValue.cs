using SRTPluginBase;
using System;
using System.Diagnostics;

namespace SRTHost
{
    public interface IPluginStateValue<T> : IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        PluginLoadContext LoadContext { get; init; }
        Type PluginType { get; }
        bool IsInstantiated { get; }
        T? Plugin { get; }
    }

    [DebuggerDisplay("[{PluginType.Name,nq}]")]
    public class PluginStateValue<T> : IPluginStateValue<T>, IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        public PluginLoadContext LoadContext { get; init; }
        public Type PluginType { get; internal set; }
        public bool IsInstantiated { get; internal set; }
        public T? Plugin { get; internal set; }

        public PluginStateValue(PluginLoadContext loadContext, Type pluginType, bool isInstantiated = false, T? plugin = default)
        {
            LoadContext = loadContext;
            PluginType = pluginType;
            IsInstantiated = isInstantiated;
            Plugin = plugin;
        }

        public bool Equals(PluginStateValue<T>? other) => Plugin?.Info.Name == other?.Plugin?.Info.Name;

        public override bool Equals(object? obj) => Equals(obj as PluginStateValue<T>);

        public override int GetHashCode() => HashCode.Combine(LoadContext, IsInstantiated, Plugin);
    }
}
