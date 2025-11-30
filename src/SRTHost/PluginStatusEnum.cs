using System;

namespace SRTHost
{
    public enum PluginStatusEnum : int
    {
        /// <summary>
        /// Plugin is not loaded.
        /// </summary>
        NotLoaded = 0,

        /// <summary>
        /// Plugin is loaded but not instantiated.
        /// </summary>
        Loaded = 1,

        /// <summary>
        /// Plugin is loaded and instantiated.
        /// </summary>
        // Also known as plugin is running.
        Instantiated = 2,

        /// <summary>
        /// Plugin failed to load. See <see cref="PluginSubStatusEnum"/> for more details.
        /// </summary>
        LoadingError = 3,

        /// <summary>
        /// Plugin failed to instantiate. See <see cref="PluginSubStatusEnum"/> for more details.
        /// </summary>
        InstantiationError = 4,
    }

    [Flags]
    public enum PluginSubStatusEnum : int
    {
        /// <summary>
        /// No secondary status.
        /// </summary>
        None = 0b00000000_00000000_00000000_00000000,

        /// <summary>
        /// All other exceptions.
        /// </summary>
        UndefinedException = 0b00000000_00000000_00000000_00000001,

        /// <summary>
        /// Incorrect architecture (x86 dll on x64 program, vice versa).
        /// </summary>
        IncorrectArchitecture = 0b00000000_00000000_00000000_00000010,

        /// <summary>
        /// Plugin cannot initialize.
        /// </summary>
        // Gracefully warn as this is explicitly thrown to gracefully handle cases such as game is not running.
        PluginInitializationException = 0b00000000_00000000_00000000_00000100,

        /// <summary>
        /// Required plugin dependency was not found.
        /// </summary>
        // Error condition.
        PluginNotFoundException = 0b00000000_00000000_00000000_00001000,
    }
}
