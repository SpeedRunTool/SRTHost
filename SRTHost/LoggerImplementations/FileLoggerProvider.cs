using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Text;

namespace SRTHost.LoggerImplementations
{
    public class FileLoggerProducer : ILoggerProvider
    {
        private string logName;
        private StreamWriter logStreamWriter;

        public bool Append { get; init; }
        public bool AutoFlush { get; init; }
        public Encoding Encoding { get; init; }
        public LogLevel LoggingLevel { get; init; }
        public string TimestampFormat { get; init; }
        public bool UtcTime { get; init; }

        public FileLoggerProducer(string logName, FileLoggerOptions fileLoggerOptions)
        {
            this.logName = logName;

            Append = fileLoggerOptions.Append;
            AutoFlush = fileLoggerOptions.AutoFlush;
            Encoding = fileLoggerOptions.Encoding;
            LoggingLevel = fileLoggerOptions.LoggingLevel;
            TimestampFormat = fileLoggerOptions.TimestampFormat;
            UtcTime = fileLoggerOptions.UtcTime;

            this.logStreamWriter = new StreamWriter(new FileStream(Path.Combine(AppContext.BaseDirectory, string.Format("{0}.log", logName)), Append ? FileMode.Append : FileMode.Create, FileAccess.Write), Encoding)
            {
                AutoFlush = AutoFlush,
                NewLine = Environment.NewLine,
            };
        }

        public ILogger CreateLogger(string categoryName) => new FileLogger(logName, categoryName, this);

        public void WriteLog(string message) => logStreamWriter?.WriteLine(message);

        private bool disposedValue;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    this.logStreamWriter?.Dispose();
                    this.logStreamWriter = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~FileLoggerProducer()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            System.GC.SuppressFinalize(this);
        }
    }
}
