using System.Net.Http;

namespace SRTHost.APIs
{
    internal class BaseAPIHandler
    {
        internal readonly HttpClient client = new HttpClient();

        internal BaseAPIHandler()
        {
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        }
    }
}
