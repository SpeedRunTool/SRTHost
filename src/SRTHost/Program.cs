using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SRTHost.LoggerImplementations;
using SRTPluginBase.Interfaces;

namespace SRTHost
{
    public static class Program
    {
        private const bool UTC_TIMESTAMP = true;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff K";

		public static async Task Main(params string[] args)
        {
            var pluginsDirectory = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "plugins"));
            if (!pluginsDirectory.Exists)
            {
                pluginsDirectory.Create();
                pluginsDirectory.Refresh();
            }

            var host = Host
                .CreateApplicationBuilder(args)
                .ConfigureLogging();

            // Add PluginHost as a singleton first, then add it as a hosted service so that it can be referenced by interface or implementation.
            host.Services.AddSingleton<IPluginHost, PluginHost>();
            host.Services.AddHostedService(s => s.GetRequiredService<PluginHost>()!);
            host.Services.AddHostedService<WebServer>();

            using (var hostApp = host.Build())
                await hostApp.RunAsync();
        }

        public static HostApplicationBuilder ConfigureLogging(this HostApplicationBuilder ctx)
        {
            ctx.Logging.ClearProviders();
            ctx.Logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = string.Format("[{0}] ", TIMESTAMP_FORMAT);
                options.UseUtcTimestamp = UTC_TIMESTAMP;
            });
            ctx.Logging.AddDebug();
            ctx.Logging.AddEventSourceLogger();
#if x64
            ctx.Logging.AddFile(@"SRTHost64",
#else
            ctx.Logging.AddFile(@"SRTHost32",
#endif
                (FileLoggerOptions options) =>
                {
                    options.Append = false;
					options.LoggingLevel = LogLevel.Information;
                    options.StripANSIColor = true;
					options.TimestampFormat = TIMESTAMP_FORMAT;
					options.UtcTime = UTC_TIMESTAMP;
				});

            return ctx;
        }
    }
}
