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

        /// <summary>
        /// For SHOM worldwide sediment shapefiles, identifies the DBF field containing the
        /// seabed nature code. The official product uses 'typelem'.
        /// </summary>
        public string AttributeField { get; init; } = "typelem";

        /// <summary>
        /// Optional user-defined mapping table used to derive operational seabed and burial-rate
        /// values from the raw SHOM code. Relative paths are resolved against marineenvironment.json.
        /// This is intentionally separate from the source DB because these values are user rules,
        /// not values contained in the SHOM shapefile.
        /// </summary>
        public string? SeabedMappingPath { get; init; }

        /// <summary>
        /// For tidal-current sources, selects the constituent set used for synthesis.
        /// Major4 = M2/S2/K1/O1, Major6 adds N2/K2, Full uses all 34 FES2014 current constituents.
        /// </summary>
        public CurrentConstituentMode CurrentConstituentMode { get; init; } = CurrentConstituentMode.Major4;

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
