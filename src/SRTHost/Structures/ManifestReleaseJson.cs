using System;

namespace SRTHost.Structures
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public class ManifestReleaseJson
    {
        public string Version { get; set; }
        public Uri DownloadURL { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
}
