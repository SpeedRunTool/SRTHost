using System;

namespace SRTHost.Structures;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
public class MainPluginEntry
{
    public string Name { get; set; }
    public MainPluginPlatformEnum Platform { get; set; }
    public MainPluginTypeEnum Type { get; set; }
    public Uri ManifestURL { get; set; }
}
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

