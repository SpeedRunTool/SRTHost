using SRTPluginBase;
using System;
using System.Diagnostics;

namespace SRTHost
{
    public interface IPluginStateValue<T> : IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        PluginLoadContext LoadContext { get; init; }
        bool IsInstantiated { get; }
        T? Plugin { get; }
    }

    [DebuggerDisplay("[{Plugin.TypeName,nq}]")]
    public class PluginStateValue<T> : IPluginStateValue<T>, IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        public PluginLoadContext LoadContext { get; init; }
        public bool IsInstantiated { get; protected set; }
        public T? Plugin { get; protected set; }

        public PluginStateValue(PluginLoadContext loadContext, bool isInstantiated = false, T? plugin = default)
        {
            LoadContext = loadContext;
            IsInstantiated = isInstantiated;
            Plugin = plugin;
        }

        public bool Equals(PluginStateValue<T>? other) => Plugin?.Info.Name == other?.Plugin?.Info.Name;

        public override bool Equals(object? obj) => Equals(obj as PluginStateValue<T>);

        public override int GetHashCode() => HashCode.Combine(LoadContext, IsInstantiated, Plugin);
    }
}
