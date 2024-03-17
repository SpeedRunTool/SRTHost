using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase.Implementations;
using SRTPluginBase.Interfaces;
using System;
using System.Diagnostics;
using System.Text.Json;
using static MudBlazor.CategoryTypes;

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
                    Hosts = new IMainHostEntry[]
                    {
                        new MainHostEntry()
                        {
                            Name = "SRTHost",
                            DisplayName = "SRT Host",
                            Description = "A plugin host for various informational SpeedRun Tools.",
                            RepoURL = new Uri("https://github.com/SpeedRunTool/SRTHost"),
                            ManifestURL = new Uri("https://raw.githubusercontent.com/SpeedRunTool/SRTHost/develop/manifest.json")
                        }
                    },
                    Plugins = new IMainPluginEntry[]
                    {
                            new MainPluginEntry()
                            {
                                Name = "SRTProducerTest",
                                DisplayName = "Test Producer",
                                Description = "A plugin for Test that produces values from in-game memory for consumers.",
                                Platform = MainPluginPlatformEnum.x64,
                                Type = MainPluginTypeEnum.Producer,
                                Tags = new string[] { "Producer" },
                                RepoURL = new Uri("https://github.com/TestAuthor1/SRTProducerTest1"),
                                ManifestURL = new Uri("https://raw.githubusercontent.com/TestAuthor1/SRTProducerTest1/main/manifest.json")
                            },
                            new MainPluginEntry()
                            {
                                Name = "SRTConsumerTestUIDirectX",
                                DisplayName = "Test Consumer (DirectX)",
                                Description = "A plugin for Test that displays in-game data within a DirectX Overlay.",
                                Platform = MainPluginPlatformEnum.x64,
                                Type = MainPluginTypeEnum.Consumer,
                                Tags = new string[] { "Consumer", "UI", "Overlay", "DirectX" },
                                RepoURL = new Uri("https://github.com/TestAuthor1/SRTConsumerTest1"),
                                ManifestURL = new Uri("https://raw.githubusercontent.com/TestAuthor1/SRTConsumerTest1/main/manifest.json")
                            },
                            new MainPluginEntry()
                            {
                                Name = "SRTConsumerTestUIWinForms",
                                DisplayName = "Test Consumer (WinForms)",
                                Description = "A plugin for Test that displays in-game data within a Win32 WinForm.",
                                Platform = MainPluginPlatformEnum.x64,
                                Type = MainPluginTypeEnum.Consumer,
                                Tags = new string[] { "Consumer", "UI", "WinForms" },
                                RepoURL = new Uri("https://github.com/TestAuthor1/SRTConsumerTest2"),
                                ManifestURL = new Uri("https://raw.githubusercontent.com/TestAuthor1/SRTConsumerTest2/main/manifest.json")
                            },
                    }
                },
                new JsonSerializerOptions()
                {
                    WriteIndented = true
                })
            {

            };
        }

        // GET: api/v1/Debug/GenerateManifestHost
        [HttpGet("GenerateManifestHost", Name = "DebugGenerateManifestHostGet")]
        public IActionResult DebugGenerateManifestHostGet()
        {
            LogDebugGenerateManifestHostGet();

            return new JsonResult(
                new ManifestEntryJson()
                {
                    Contributors = new string[]
                    {
                        "Squirrelies"
                    },
                    Releases = new IManifestReleaseJson[]
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
                new ManifestEntryJson()
                {
                    Contributors = new string[]
                    {
                        "TestAuthor1",
                        "TestAuthor2"
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
