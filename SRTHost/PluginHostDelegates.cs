using SRTPluginBase;
using System;

namespace SRTHost
{
    public class PluginHostDelegates : IPluginHostDelegates
    {
        public ExceptionMessageDelegate ExceptionMessage => new ExceptionMessageDelegate((Exception ex) => { });// Program.HandleException;

        public OutputMessageDelegate OutputMessage => Console.WriteLine;

        public ReloadDelegate Reload => new ReloadDelegate(() => { });

        public ExitDelegate Exit => new ExitDelegate(() =>
        {
            //Program.running = false;
        });
    }
}
