using System;

namespace SRTHost
{
    /// <summary>
    /// Service for notifying Blazor components of state changes that require re-rendering.
    /// This is particularly useful for cascading updates across the component hierarchy
    /// when plugin state changes occur.
    /// 
    /// Thread Safety: This class is thread-safe. The NotifyStateChanged method can be called
    /// from any thread, and event handlers will be invoked on the calling thread. Subscribers
    /// should be aware that they may be called from background threads and should use
    /// InvokeAsync when updating component state.
    /// </summary>
    public class CascadingStateChanger
    {
        /// <summary>
        /// Event triggered when the application state has changed and components should refresh.
        /// </summary>
        public event Action? OnChange;

        /// <summary>
        /// Notifies all subscribed components that the state has changed and they should re-render.
        /// This method is thread-safe and can be called from any thread.
        /// </summary>
        public void NotifyStateChanged()
        {
            // Create local copy to avoid race condition between null check and invocation
            Action? handler = OnChange;
            handler?.Invoke();
        }
    }
}
