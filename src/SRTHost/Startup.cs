using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using SRTPluginBase;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace SRTHost
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors(options =>
            {
                options.AddPolicy("CORSPolicy", builder =>
                {
                    builder.AllowAnyMethod()
                           .AllowAnyHeader()
                           .AllowCredentials()
                           .SetIsOriginAllowed((string host) => true);
                });
            });

			services
                .AddRazorPages()
                .AddRazorRuntimeCompilation();

            services.AddServerSideBlazor();
            services.AddMudServices();

            services.AddHttpClient();

			// Add a new ViewCompilerProvider to support plugins. Ref(s): https://stackoverflow.com/a/60901929
			ServiceDescriptor? descriptor = services.FirstOrDefault(s => s.ServiceType == typeof(IViewCompilerProvider));
            if (descriptor is not null)
                services.Remove(descriptor);
			services.AddSingleton<IViewCompilerProvider, PluginViewCompilerProvider>();
            services.AddScoped<CascadingStateChanger>();
            services.AddSingleton<PluginHost>(s => ActivatorUtilities.CreateInstance<PluginHost>(s, s.GetRequiredService<ILogger<PluginHost>>(), s, Environment.GetCommandLineArgs().Skip(1).ToArray()));
			services.AddSingleton<IPluginHost>(s => s.GetRequiredService<PluginHost>());
			services.AddHostedService(s => s.GetRequiredService<PluginHost>()!);
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            DirectoryInfo wwwrootDirectory = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
            if (!wwwrootDirectory.Exists)
            {
                wwwrootDirectory.Create();
                wwwrootDirectory.Refresh();
            }

            DirectoryInfo pluginsDirectory = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "plugins"));
            if (!pluginsDirectory.Exists)
            {
                pluginsDirectory.Create();
                pluginsDirectory.Refresh();
            }

            app.UseStaticFiles(new StaticFileOptions()
            {
                ServeUnknownFileTypes = true,
                OnPrepareResponse = (StaticFileResponseContext staticFileResponseContext) =>
                {
                    staticFileResponseContext.Context.Response.Headers.TryAdd("X-SRT-Host", FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, PluginHost.APP_EXE_NAME)).ProductVersion);
                },
                FileProvider = new CompositeFileProvider(
                    new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")),
                    new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "plugins"))
                    )
            });
            app.UseStaticFiles();

            app.UseRouting();
            app.UseCors("CORSPolicy");
            app.UseEndpoints(endpoints =>
            {
				endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });
        }
	}
}
