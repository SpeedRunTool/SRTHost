using System;

namespace SRTHost
{
    /// <summary>
    /// Service for notifying Blazor components of state changes that require re-rendering.
    /// This is particularly useful for cascading updates across the component hierarchy
    /// when plugin state changes occur.
    /// </summary>
    public class CascadingStateChanger
    {
        /// <summary>
        /// Event triggered when the application state has changed and components should refresh.
        /// </summary>
        public event Action? OnChange;

        /// <summary>
        /// Notifies all subscribed components that the state has changed and they should re-render.
        /// </summary>
        public void NotifyStateChanged()
        {
            OnChange?.Invoke();
        }
    }
}
