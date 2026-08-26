using System.Collections.Generic;
using MarineEnvironment.Models;

namespace MarineEnvironment.Configuration
{
    public sealed class DataSourceOption
    {
        public string Id { get; init; } = string.Empty;
        public EnvironmentType Type { get; init; }
        public DataSourceFormat Format { get; init; } = DataSourceFormat.NetCdf;
        public bool Enabled { get; init; } = true;

        /// <summary>File path or directory path. Relative paths are resolved against the configuration file directory.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>Optional pattern for multi-file sources. Supports {MM}, e.g. woa23_..._t{MM}_04.nc.</summary>
        public string? FilePattern { get; init; }

        public string Variable { get; init; } = string.Empty;
        public string LatitudeVariable { get; init; } = "lat";
        public string LongitudeVariable { get; init; } = "lon";
        public string? DepthVariable { get; init; }
        public string? TimeVariable { get; init; }
        public string? Unit { get; init; }

        /// <summary>Maps query axes to the data variable's dimensions when automatic matching is insufficient.</summary>
        public Dictionary<string, string>? DimensionMap { get; init; }

        /// <summary>Optional source-specific metadata retained in query results.</summary>
        public Dictionary<string, string>? Metadata { get; init; }
    }

    public sealed class MarineEnvironmentOptions
    {
        public string Version { get; init; } = "1.0";
        public List<DataSourceOption> Sources { get; init; } = new List<DataSourceOption>();
    }
}
