using System.Net;
using System.Net.Http;

namespace SRTHost.Exceptions.HTTP
{
    internal class HTTPManifestNotFoundException() : HttpRequestException($"Could not find manifest")
    {
        public new HttpStatusCode? StatusCode { get; } = HttpStatusCode.NotFound;
    }
}
