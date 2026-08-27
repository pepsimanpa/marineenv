using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Sources;
using MarineEnvironment.Sources.Fes2014;
using MarineEnvironment.Sources.NetCdf;
using MarineEnvironment.Sources.Shom;

namespace MarineEnvironment
{
    public sealed class MarineEnvironmentManager : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, IEnvironmentDataSource> _sources = new Dictionary<string, IEnvironmentDataSource>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public InitializationResult Initialize()
        {
            return Initialize(Path.Combine(AppContext.BaseDirectory, "marineenvironment.json"));
        }

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

        public SourceState LoadSource(DataSourceOption option)
        {
            if (option == null)
                throw new ArgumentNullException(nameof(option));
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
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source id is required.", nameof(sourceId));
            ThrowIfDisposed();

            lock (_sync)
            {
                IEnvironmentDataSource source;
                if (!_sources.TryGetValue(sourceId, out source))
                    return false;
                _sources.Remove(sourceId);
                source.Dispose();
                return true;
            }
        }

        public SourceState ReloadSource(DataSourceOption option)
        {
            return LoadSource(option);
        }

        public IReadOnlyList<SourceState> GetSources()
        {
            ThrowIfDisposed();
            lock (_sync)
                return _sources.Values.Select(ToState).OrderBy(x => x.Id).ToArray();
        }

        public SourceState? GetSourceStatus(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source id is required.", nameof(sourceId));
            ThrowIfDisposed();
            lock (_sync)
            {
                IEnvironmentDataSource source;
                return _sources.TryGetValue(sourceId, out source) ? ToState(source) : null;
            }
        }

        public EnvironmentQueryResult Query(EnvironmentQuery query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));
            ValidateQuery(query);
            ThrowIfDisposed();

            IEnvironmentDataSource[] snapshot;
            lock (_sync)
                snapshot = _sources.Values.Where(x => x.Status == SourceStatus.Ready).ToArray();

            var values = new List<EnvironmentValue>(snapshot.Length);
            foreach (var source in snapshot)
            {
                var value = source.Query(query);
                if (value != null)
                    values.Add(value);
            }

            return new EnvironmentQueryResult
            {
                RequestedLatitude = query.Latitude,
                RequestedLongitude = query.Longitude,
                RequestedDepth = query.Depth,
                RequestedDateTime = query.DateTime,
                Sampling = query.Sampling,
                Values = values.OrderBy(x => x.Type).ThenBy(x => x.SourceId).ToArray()
            };
        }

        public EnvironmentValue? Query(string sourceId, EnvironmentQuery query)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source id is required.", nameof(sourceId));
            if (query == null)
                throw new ArgumentNullException(nameof(query));
            ValidateQuery(query);
            ThrowIfDisposed();

            return GetReadySource(sourceId).Query(query);
        }

        public GridResult QueryGrid(string sourceId, GridQuery query)
        {
            if (string.IsNullOrWhiteSpace(sourceId))
                throw new ArgumentException("Source id is required.", nameof(sourceId));
            if (query == null)
                throw new ArgumentNullException(nameof(query));
            ValidateGridQuery(query);
            ThrowIfDisposed();

            return GetReadySource(sourceId).QueryGrid(query);
        }

        private IEnvironmentDataSource GetReadySource(string sourceId)
        {
            IEnvironmentDataSource source;
            lock (_sync)
            {
                if (!_sources.TryGetValue(sourceId, out source))
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
            if (option.Format == DataSourceFormat.NetCdf && string.IsNullOrWhiteSpace(option.Variable))
                throw new ArgumentException($"NetCDF data source '{option.Id}' requires a variable name.", nameof(option));
            if (option.Format == DataSourceFormat.Fes2014Current && option.Type != EnvironmentType.Current)
                throw new ArgumentException($"FES2014 current source '{option.Id}' must use type Current.", nameof(option));
            if (option.Format == DataSourceFormat.ShomSeabed && option.Type != EnvironmentType.Seabed)
                throw new ArgumentException($"SHOM seabed source '{option.Id}' must use type Seabed.", nameof(option));

            IEnvironmentDataSource existing;
            if (_sources.TryGetValue(option.Id, out existing))
            {
                _sources.Remove(option.Id);
                existing.Dispose();
            }

            IEnvironmentDataSource source;
            switch (option.Format)
            {
                case DataSourceFormat.NetCdf:
                    source = new NetCdfDataSource(option, resolvedPath);
                    break;
                case DataSourceFormat.Fes2014Current:
                    source = new Fes2014CurrentDataSource(option, resolvedPath);
                    break;
                case DataSourceFormat.ShomSeabed:
                    source = new ShomSeabedDataSource(option, resolvedPath);
                    break;
                default:
                    throw new NotSupportedException($"Data format '{option.Format}' is not supported yet.");
            }

            _sources.Add(option.Id, source);
        }

        private InitializationResult SnapshotInitializationResult()
        {
            return new InitializationResult
            {
                Sources = _sources.Values.Select(ToState).OrderBy(x => x.Id).ToArray()
            };
        }

        private static SourceState ToState(IEnvironmentDataSource source)
        {
            return new SourceState(source.Id, source.Type, source.Status, source.StatusMessage);
        }

        private static void ValidateQuery(EnvironmentQuery query)
        {
            if (query.Latitude < -90 || query.Latitude > 90)
                throw new ArgumentOutOfRangeException(nameof(query.Latitude), "Latitude must be between -90 and 90 degrees.");
            if (query.Longitude < -360 || query.Longitude > 360)
                throw new ArgumentOutOfRangeException(nameof(query.Longitude), "Longitude must be between -360 and 360 degrees.");
        }

        private static void ValidateGridQuery(GridQuery query)
        {
            if (query.MinLatitude < -90 || query.MinLatitude > 90 || query.MaxLatitude < -90 || query.MaxLatitude > 90)
                throw new ArgumentOutOfRangeException(nameof(query), "Latitude bounds must be between -90 and 90 degrees.");
            if (query.MinLongitude < -360 || query.MinLongitude > 360 || query.MaxLongitude < -360 || query.MaxLongitude > 360)
                throw new ArgumentOutOfRangeException(nameof(query), "Longitude bounds must be between -360 and 360 degrees.");
            if (query.MinLatitude >= query.MaxLatitude)
                throw new ArgumentException("MinLatitude must be less than MaxLatitude.", nameof(query));
            if (query.MinLongitude >= query.MaxLongitude)
                throw new ArgumentException("MinLongitude must be less than MaxLongitude.", nameof(query));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MarineEnvironmentManager));
        }

        private void ClearSources()
        {
            foreach (var source in _sources.Values)
                source.Dispose();
            _sources.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_sync)
            {
                if (_disposed)
                    return;
                ClearSources();
                _disposed = true;
            }
        }
    }
}
