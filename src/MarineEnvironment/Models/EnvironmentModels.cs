using System;
using System.Collections.Generic;
using System.Linq;

namespace MarineEnvironment.Models
{
    public enum EnvironmentType
    {
        Bathymetry,
        Temperature,
        Salinity,
        Current,
        Turbidity,
        Seabed,
        BurialRate,
        ContactDensity,
        SeabedRoughness,
        Unknown
    }

    public enum DataSourceFormat
    {
        NetCdf
    }

    public enum SourceStatus
    {
        NotInitialized,
        Ready,
        Disabled,
        FileNotFound,
        InvalidConfiguration,
        NativeLibraryUnavailable,
        Error
    }

    public enum SpatialSampling
    {
        Nearest
    }

    public sealed class EnvironmentQuery
    {
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public double? Depth { get; init; }
        public DateTime? DateTime { get; init; }
        public SpatialSampling Sampling { get; init; } = SpatialSampling.Nearest;
    }

    public sealed class GridQuery
    {
        public double MinLatitude { get; init; }
        public double MaxLatitude { get; init; }
        public double MinLongitude { get; init; }
        public double MaxLongitude { get; init; }
        public double? Depth { get; init; }
        public DateTime? DateTime { get; init; }
        public int Width { get; init; } = 480;
        public int Height { get; init; } = 300;
        public SpatialSampling Sampling { get; init; } = SpatialSampling.Nearest;
    }

    public sealed class GridResult
    {
        public string SourceId { get; init; } = string.Empty;
        public EnvironmentType Type { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double[] Latitudes { get; init; } = Array.Empty<double>();
        public double[] Longitudes { get; init; } = Array.Empty<double>();
        public double?[] Values { get; init; } = Array.Empty<double?>();
        public string? Unit { get; init; }
        public double? Depth { get; init; }
        public DateTime? DateTime { get; init; }
        public string? Variable { get; init; }
        public double? Minimum { get; init; }
        public double? Maximum { get; init; }
        public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

        public double? GetValue(int row, int column)
        {
            if ((uint)row >= (uint)Height)
                throw new ArgumentOutOfRangeException(nameof(row));
            if ((uint)column >= (uint)Width)
                throw new ArgumentOutOfRangeException(nameof(column));
            return Values[(row * Width) + column];
        }
    }

    public sealed record EnvironmentValue(
        string SourceId,
        EnvironmentType Type,
        object? Value,
        string? Unit,
        double Latitude,
        double Longitude,
        double? Depth,
        DateTime? DateTime,
        string? Variable = null,
        IReadOnlyDictionary<string, object?>? Metadata = null);

    public sealed record SourceState(
        string Id,
        EnvironmentType Type,
        SourceStatus Status,
        string? Message = null);

    public sealed class InitializationResult
    {
        public IReadOnlyList<SourceState> Sources { get; init; } = Array.Empty<SourceState>();
        public bool Success => Sources.All(x => x.Status == SourceStatus.Ready || x.Status == SourceStatus.Disabled);
    }
}
