using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Sources;
using MarineEnvironment.Sources.NetCdf;

namespace MarineEnvironment;

public sealed class MarineEnvironmentManager : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IEnvironmentDataSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public InitializationResult Initialize()
        => Initialize(Path.Combine(AppContext.BaseDirectory, "marineenvironment.json"));

    public InitializationResult Initialize(string configPath)
    {
        ThrowIfDisposed();
        var options = ConfigurationLoader.Load(configPath);

        lock (_sync)
        {
            ClearSources();
            foreach (var option in options.Sources)
            {
                var resolvedPath = ConfigurationLoader.ResolveSourcePath(option.Path, configPath);
                AddOrReplaceSource(option, resolvedPath);
            }
            return SnapshotInitializationResult();
        }
    }

    /// <summary>Registers or replaces a source at runtime. Relative paths are resolved against the current process directory.</summary>
    public SourceState LoadSource(DataSourceOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        ThrowIfDisposed();

        var resolvedPath = Path.GetFullPath(option.Path);
        lock (_sync)
        {
            AddOrReplaceSource(option, resolvedPath);
            return ToState(_sources[option.Id]);
        }
    }

    public bool UnloadSource(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ThrowIfDisposed();

        lock (_sync)
        {
            if (!_sources.Remove(sourceId, out var source))
                return false;
            source.Dispose();
            return true;
        }
    }

    public SourceState ReloadSource(DataSourceOption option) => LoadSource(option);

    public IReadOnlyList<SourceState> GetSources()
    {
        ThrowIfDisposed();
        lock (_sync)
            return _sources.Values.Select(ToState).OrderBy(x => x.Id).ToArray();
    }

    public SourceState? GetSourceStatus(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ThrowIfDisposed();
        lock (_sync)
            return _sources.TryGetValue(sourceId, out var source) ? ToState(source) : null;
    }

    public IReadOnlyList<EnvironmentValue> Query(EnvironmentQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        ThrowIfDisposed();

        IEnvironmentDataSource[] snapshot;
        lock (_sync)
            snapshot = _sources.Values.Where(x => x.Status == SourceStatus.Ready).ToArray();

        var results = new List<EnvironmentValue>(snapshot.Length);
        foreach (var source in snapshot)
        {
            var value = source.Query(query);
            if (value is not null)
                results.Add(value);
        }
        return results;
    }

    public EnvironmentValue? Query(string sourceId, EnvironmentQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);
        ThrowIfDisposed();

        var source = GetReadySource(sourceId);
        return source.Query(query);
    }

    /// <summary>
    /// Returns a regularly sampled 2-D grid for visualization and validation.
    /// The source file remains in its native grid; this API only samples values for the requested view.
    /// </summary>
    public GridResult QueryGrid(string sourceId, GridQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentNullException.ThrowIfNull(query);
        ValidateGridQuery(query);
        ThrowIfDisposed();

        var source = GetReadySource(sourceId);
        return source.QueryGrid(query);
    }

    private IEnvironmentDataSource GetReadySource(string sourceId)
    {
        IEnvironmentDataSource source;
        lock (_sync)
        {
            if (!_sources.TryGetValue(sourceId, out source!))
                throw new KeyNotFoundException($"MarineEnvironment source '{sourceId}' is not registered.");
        }

        if (source.Status != SourceStatus.Ready)
            throw new InvalidOperationException($"Source '{sourceId}' is not ready: {source.Status} - {source.StatusMessage}");
        return source;
    }

    private void AddOrReplaceSource(DataSourceOption option, string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(option.Id))
            throw new ArgumentException("A data source id is required.", nameof(option));
        if (string.IsNullOrWhiteSpace(option.Variable))
            throw new ArgumentException($"Data source '{option.Id}' requires a NetCDF variable name.", nameof(option));

        if (_sources.Remove(option.Id, out var existing))
            existing.Dispose();

        IEnvironmentDataSource source = option.Format switch
        {
            DataSourceFormat.NetCdf => new NetCdfDataSource(option, resolvedPath),
            _ => throw new NotSupportedException($"Data format '{option.Format}' is not supported yet.")
        };

        _sources.Add(option.Id, source);
    }

    private InitializationResult SnapshotInitializationResult()
        => new() { Sources = _sources.Values.Select(ToState).OrderBy(x => x.Id).ToArray() };

    private static SourceState ToState(IEnvironmentDataSource source)
        => new(source.Id, source.Type, source.Status, source.StatusMessage);

    private static void ValidateQuery(EnvironmentQuery query)
    {
        if (query.Latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(query.Latitude), "Latitude must be between -90 and 90 degrees.");
        if (query.Longitude is < -360 or > 360)
            throw new ArgumentOutOfRangeException(nameof(query.Longitude), "Longitude must be between -360 and 360 degrees.");
    }

    private static void ValidateGridQuery(GridQuery query)
    {
        if (query.MinLatitude is < -90 or > 90 || query.MaxLatitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(query), "Latitude bounds must be between -90 and 90 degrees.");
        if (query.MinLongitude is < -360 or > 360 || query.MaxLongitude is < -360 or > 360)
            throw new ArgumentOutOfRangeException(nameof(query), "Longitude bounds must be between -360 and 360 degrees.");
        if (query.MinLatitude >= query.MaxLatitude)
            throw new ArgumentException("MinLatitude must be less than MaxLatitude.", nameof(query));
        if (query.MinLongitude >= query.MaxLongitude)
            throw new ArgumentException("MinLongitude must be less than MaxLongitude.", nameof(query));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ClearSources()
    {
        foreach (var source in _sources.Values)
            source.Dispose();
        _sources.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sync)
        {
            if (_disposed) return;
            ClearSources();
            _disposed = true;
        }
    }
}
