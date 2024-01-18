using System;
using System.Collections.Generic;

namespace SRTHost.Structures
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public class ManifestPluginJson
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Uri RepoURL { get; set; }
        public IEnumerable<string> Contributors { get; set; }
        public IEnumerable<string> Tags { get; set; }
        public IEnumerable<ManifestReleaseJson> Releases { get; set; }
    }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
}
