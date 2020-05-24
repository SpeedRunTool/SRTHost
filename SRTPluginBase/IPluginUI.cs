using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Text;

namespace SRTPluginBase
{
    public interface IPluginUI : IPlugin
    {
        /// <summary>
        /// Receives a representation of the game's current memory.
        /// </summary>
        /// <param name="gameMemory"></param>
        /// <returns></returns>
        int ReceiveData(object gameMemory);

        string RequiredProvider { get; }
    }
}
