using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SRTHost.LoggerImplementations;
using System;
using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore;

namespace SRTHost
{
    public static class Program
    {
        private const bool UTC_TIMESTAMP = true;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff K";

        public static async Task Main(params string[] args)
        {
            IWebHost host = CreateHostBuilder(args).Build();
            await host.RunAsync();
        }

        private static IWebHostBuilder CreateHostBuilder(string[] args) =>
            WebHost
            .CreateDefaultBuilder(args)
            .UseContentRoot(AppContext.BaseDirectory) // For some reason this gets set incorrectly sometimes? So reset it after Default Builder.
            .ConfigureLogging(ConfigureLogging)
            .UseStartup<Startup>();

        private static void ConfigureLogging(WebHostBuilderContext context, ILoggingBuilder logging)
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = string.Format("[{0}] ", TIMESTAMP_FORMAT);
                options.UseUtcTimestamp = UTC_TIMESTAMP;
            });
            logging.AddDebug();
            logging.AddEventSourceLogger();
#if x64
            logging.AddFile(@"SRTHost64",
#else
            logging.AddFile(@"SRTHost32",
#endif
                (FileLoggerOptions options) =>
                {
                    options.Append = false;
                    options.UtcTime = UTC_TIMESTAMP;
                    options.TimestampFormat = TIMESTAMP_FORMAT;
                    options.LoggingLevel = LogLevel.Information;
                });
        }
    }
}
