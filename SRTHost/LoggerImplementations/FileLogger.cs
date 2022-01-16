using Microsoft.Extensions.Logging;
using System;
using System.Text;

namespace SRTHost.LoggerImplementations
{
    public partial class FileLogger : ILogger
    {
        private string logName;
        private FileLoggerProvider fileLoggerProvider;

        public FileLogger(string logName, FileLoggerProvider fileLoggerProvider)
        {
            this.logName = logName;
            this.fileLoggerProvider = fileLoggerProvider;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            throw new NotImplementedException();
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel >= fileLoggerProvider.LoggingLevel;

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
            logMessage.AppendFormat("\t{0}", logLevel);
            logMessage.AppendFormat("\t[{0}]", logName);
            logMessage.AppendFormat("\t[{0}]", eventId);

            if (!string.IsNullOrWhiteSpace(message))
                logMessage.AppendFormat("\t{0}", message);
            
            if (exception != null)
                logMessage.AppendFormat("{0}{1}", Environment.NewLine, exception);

            fileLoggerProvider.WriteLog(logMessage.ToString());
        }
    }
}
