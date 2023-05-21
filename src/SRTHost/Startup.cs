using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

            IMvcBuilder mvcBuilder = services.AddRazorPages();
            Assembly[] assemblies = GetPrivateRazorAssemblies().ToArray();
            foreach (Assembly assembly in assemblies)
                mvcBuilder = mvcBuilder.AddApplicationPart(assembly);
            mvcBuilder.AddRazorRuntimeCompilation(
                opt =>
                {
                    opt.FileProviders.Clear();
                    foreach (Assembly assembly in assemblies)
                        opt.FileProviders.Add(new EmbeddedFileProvider(assembly));
                });
            services.AddServerSideBlazor();

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

            app.UseStaticFiles(new StaticFileOptions()
            {
                ServeUnknownFileTypes = true,
                OnPrepareResponse = (StaticFileResponseContext staticFileResponseContext) =>
                {
                    staticFileResponseContext.Context.Response.Headers.TryAdd("X-SRT-Host", FileVersionInfo.GetVersionInfo(Path.Combine(AppContext.BaseDirectory, PluginHost.APP_EXE_NAME)).ProductVersion);
                }
            });
            app.UseRouting();
            app.UseCors("CORSPolicy");
            app.UseEndpoints(endpoints =>
            {
				endpoints.MapBlazorHub();
                endpoints.MapFallbackToPage("/_Host");
                endpoints.MapControllers();
            });
        }

        private IEnumerable<Assembly> GetPrivateRazorAssemblies()
        {
            DirectoryInfo dir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "plugins"));
            if (dir.Exists)
                foreach (var file in dir.EnumerateFiles("*.dll", SearchOption.AllDirectories).Where(d => string.Equals(Path.GetFileNameWithoutExtension(d.Name), d.Directory?.Name ?? string.Empty, StringComparison.Ordinal)))
                    yield return AssemblyLoadContext.Default.LoadFromAssemblyPath(file?.FullName ?? string.Empty);
        }
    }
}
