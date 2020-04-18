using System;
using System.Collections.Generic;
using System.Text;

namespace SRTPluginBase
{
    public interface IPluginProvider : IPlugin
    {
        /// <summary>
        /// Instructs the provider plugin to read the game's memory and return a structure representing the data retrieved.
        /// </summary>
        /// <returns>A game-specific </returns>
        object PullData(); // TODO: Make generic if possible without embedding game-specific info into SRTPluginBase... Probably not possible...
    }
}
