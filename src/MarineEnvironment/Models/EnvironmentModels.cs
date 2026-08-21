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
