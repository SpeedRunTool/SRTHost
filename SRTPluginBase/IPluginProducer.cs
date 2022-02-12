namespace SRTPluginBase
{
    public interface IPluginProducer : IPlugin
    {
        /// <summary>
        /// Instructs the producer plugin to retrieve data and return a structure representing the data retrieved.
        /// </summary>
        /// <returns>Plugin-specific data structure.</returns>
        object PullData();

        bool ProcessRunning { get; }
    }
}
