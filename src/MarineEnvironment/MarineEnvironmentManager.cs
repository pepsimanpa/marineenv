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
    public sealed partial class MarineEnvironmentManager : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, IEnvironmentDataSource> _sources = new Dictionary<string, IEnvironmentDataSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SeabedMappingLookup> _seabedMappings = new Dictionary<string, SeabedMappingLookup>(StringComparer.OrdinalIgnoreCase);
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
                ConfigureEstimatedSeabed(options.EstimatedSeabedModel);
                foreach (var option in options.Sources)
                {
                    var resolvedPath = ConfigurationLoader.ResolveSourcePath(option.Path, configPath);
                    var resolvedMappingPath = string.IsNullOrWhiteSpace(option.SeabedMappingPath)
                        ? null
                        : ConfigurationLoader.ResolveSourcePath(option.SeabedMappingPath!, configPath);
                    AddOrReplaceSource(option, resolvedPath, resolvedMappingPath);
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
            var resolvedMappingPath = string.IsNullOrWhiteSpace(option.SeabedMappingPath)
                ? null
                : Path.GetFullPath(option.SeabedMappingPath!);
            lock (_sync)
            {
                AddOrReplaceSource(option, resolvedPath, resolvedMappingPath);
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
                _seabedMappings.Remove(sourceId);
                source.Dispose();
                ResetEstimatedSeabedCalibration();
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

            IEnvironmentDataSource[] sourceSnapshot;
            Dictionary<string, SeabedMappingLookup> mappingSnapshot;
            lock (_sync)
            {
                sourceSnapshot = _sources.Values.Where(x => x.Status == SourceStatus.Ready).ToArray();
                mappingSnapshot = new Dictionary<string, SeabedMappingLookup>(_seabedMappings, StringComparer.OrdinalIgnoreCase);
            }

            var values = new List<EnvironmentValue>(sourceSnapshot.Length + 1);
            foreach (var source in sourceSnapshot)
            {
                var value = source.Query(query);
                if (value != null)
                    values.Add(ApplySeabedMapping(value, mappingSnapshot));
            }

            AppendEstimatedSeabedCategorical(values, sourceSnapshot, query);

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

            var value = GetReadySource(sourceId).Query(query);
            if (value == null)
                return null;

            SeabedMappingLookup? mapping;
            lock (_sync)
                _seabedMappings.TryGetValue(sourceId, out mapping);

            if (mapping == null)
                return value;

            var one = new Dictionary<string, SeabedMappingLookup>(StringComparer.OrdinalIgnoreCase)
            {
                [sourceId] = mapping
            };
            return ApplySeabedMapping(value, one);
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

        private static EnvironmentValue ApplySeabedMapping(EnvironmentValue value, IReadOnlyDictionary<string, SeabedMappingLookup> mappings)
        {
            if (!(value.Value is SeabedValue seabed))
                return value;
            if (!mappings.TryGetValue(value.SourceId, out var mapping))
                return value;
            if (!mapping.TryGet(seabed.Code, out var rule))
                return value;

            var derived = new SeabedDerivedValue
            {
                MappingTableId = mapping.Table.Id,
                ShomOriginalClassification = rule.ShomOriginalClassification,
                PrimaryClassification = rule.PrimaryClassification,
                Seabed = rule.Seabed,
                MudPercent = rule.MudPercent,
                SandPercent = rule.SandPercent,
                BurialRatePercent = rule.BurialRatePercent
            };

            var enriched = new SeabedValue
            {
                Code = seabed.Code,
                Name = seabed.Name,
                SedimentClass = seabed.SedimentClass,
                SourceMapNumber = seabed.SourceMapNumber,
                Derived = derived
            };

            var metadata = value.Metadata != null
                ? value.Metadata.ToDictionary(x => x.Key, x => x.Value)
                : new Dictionary<string, object?>();
            metadata["derivedMappingTable"] = derived.MappingTableId;
            metadata["derivedShomOriginalClassification"] = derived.ShomOriginalClassification;
            metadata["derivedPrimaryClassification"] = derived.PrimaryClassification;
            metadata["derivedSeabed"] = derived.Seabed;
            metadata["derivedMudPercent"] = derived.MudPercent;
            metadata["derivedSandPercent"] = derived.SandPercent;
            metadata["derivedBurialRatePercent"] = derived.BurialRatePercent;

            return value with { Value = enriched, Metadata = metadata };
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

        private void AddOrReplaceSource(DataSourceOption option, string resolvedPath, string? resolvedMappingPath)
        {
            if (string.IsNullOrWhiteSpace(option.Id))
                throw new ArgumentException("A data source id is required.", nameof(option));
            if (option.Format == DataSourceFormat.NetCdf && string.IsNullOrWhiteSpace(option.Variable))
                throw new ArgumentException($"NetCDF data source '{option.Id}' requires a variable name.", nameof(option));
            if (option.Format == DataSourceFormat.Fes2014Current && option.Type != EnvironmentType.Current)
                throw new ArgumentException($"FES2014 current source '{option.Id}' must use type Current.", nameof(option));
            if (option.Format == DataSourceFormat.ShomSeabed && option.Type != EnvironmentType.Seabed)
                throw new ArgumentException($"SHOM seabed source '{option.Id}' must use type Seabed.", nameof(option));
            if (!string.IsNullOrWhiteSpace(option.SeabedMappingPath) && option.Format != DataSourceFormat.ShomSeabed)
                throw new ArgumentException($"seabedMappingPath is currently supported only by ShomSeabed sources ('{option.Id}').", nameof(option));

            var mapping = resolvedMappingPath == null ? null : SeabedMappingTableLoader.Load(resolvedMappingPath);

            IEnvironmentDataSource existing;
            if (_sources.TryGetValue(option.Id, out existing))
            {
                _sources.Remove(option.Id);
                _seabedMappings.Remove(option.Id);
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
            if (mapping != null)
                _seabedMappings[option.Id] = mapping;
            ResetEstimatedSeabedCalibration();
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
            _seabedMappings.Clear();
            ResetEstimatedSeabedCalibration();
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
