using SRTPluginBase;
using System;

namespace SRTHost
{
    public class PluginHostDelegates : IPluginHostDelegates
    {
        public OutputMessageDelegate OutputMessage => Console.WriteLine;

        public ReloadDelegate Reload => null;

        public ExitDelegate Exit => null;
    }
}
