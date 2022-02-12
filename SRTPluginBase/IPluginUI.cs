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

        string RequiredProducer { get; }
    }
}
