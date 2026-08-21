namespace MarineEnvironment.Models;

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
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public double? Depth { get; init; }
    public DateTime? DateTime { get; init; }
    public SpatialSampling Sampling { get; init; } = SpatialSampling.Nearest;
}

/// <summary>
/// Requests a regularly sampled two-dimensional view over a source-native grid.
/// Values are currently sampled with nearest-neighbour lookup; the source itself
/// is not resampled or rewritten on disk.
/// </summary>
public sealed class GridQuery
{
    public required double MinLatitude { get; init; }
    public required double MaxLatitude { get; init; }
    public required double MinLongitude { get; init; }
    public required double MaxLongitude { get; init; }
    public double? Depth { get; init; }
    public DateTime? DateTime { get; init; }
    public int Width { get; init; } = 480;
    public int Height { get; init; } = 300;
    public SpatialSampling Sampling { get; init; } = SpatialSampling.Nearest;
}

public sealed class GridResult
{
    public required string SourceId { get; init; }
    public required EnvironmentType Type { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Latitude represented by each raster row, north to south.</summary>
    public required double[] Latitudes { get; init; }

    /// <summary>Longitude represented by each raster column, west to east.</summary>
    public required double[] Longitudes { get; init; }

    /// <summary>Row-major values. Null represents a source fill/missing value.</summary>
    public required double?[] Values { get; init; }

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
    public required IReadOnlyList<SourceState> Sources { get; init; }
    public bool Success => Sources.All(x => x.Status is SourceStatus.Ready or SourceStatus.Disabled);
}
