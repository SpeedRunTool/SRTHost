using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SRTHost.LoggerImplementations;
using System;
using System.Linq;

namespace SRTHost
{
    public static class Program
    {
        private const bool UTC_TIMESTAMP = true;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff K";

        public static async Task Main(params string[] args)
        {
            IHost host = CreateHostBuilder(args).Build();
            await host.RunAsync();
        }

        private static IHostBuilder CreateHostBuilder(string[] args) => Host.CreateDefaultBuilder(args).ConfigureServices(ConfigureServices);

        private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
        {
            services.AddLogging((ILoggingBuilder c) =>
            {
                c.AddSimpleConsole(o =>
                {
                    o.IncludeScopes = true;
                    o.TimestampFormat = string.Format("[{0}] ", TIMESTAMP_FORMAT);
                    o.UseUtcTimestamp = UTC_TIMESTAMP;
                });
                c.AddDebug();
                c.AddEventSourceLogger();
#if x64
                c.AddFile(@"SRTHost64.log",
#else
                c.AddFile(@"SRTHost32.log",
#endif
                    (FileLoggerOptions o) =>
                    {
                        o.UtcTime = UTC_TIMESTAMP;
                        o.TimestampFormat = TIMESTAMP_FORMAT;
                        o.LoggingLevel = LogLevel.Information;
                    });
            });
            services.AddHostedService(s => ActivatorUtilities.CreateInstance<PluginSystem>(s, s.GetRequiredService<ILogger<PluginSystem>>(), Environment.GetCommandLineArgs().Skip(1).ToArray()));
        }
    }
}
