using System;

namespace SRTPluginBase
{
    public delegate void ExceptionMessageDelegate(Exception exception);
    public delegate void OutputMessageDelegate(string message, params object[] args);
    public delegate void ReloadDelegate();
    public delegate void ExitDelegate();

    public interface IPluginHostDelegates
    {
        ExceptionMessageDelegate ExceptionMessage { get; }
        OutputMessageDelegate OutputMessage { get; }
        ReloadDelegate Reload { get; }
        ExitDelegate Exit { get; }
    }
}
