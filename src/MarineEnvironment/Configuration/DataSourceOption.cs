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
        public string Path { get; init; } = string.Empty;
        public string? FilePattern { get; init; }
        public string Variable { get; init; } = string.Empty;
        public string LatitudeVariable { get; init; } = "lat";
        public string LongitudeVariable { get; init; } = "lon";
        public string? DepthVariable { get; init; }
        public string? TimeVariable { get; init; }
        public string? Unit { get; init; }
        public string AttributeField { get; init; } = "typelem";
        public string? SeabedMappingPath { get; init; }
        public CurrentConstituentMode CurrentConstituentMode { get; init; } = CurrentConstituentMode.Major4;
        public Dictionary<string, string>? DimensionMap { get; init; }
        public Dictionary<string, string>? Metadata { get; init; }
    }

    public sealed class EstimatedSeabedModelOption
    {
        public bool Enabled { get; init; }
        public string Id { get; init; } = "ETOPO2022_MARTIN_SEABED_V1";
        public string TerrainSourceId { get; init; } = "ETOPO2022";
        public string PorositySourceId { get; init; } = "MARTIN2015_POROSITY";
        public int NeighborhoodSize { get; init; } = 3;
        public int CalibrationStride { get; init; } = 4;
        public double CalibrationMinLatitude { get; init; } = 32.0;
        public double CalibrationMaxLatitude { get; init; } = 43.0;
        public double CalibrationMinLongitude { get; init; } = 122.0;
        public double CalibrationMaxLongitude { get; init; } = 133.0;
        public double PorosityLowPercent { get; init; } = 53.34;
        public double PorosityHighPercent { get; init; } = 71.96;
        public double RockDecisionThreshold { get; init; } = 0.5;
    }

    public sealed class MarineEnvironmentOptions
    {
        public string Version { get; init; } = "1.0";
        public List<DataSourceOption> Sources { get; init; } = new List<DataSourceOption>();
        public EstimatedSeabedModelOption? EstimatedSeabedModel { get; init; } = new EstimatedSeabedModelOption { Enabled = true };
    }
}
