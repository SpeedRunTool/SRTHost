using SRTPluginBase;
using System;
using System.Diagnostics;

namespace SRTHost
{
    [DebuggerDisplay("[{PluginType.Name,nq}]")]
    public class PluginStateValue<T> : IPluginStateValue<T>, IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        public PluginLoadContext? LoadContext { get; internal set; }
        public Type? PluginType { get; internal set; }
        public PluginStatusEnum Status { get; internal set; }
        public PluginSubStatusEnum SubStatus { get; internal set; }
        public T? Plugin { get; internal set; }

        public bool Equals(PluginStateValue<T>? other) => Plugin?.Info.Name == other?.Plugin?.Info.Name;

        public override bool Equals(object? obj) => Equals(obj as PluginStateValue<T>);

        public override int GetHashCode() => HashCode.Combine(LoadContext, PluginType, Plugin);
    }
}
