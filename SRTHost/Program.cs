using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using SRTHost.LoggerImplementations;
using System;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.IO;
using System.Diagnostics;

namespace SRTHost
{
    public static class Program
    {
        private const bool UTC_TIMESTAMP = true;
        private const string TIMESTAMP_FORMAT = "yyyy-MM-dd HH:mm:ss.fff K";

        public static async Task Main(params string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // Logging
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = string.Format("[{0}] ", TIMESTAMP_FORMAT);
                options.UseUtcTimestamp = UTC_TIMESTAMP;
            });
            builder.Logging.AddDebug();
            builder.Logging.AddEventSourceLogger();
#if x64
            builder.Logging.AddFile(@"SRTHost64",
#else
            builder.Logging.AddFile(@"SRTHost32",
#endif
                (FileLoggerOptions options) =>
                {
                    options.Append = false;
                    options.UtcTime = UTC_TIMESTAMP;
                    options.TimestampFormat = TIMESTAMP_FORMAT;
                    options.LoggingLevel = LogLevel.Information;
                });

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("CORSPolicy", builder =>
                {
                    builder.AllowAnyMethod()
                           .AllowAnyHeader()
                           .AllowCredentials()
                           .SetIsOriginAllowed((string host) => true);
                });
            });

            builder.Services.AddRazorPages();
            builder.Services.AddServerSideBlazor();

            builder.Services.AddSingleton(s => ActivatorUtilities.CreateInstance<PluginSystem>(s, s.GetRequiredService<ILogger<PluginSystem>>(), Environment.GetCommandLineArgs().Skip(1).ToArray()));
            builder.Services.AddHostedService(s => s.GetService<PluginSystem>());

            WebApplication app = builder.Build();

            if (app.Environment.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseStaticFiles();
            app.UseRouting();
            app.UseCors("CORSPolicy");
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });

            await app.RunAsync();
        }
    }
}
