using SRTPluginBase;
using System;

namespace SRTHost
{
    public class PluginHostDelegates : IPluginHostDelegates
    {
        public OutputMessageDelegate OutputMessage => Console.WriteLine;

        public ReloadDelegate Reload => new ReloadDelegate(() => { });

        public ExitDelegate Exit => new ExitDelegate(() =>
        {
            Program.running = false;
        });
    }
}
