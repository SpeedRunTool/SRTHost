using System;
using System.Collections.Generic;
using System.Net;

namespace SRTPluginBase
{
    public interface IPlugin : IEquatable<IPlugin>
    {
        /// <summary>
        /// Gets the plugins type name.
        /// </summary>
        string TypeName => this.GetType().Name;

        /// <summary>
        /// Information about this plugin.
        /// </summary>
        IPluginInfo Info { get; }

        /// <summary>
        /// This method is called when the plugin is being loaded. All one-time initialization should occur here.
        /// </summary>
        /// <returns>A value indicating success or failure. SRT Host expects 0 for success, any other value will indicate a failure. These values are up to the plugin developer discretion.</returns>
        int Startup();

        /// <summary>
        /// This method is called when the plugin is being unloaded. All graceful cleanup code should occur here.
        /// </summary>
        /// <returns>A value indicating success or failure. SRT Host expects 0 for success, any other value will indicate a failure. These values are up to the plugin developer discretion.</returns>
        int Shutdown();

        /// <summary>
        /// This method is called when a request comes in via the API framework.
        /// </summary>
        /// <param name="command">The plugin-specific command to handle.</param>
        /// <param name="arguments">The query string parameters supplied to the API call.</param>
        /// <param name="statusCode">The status code to return to the requestor.</param>
        /// <returns>The plugin-specific value to return to the requestor.</returns>
        object? CommandHandler(string command, KeyValuePair<string, string[]>[] arguments, out HttpStatusCode statusCode);

        public new bool Equals(IPlugin? other) => TypeName == other?.TypeName && Info.Name == other?.Info.Name;

        public bool Equals(object? obj) => Equals(obj as IPlugin);

        public int GetHashCode() => HashCode.Combine(TypeName, Info);
    }
}
