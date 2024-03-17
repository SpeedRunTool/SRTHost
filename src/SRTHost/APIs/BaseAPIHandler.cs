using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace SRTHost.APIs
{
    internal abstract class BaseAPIHandler : IDisposable
    {
        protected readonly HttpClient client = new HttpClient();

        internal BaseAPIHandler()
        {
            FileVersionInfo srtHostFileVersionInfo = FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, PluginHost.APP_EXE_NAME));
            client.DefaultRequestHeaders.Add("User-Agent", $"{srtHostFileVersionInfo.ProductName} v{srtHostFileVersionInfo.ProductVersion} {PluginHost.APP_ARCHITECTURE}");
        }

        private bool disposedValue;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    client?.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~BaseAPIHandler()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
