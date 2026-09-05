using System;

namespace SRTPluginBase
{
    public interface IPluginInfo
    {
        /// <summary>
        /// The name of the plugin.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// The description of what this plugin does.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// The author of the plugin.
        /// </summary>
        string Author { get; }

        /// <summary>
        /// A URI for the user to get more info.
        /// </summary>
        Uri MoreInfoURL { get; }

        /// <summary>
        /// The major version of this plugin.
        /// </summary>
        int VersionMajor { get; }

        /// <summary>
        /// The minor version of this plugin.
        /// </summary>
        int VersionMinor { get; }

        /// <summary>
        /// The build version of this plugin.
        /// </summary>
        int VersionBuild { get; }

        /// <summary>
        /// The revision version of this plugin.
        /// </summary>
        int VersionRevision { get; }
    }
}
