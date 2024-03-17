using SRTPluginBase.Interfaces;
using System;

namespace SRTHost
{
    public interface IPluginStateValue<T> : IEquatable<PluginStateValue<T>> where T : IPlugin
    {
        /// <summary>
        /// The AssemblyLoadContext the plugin was loaded into.
        /// </summary>
        PluginLoadContext? LoadContext { get; }

        /// <summary>
        /// The Type of the plugin.
        /// </summary>
        Type? PluginType { get; }

        /// <summary>
        /// The current status of this plugin.
        /// </summary>
        PluginStatusEnum Status { get; }

        /// <summary>
        /// Additional status details about this plugin, if available.
        /// </summary>
        PluginSubStatusEnum SubStatus { get; }

        /// <summary>
        /// The instance of this plugin.
        /// </summary>
        T? Plugin { get; }
    }
}
