using System;

namespace SRTHost
{
    public class CascadingStateChanger
    {
        public event Action? OnChange;

        public void NotifyStateChanged() => OnChange?.Invoke();
    }
}
