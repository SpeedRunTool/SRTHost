using SRTPluginBase;
using System;
using System.Diagnostics;

namespace SRTHost
{
    public interface IPluginStateValue<T> : IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        T Plugin { get; set;  }
        bool Startup { get; set; }
    }

    [DebuggerDisplay("[{Plugin.TypeName,nq}] S:{Startup,nq}")]
    public class PluginStateValue<T> : IPluginStateValue<T>, IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        public T Plugin { get; set; }
        public bool Startup { get; set; }

        public PluginStateValue(T plugin, bool startup)
        {
            Plugin = plugin;
            Startup = startup;
        }

        public bool Equals(PluginStateValue<T>? other) => this.Plugin.Info.Name == other?.Plugin.Info.Name;
    }

    [DebuggerDisplay("[{Plugin.TypeName,nq}] S:{Startup,nq} GR:{Plugin.ProcessRunning,nq}")]
    public class PluginProducerStateValue : PluginStateValue<IPluginProducer>
    {
        public object? LastData { get; set; }

        public PluginProducerStateValue(IPluginProducer plugin, bool startup) : base(plugin, startup) { }
    }

    [DebuggerDisplay("[{Plugin.TypeName,nq}] S:{Startup,nq} RP:{Plugin.RequiredProducer,nq}")]
    public class PluginUIStateValue : PluginStateValue<IPluginUI>
    {
        public PluginUIStateValue(IPluginUI plugin, bool startup) : base(plugin, startup) { }
    }
}
