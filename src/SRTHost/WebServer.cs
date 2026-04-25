using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SRTPluginBase.Interfaces;

namespace SRTHost
{
    public partial class WebServer : IHostedService
    {
        private readonly IServiceProvider serviceProvider;
        private readonly IConfiguration configuration;
        private readonly IPluginHost pluginHost;

        public WebServer(ILogger<WebServer> logger, IServiceProvider serviceProvider, IConfiguration configuration, IPluginHost pluginHost)
        {
            this.logger = logger;
            this.serviceProvider = serviceProvider;
            this.configuration = configuration;
            this.pluginHost = pluginHost;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
        }
	}
}
