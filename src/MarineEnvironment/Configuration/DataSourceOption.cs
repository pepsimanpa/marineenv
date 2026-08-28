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

    /// <summary>
    /// Project-derived seabed model combining an ETOPO bathymetry source and
    /// Martin et al. (2015) predicted porosity. This model produces estimated values only.
    /// </summary>
    public sealed class EstimatedSeabedModelOption
    {
        public bool Enabled { get; init; }
        public string Id { get; init; } = "ETOPO2022_MARTIN_SEABED_V1";
        public string TerrainSourceId { get; init; } = "ETOPO2022";
        public string PorositySourceId { get; init; } = "MARTIN2015_POROSITY";

        /// <summary>Odd-sized native ETOPO neighborhood used for slope/roughness. V1 uses 3x3.</summary>
        public int NeighborhoodSize { get; init; } = 3;

        /// <summary>Stride used while sampling ETOPO cells for regional P75/P95 calibration.</summary>
        public int CalibrationStride { get; init; } = 4;
        public double CalibrationMinLatitude { get; init; } = 32.0;
        public double CalibrationMaxLatitude { get; init; } = 43.0;
        public double CalibrationMinLongitude { get; init; } = 122.0;
        public double CalibrationMaxLongitude { get; init; } = 133.0;

        /// <summary>Regional Martin porosity anchors used to create the 0..1 mud tendency.</summary>
        public double PorosityLowPercent { get; init; } = 53.34;
        public double PorosityHighPercent { get; init; } = 71.96;

        /// <summary>
        /// Decision threshold for the ETOPO terrain RockIndex. This is a categorical decision
        /// threshold, not a rock percentage. V1 uses 0.5 on the normalized 0..1 terrain index.
        /// </summary>
        public double RockDecisionThreshold { get; init; } = 0.5;
    }

    public sealed class MarineEnvironmentOptions
    {
        public string Version { get; init; } = "1.0";
        public List<DataSourceOption> Sources { get; init; } = new List<DataSourceOption>();

        /// <summary>
        /// Defaults to the project V1 ETOPO2022 + Martin estimated-seabed model so existing
        /// configuration files gain the derived result when both required sources are READY.
        /// Set enabled=false explicitly to disable the derived estimate.
        /// </summary>
        public EstimatedSeabedModelOption? EstimatedSeabedModel { get; init; } = new EstimatedSeabedModelOption { Enabled = true };
    }
}
