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

    [DebuggerDisplay("[{Plugin.GetType().Name,nq}] S:{Startup,nq}")]
    public class PluginStateValue<T> : IPluginStateValue<T>, IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        public T Plugin { get; set; }
        public bool Startup { get; set; }

        public bool Equals(PluginStateValue<T> other) => this.Plugin.Info.Name == other.Plugin.Info.Name;
    }

    [DebuggerDisplay("[{Plugin.GetType().Name,nq}] S:{Startup,nq} GR:{Plugin.GameRunning,nq}")]
    public class PluginProviderStateValue : PluginStateValue<IPluginProvider>
    {
        public object LastData { get; set; }
    }

    [DebuggerDisplay("[{Plugin.GetType().Name,nq}] S:{Startup,nq} RP:{Plugin.RequiredProvider,nq}")]
    public class PluginUIStateValue : PluginStateValue<IPluginUI>
    {
    }
}
