namespace SRTPluginBase
{
    public interface IPluginUI : IPlugin
    {
        /// <summary>
        /// Receives a producer-specific data structure for processing and returns a status code.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        int ReceiveData(object data);

        /// <summary>
        /// Constrains this UI plugin to work with a specific producer plugin. This value may be null if there are no plugin constraints.
        /// </summary>
        string? RequiredProducer { get; }
    }
}
