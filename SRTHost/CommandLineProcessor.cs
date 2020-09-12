using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SRTHost
{
    public class CommandLineProcessor : IDisposable, IEnumerable<KeyValuePair<string, string?>>
    {
        private ReadOnlyDictionary<string, string?> keyValueDictionary;
        private bool disposedValue;

        public CommandLineProcessor(string[] args)
        {
            keyValueDictionary = new ReadOnlyDictionary<string, string?>((args != null) ?
                args.ToDictionary(
                    (string input) => (input.Contains('=')) ? input.Split('=', StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('-') : input.TrimStart('-'),
                    (string input) => (input.Contains('=')) ? input.Split('=', StringSplitOptions.RemoveEmptyEntries)[1] : null,
                    StringComparer.InvariantCultureIgnoreCase
                    ) : new Dictionary<string, string?>()
                );
        }

        public string? GetValue(string? key) => (key != null && keyValueDictionary.ContainsKey(key)) ? keyValueDictionary[key] : null;

        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => keyValueDictionary.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => keyValueDictionary.GetEnumerator();

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    keyValueDictionary = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~CommandLineProcessor()
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
