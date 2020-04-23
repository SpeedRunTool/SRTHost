using SRTPluginBase;
using System;

namespace SRTHost
{
    public class PluginHostDelegates : IPluginHostDelegates
    {
        public ExceptionMessageDelegate ExceptionMessage => Program.HandleException;

        public OutputMessageDelegate OutputMessage => Program.WriteToConsoleAndLog;

        public ReloadDelegate Reload => new ReloadDelegate(() => { });

        public ExitDelegate Exit => new ExitDelegate(() =>
        {
            Program.running = false;
        });
    }
}
