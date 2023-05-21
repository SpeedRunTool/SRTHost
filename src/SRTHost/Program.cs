﻿using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SRTHost.LoggerImplementations;
using System.IO;
using System.Threading.Tasks;

namespace SRTHost
{
    public static class Program
    {
        private const bool UTC_TIMESTAMP = true;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff K";

        public static string WebRoot => Path.Combine(Directory.GetCurrentDirectory(), "plugins");

		public static async Task Main(params string[] args)
        {
            using (IWebHost host = CreateBuilder(args).Build())
                await host.RunAsync();
        }

        public static IWebHostBuilder CreateBuilder(string[] args) =>
            WebHost
            .CreateDefaultBuilder(args)
            .UseWebRoot(WebRoot) // Set the Web Root to the same folder as the Content Root + "/plugins".
            .ConfigureLogging(ConfigureLogging)
            .UseStartup<Startup>();

        public static void ConfigureLogging(WebHostBuilderContext ctx, ILoggingBuilder logging)
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
