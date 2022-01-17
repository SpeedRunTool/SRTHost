using Microsoft.Extensions.Logging;
using System;
using System.Text;

namespace SRTHost.LoggerImplementations
{
    public partial class FileLogger : ILogger
    {
        private string logName;
        private string categoryName;
        private FileLoggerProvider fileLoggerProvider;

        public FileLogger(string logName, string categoryName, FileLoggerProvider fileLoggerProvider)
        {
            this.logName = logName;
            this.categoryName = categoryName;
            this.fileLoggerProvider = fileLoggerProvider;
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

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= fileLoggerProvider.LoggingLevel;

        private string GetShortLogLevel(LogLevel logLevel)
        {
            switch (logLevel)
            {
                case LogLevel.Trace:
                    return "trce";

                case LogLevel.Debug:
                    return "dbug";

                case LogLevel.Information:
                    return "info";

                case LogLevel.Warning:
                    return "warn";

                case LogLevel.Error:
                    return "fail";

                case LogLevel.Critical:
                    return "crit";

                default:
                    return string.Empty;
            }
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
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
            logMessage.AppendFormat("[{0}]", (fileLoggerProvider.UtcTime ? DateTime.UtcNow : DateTime.Now).ToString(fileLoggerProvider.TimestampFormat));
            logMessage.AppendFormat("\x00A0{0}:", GetShortLogLevel(logLevel));
            logMessage.AppendFormat("\x00A0{0}[", categoryName);
            logMessage.AppendFormat("{0}]\r\n      ", eventId.Id);

            if (!string.IsNullOrWhiteSpace(message))
                logMessage.AppendFormat("{0}", message);
            
            if (exception != null)
                logMessage.AppendFormat("{0}{1}", Environment.NewLine, exception);

            fileLoggerProvider.WriteLog(logMessage.ToString());
        }
    }
}
