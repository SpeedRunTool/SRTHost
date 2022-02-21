using Microsoft.Extensions.Logging;
using System;
using System.Text;

namespace SRTHost.LoggerImplementations
{
    public partial class FileLogger : ILogger
    {
        private readonly string categoryName;
        private readonly FileLoggerProducer fileLoggerProducer;

        public FileLogger(string categoryName, FileLoggerProducer fileLoggerProducer)
        {
            this.categoryName = categoryName;
            this.fileLoggerProducer = fileLoggerProducer;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return new NoopDisposable();
        }

        private class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= fileLoggerProducer.LoggingLevel;

        private static string GetShortLogLevel(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => string.Empty,
            };
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            if (formatter == null)
                throw new ArgumentNullException(nameof(formatter));

            string message = formatter(state, exception);

            // Don't log empty messages?
            if (string.IsNullOrWhiteSpace(message) && exception == null)
                return;

            StringBuilder logMessage = new StringBuilder();
            logMessage.AppendFormat("[{0}]", (fileLoggerProducer.UtcTime ? DateTime.UtcNow : DateTime.Now).ToString(fileLoggerProducer.TimestampFormat));
            logMessage.AppendFormat("\x00A0{0}:", GetShortLogLevel(logLevel));
            logMessage.AppendFormat("\x00A0{0}[", categoryName);
            logMessage.AppendFormat("{0}]\r\n      ", eventId.Id);

            if (!string.IsNullOrWhiteSpace(message))
                logMessage.AppendFormat("{0}", message);
            
            if (exception != null)
                logMessage.AppendFormat("{0}{1}", Environment.NewLine, exception);

            fileLoggerProducer.WriteLog(logMessage.ToString());
        }
    }
}
