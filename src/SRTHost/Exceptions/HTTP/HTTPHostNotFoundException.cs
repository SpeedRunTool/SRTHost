using System.Net;
using System.Net.Http;

namespace SRTHost.Exceptions.HTTP
{
    internal class HTTPHostNotFoundException(string hostName) : HttpRequestException($"Could not find host '{hostName}'")
    {
        public new HttpStatusCode? StatusCode { get; } = HttpStatusCode.NotFound;
    }
}
