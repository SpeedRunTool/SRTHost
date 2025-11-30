using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SRTPluginBase.Implementations;
using SRTPluginBase.Interfaces;
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
                    Hosts = new MainHostEntry[]
                    {
                        new MainHostEntry()
                        {
                            Name = "SRTHost",
                            DisplayName = "SRT Host",
                            Description = "A plugin host for various informational SpeedRun Tools.",
                            RepoURL = new Uri("https://github.com/SpeedRunTool/SRTHost"),
                            ManifestURL = new Uri("https://127.0.0.1:7192/api/v1/Debug/GenerateManifestHost", UriKind.Absolute)
                            //ManifestURL = new Uri("https://raw.githubusercontent.com/SpeedRunTool/SRTHost/main/manifest.json")
                        },
                        new MainHostEntry()
                        {
                            Name = "SRTHost",
                            DisplayName = "SRT Host (Beta)",
                            Description = "A plugin host for various informational SpeedRun Tools. This is a beta version and may not work as intended.",
                            RepoURL = new Uri("https://github.com/SpeedRunTool/SRTHost"),
                            ManifestURL = new Uri("https://127.0.0.1:7192/api/v1/Debug/GenerateManifestHost", UriKind.Absolute)
                            //ManifestURL = new Uri("https://raw.githubusercontent.com/SpeedRunTool/SRTHost/develop/manifest.json")
                        }
                    },
                    Plugins = new MainPluginEntry[]
                    {
                        new MainPluginEntry()
                        {
                            Name = "SRTPluginProviderRE2",
                            DisplayName = "Resident Evil 2 (2019) (Beta)",
                            Description = "A producer plugin for Resident Evil 2 (2019) that produces values from in-game memory for consumers.",
                            Platform = MainPluginPlatformEnum.x64,
                            Type = MainPluginTypeEnum.Producer,
                            Tags = new string[] { "Producer" },
                            RepoURL = new Uri("https://github.com/SpeedrunTooling/SRTPluginProviderRE2"),
                            ManifestURL = new Uri("https://127.0.0.1:7192/api/v1/Debug/GenerateManifestPlugin", UriKind.Absolute)
                            //ManifestURL = new Uri("https://raw.githubusercontent.com/SpeedrunTooling/SRTPluginProviderRE2/develop/manifest.json")
                        },
                        new MainPluginEntry()
                        {
                            Name = "SRTPluginUIRE2DirectXOverlay",
                            DisplayName = "Resident Evil 2 (2019) DirectX Overlay (Beta)",
                            Description = "A consumer plugin for Resident Evil 2 (2019) that displays in-game data within a DirectX Overlay.",
                            Platform = MainPluginPlatformEnum.x64,
                            Type = MainPluginTypeEnum.Consumer,
                            Tags = new string[] { "Consumer", "UI", "Overlay", "DirectX" },
                            RepoURL = new Uri("https://github.com/SpeedrunTooling/SRTPluginUIRE2DirectXOverlay"),
                            ManifestURL = new Uri("https://127.0.0.1:7192/api/v1/Debug/GenerateManifestPlugin", UriKind.Absolute)
                            //ManifestURL = new Uri("https://raw.githubusercontent.com/SpeedrunTooling/SRTPluginUIRE2DirectXOverlay/develop/manifest.json")
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
                    Releases = new ManifestReleaseJson[]
                    {
                        new ManifestReleaseJson()
                        {
                            Version = "3.1.0.1",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/3.1.0.1/SRTHost-v3.1.0.1.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "3.0.0.3",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/3.0.0.3/SRTHost-v3.0.0.3.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "3.0.0.2",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/3.0.0.2/SRTHost-v3.0.0.2.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "3.0.0.1",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/3.0.0.1/SRTHost-v3.0.0.1.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "3.0.0.0",
                            DownloadURL = new Uri("https://github.com/SpeedRunTool/SRTHost/releases/download/3.0.0.0/SRTHost-v3.0.0.0.zip")
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
                        "Squirrelies",
                        "VideoGameRoulette"
                    },
                    Releases = new ManifestReleaseJson[]
                    {
                        new ManifestReleaseJson()
                        {
                            Version = "2.0.2.0",
                            DownloadURL = new Uri("https://github.com/SpeedrunTooling/SRTPluginProviderRE2/releases/download/2.0.2.0/SRTPluginProviderRE2-v2.0.2.0.zip")
                        },
                        new ManifestReleaseJson()
                        {
                            Version = "2.0.0.8",
                            DownloadURL = new Uri("https://github.com/SpeedrunTooling/SRTPluginProviderRE2/releases/download/2.0.0.8/SRTPluginProviderRE2-v2.0.0.8.zip")
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
