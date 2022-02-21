using System;

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

        public new bool Equals(IPlugin? other) => TypeName == other?.TypeName && Info.Name == other?.Info.Name;

        public bool Equals(object? obj) => Equals(obj as IPlugin);

        public int GetHashCode() => HashCode.Combine(TypeName, Info);
    }
}
