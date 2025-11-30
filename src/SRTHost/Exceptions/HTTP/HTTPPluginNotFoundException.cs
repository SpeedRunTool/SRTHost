using System.Net;
using System.Net.Http;

namespace SRTHost.Exceptions.HTTP
{
    internal class HTTPPluginNotFoundException(string pluginName) : HttpRequestException($"Could not find plugin '{pluginName}'")
    {
        public new HttpStatusCode? StatusCode { get; } = HttpStatusCode.NotFound;
    }
}
