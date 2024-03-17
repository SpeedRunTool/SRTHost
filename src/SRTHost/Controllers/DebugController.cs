using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase;
using System;
using System.Diagnostics;
using System.Text.Json;

namespace SRTHost.Controllers
{
    [ApiController]
    [Route("api/v1/[controller]")]
    [EnableCors("CORSPolicy")]
    public partial class DebugController : Controller
    {
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly ILogger<DebugController> logger;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private readonly PluginHost pluginHost;

        public DebugController(ILogger<DebugController> logger, PluginHost pluginHost)
        {
            this.logger = logger;
            this.pluginHost = pluginHost;
        }

        // GET: api/v1/Debug/GenerateMainJson
        [HttpGet("GenerateMainJson", Name = "DebugGenerateMainJsonGet")]
        public IActionResult DebugGenerateMainJsonGet()
        {
            LogDebugGenerateMainJsonGet();

            return new JsonResult(
                new MainJson()
                {
                    Host = new MainHostEntry()
                    {
                        ManifestURL = new Uri("https://raw.githubusercontent.com/SpeedRunTool/SRTHost/develop/main.json")
                    },
                    Plugins = new MainPluginEntry[]
                    {
                            new MainPluginEntry()
                            {
                                Name = "SRTProducerTest1",
                                Type = MainPluginTypeEnum.Producer,
                                Platform = MainPluginPlatformEnum.x64,
                                ManifestURL = new Uri("https://raw.githubusercontent.com/TestAuthor1/SRTProducerTest1/main/manifest.json")
                            },
                            new MainPluginEntry()
                            {
                                Name = "SRTConsumerTest1",
                                Type = MainPluginTypeEnum.Consumer,
                                Platform = MainPluginPlatformEnum.x64,
                                ManifestURL = new Uri("https://raw.githubusercontent.com/TestAuthor1/SRTConsumerTest1/main/manifest.json")
                            },
                            new MainPluginEntry()
                            {
                                Name = "SRTConsumerTest2",
                                Type = MainPluginTypeEnum.Consumer,
                                Platform = MainPluginPlatformEnum.x64,
                                ManifestURL = new Uri("https://raw.githubusercontent.com/TestAuthor1/SRTConsumerTest2/main/manifest.json")
                            },
                    }
                },
                new JsonSerializerOptions()
                {
                    WriteIndented = true
                });
        }


        // GET: api/v1/Debug/GenerateManifestHost
        [HttpGet("GenerateManifestHost", Name = "DebugGenerateManifestHostGet")]
        public IActionResult DebugGenerateManifestHostGet()
        {
            LogDebugGenerateManifestHostGet();

            return new JsonResult(
                new ManifestEntryJson()
                {
                    Releases = new ManifestReleaseJson[]
                    {
                        new ManifestReleaseJson()
                        {
                            Version = "1.0.0.1",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/1.0.0.1/SRTHost-v1.0.0.1.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "1.0.0.0",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/1.0.0.0/SRTHost-v1.0.0.0.zip")
                        }
                    }
                },
                new JsonSerializerOptions()
                {
                    WriteIndented = true
                });
        }

        // GET: api/v1/Debug/GenerateManifestPlugin
        [HttpGet("GenerateManifestPlugin", Name = "DebugGenerateManifestPluginGet")]
        public IActionResult DebugGenerateManifestPluginGet()
        {
            return new JsonResult(
                new ManifestPluginJson()
                {
                    Contributors = new string[]
                    {
                        "TestAuthor1",
                        "TestAuthor2"
                    },
                    Tags = new string[]
                    {
                        "Consumer",
                        "UI",
                        "Overlay",
                        "DirectX",
                    },
                    Releases = new ManifestReleaseJson[]
                    {
                        new ManifestReleaseJson()
                        {
                            Version = "1.0.0.1",
                            DownloadURL = new Uri("https://github.com/TestAuthor1/SRTConsumerTest1/releases/download/1.0.0.1/SRTConsumerTest1-v1.0.0.1.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "1.0.0.0",
                            DownloadURL = new Uri("https://github.com/TestAuthor1/SRTConsumerTest1/releases/download/1.0.0.0/SRTConsumerTest1-v1.0.0.0.zip")
                        }
                    }
                },
                new JsonSerializerOptions()
                {
                    WriteIndented = true
                });
        }
    }
}
