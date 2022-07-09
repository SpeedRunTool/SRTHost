using Microsoft.Extensions.Logging;
using System.Text;

namespace SRTHost.LoggerImplementations
{
    public class FileLoggerOptions
    {
        public bool Append { get; set; } = true;
        public bool AutoFlush { get; set; } = true;
        public Encoding Encoding { get; set; } = Encoding.UTF8;
        public LogLevel LoggingLevel { get; set; } = LogLevel.Trace;
        public string TimestampFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff K";
        public bool UtcTime { get; set; } = true;
    }
}
