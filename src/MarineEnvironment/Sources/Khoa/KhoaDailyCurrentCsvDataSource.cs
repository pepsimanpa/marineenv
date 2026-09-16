using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;

namespace MarineEnvironment.Sources.Khoa
{
    /// <summary>
    /// Reads the Ministry of Oceans and Fisheries / KHOA numerical tidal-current CSV archive.
    /// The public yearly files are not guaranteed to be date ordered and a single search date
    /// may cover only part of the overall point set. This reader therefore indexes each physical
    /// source point as a time series and composes a requested day from the nearest available
    /// record at each point within a configurable temporal tolerance.
    /// </summary>
    internal sealed class KhoaDailyCurrentCsvDataSource : IEnvironmentDataSource
    {
        private const int MaxCachedYears = 2;
        private const int MaxCachedComposites = 6;
        private const double BucketSizeDegrees = 0.25;
        private const double CanonicalPointToleranceMeters = 5.0;
        private const double CanonicalPointToleranceKm = CanonicalPointToleranceMeters / 1000.0;

        private readonly DataSourceOption _option;
        private readonly string _rootPath;
        private readonly string _filePattern;
        private readonly Dictionary<int, string> _yearFiles = new Dictionary<int, string>();
        private readonly object _cacheSync = new object();
        private readonly object _canonicalSync = new object();

        private readonly Dictionary<int, YearIndex> _yearCache = new Dictionary<int, YearIndex>();
        private readonly LinkedList<int> _yearCacheLru = new LinkedList<int>();
        private readonly Dictionary<DateTime, CompositeData> _compositeCache = new Dictionary<DateTime, CompositeData>();
        private readonly LinkedList<DateTime> _compositeCacheLru = new LinkedList<DateTime>();

        private readonly List<CanonicalPoint> _canonicalPoints = new List<CanonicalPoint>();
        private readonly Dictionary<(double Latitude, double Longitude), int> _rawCoordinateToCanonical
            = new Dictionary<(double Latitude, double Longitude), int>();
        private bool _canonicalCatalogInitialized;
        private int _ambiguousCanonicalMatchCount;

        private readonly double _maxNearestDistanceKm;
        private readonly int _maxTemporalOffsetDays;

        public KhoaDailyCurrentCsvDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option ?? throw new ArgumentNullException(nameof(option));
            _rootPath = resolvedPath ?? throw new ArgumentNullException(nameof(resolvedPath));
            _filePattern = string.IsNullOrWhiteSpace(option.FilePattern)
                ? "해양수산부_지능형해상교통정보_수치조류도_{YYYY}.csv"
                : option.FilePattern!;
            _maxNearestDistanceKm = option.MaxNearestDistanceKm ?? 30.0;
            _maxTemporalOffsetDays = option.MaxTemporalOffsetDays ?? 7;

            if (!option.Enabled)
            {
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                if (_maxTemporalOffsetDays < 0)
                    throw new InvalidDataException("KHOA maxTemporalOffsetDays must be zero or greater.");
                if (!Directory.Exists(_rootPath))
                    throw new DirectoryNotFoundException($"KHOA tidal-current CSV directory was not found: {_rootPath}");
                if (_filePattern.IndexOf("{YYYY}", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("KHOA tidal-current filePattern must contain the {YYYY} token.");

                DiscoverYearFiles();
                if (_yearFiles.Count == 0)
                    throw new FileNotFoundException($"No KHOA tidal-current CSV files matched '{_filePattern}' under '{_rootPath}'.");

                var minYear = _yearFiles.Keys.Min();
                var maxYear = _yearFiles.Keys.Max();
                Status = SourceStatus.Ready;
                StatusMessage =
                    $"{_yearFiles.Count} yearly CSV file(s), {minYear}-{maxYear} / per-point nearest-date composite ±{_maxTemporalOffsetDays} day(s) / canonical identity {CanonicalPointToleranceMeters:0.#} m";
            }
            catch (FileNotFoundException ex)
            {
                Status = SourceStatus.FileNotFound;
                StatusMessage = ex.FileName ?? ex.Message;
            }
            catch (Exception ex)
            {
                Status = SourceStatus.Error;
                StatusMessage = ex.Message;
            }
        }

        public string Id => _option.Id;
        public EnvironmentType Type => EnvironmentType.Current;
        public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
        public string? StatusMessage { get; private set; }

        public EnvironmentValue? Query(EnvironmentQuery query)
        {
            if (Status != SourceStatus.Ready)
                return null;

            var requestedDay = (query.DateTime ?? DateTime.Today).Date;
            var data = GetCompositeData(requestedDay);
            if (data.Points.Count == 0)
                return null;

            var nearest = data.FindNearest(query.Latitude, query.Longitude, _maxNearestDistanceKm);
            if (nearest == null)
                return null;

            var point = nearest.Value.Point;
            var current = ToCurrentValue(point);
            var metadata = CreateMetadata(data);
            metadata["nearestDistanceKm"] = nearest.Value.DistanceKm;
            metadata["sourceDate"] = point.SourceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            metadata["temporalOffsetDays"] = point.TemporalOffsetDays;
            metadata["sourceSpeedCmPerSecond"] = point.SpeedMetersPerSecond * 100.0;

            return new EnvironmentValue(
                Id,
                EnvironmentType.Current,
                current,
                "m/s",
                point.Latitude,
                point.Longitude,
                null,
                point.SourceDate,
                "KHOA numerical tidal-current vector",
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"KHOA tidal-current source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.Width < 2 || query.Height < 2)
                throw new ArgumentOutOfRangeException(nameof(query), "KHOA point-cloud rendering requires Width and Height of at least 2.");

            var requestedDay = (query.DateTime ?? DateTime.Today).Date;
            var data = GetCompositeData(requestedDay);

            // The public KHOA data is an irregular/curvilinear point set rather than a regular
            // lat/lon raster. Values are therefore sampled to a display raster, while CurrentVectors
            // retains the actual selected source-record coordinates so the viewer can place arrows honestly.
            var width = query.Width;
            var height = query.Height;
            var latitudes = new double[height];
            var longitudes = new double[width];

            for (var row = 0; row < height; row++)
            {
                var t = row / (double)(height - 1);
                latitudes[row] = query.MaxLatitude + ((query.MinLatitude - query.MaxLatitude) * t);
            }

            for (var column = 0; column < width; column++)
            {
                var t = column / (double)(width - 1);
                longitudes[column] = query.MinLongitude + ((query.MaxLongitude - query.MinLongitude) * t);
            }

            var values = new double?[checked(width * height)];
            var directions = new double?[values.Length];
            double? minimum = null;
            double? maximum = null;

            if (data.Points.Count > 0)
            {
                for (var row = 0; row < height; row++)
                {
                    var latitude = latitudes[row];
                    for (var column = 0; column < width; column++)
                    {
                        var nearest = data.FindNearest(latitude, longitudes[column], _maxNearestDistanceKm);
                        if (nearest == null)
                            continue;

                        var index = (row * width) + column;
                        var speed = nearest.Value.Point.SpeedMetersPerSecond;
                        values[index] = speed;
                        directions[index] = nearest.Value.Point.DirectionDegrees;
                        minimum = !minimum.HasValue ? speed : Math.Min(minimum.Value, speed);
                        maximum = !maximum.HasValue ? speed : Math.Max(maximum.Value, speed);
                    }
                }
            }

            var nativeVectors = data.Points
                .Where(x => x.Latitude >= query.MinLatitude
                    && x.Latitude <= query.MaxLatitude
                    && x.Longitude >= query.MinLongitude
                    && x.Longitude <= query.MaxLongitude)
                .Select(x => new CurrentVectorSample(
                    x.Latitude,
                    x.Longitude,
                    x.SpeedMetersPerSecond,
                    x.DirectionDegrees,
                    x.SourceDate,
                    x.TemporalOffsetDays))
                .ToArray();

            var metadata = CreateMetadata(data);
            metadata["resolutionMode"] = "DisplayRaster";
            metadata["requestedResolutionMode"] = query.ResolutionMode.ToString();
            metadata["displayRasterWidth"] = width;
            metadata["displayRasterHeight"] = height;
            metadata["nativeVectorCountInView"] = nativeVectors.Length;

            if (data.Points.Count == 0)
                metadata["noDataReason"] = $"No source records within ±{_maxTemporalOffsetDays} day(s) of {requestedDay:yyyy-MM-dd}.";

            return new GridResult
            {
                SourceId = Id,
                Type = EnvironmentType.Current,
                Width = width,
                Height = height,
                Latitudes = latitudes,
                Longitudes = longitudes,
                Values = values,
                Directions = directions,
                CurrentVectors = nativeVectors,
                Unit = "m/s",
                DateTime = requestedDay,
                Variable = "KHOA numerical tidal-current speed / nearest-date composite",
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        public void Dispose()
        {
            lock (_cacheSync)
            {
                _yearCache.Clear();
                _yearCacheLru.Clear();
                _compositeCache.Clear();
                _compositeCacheLru.Clear();
            }

            lock (_canonicalSync)
            {
                _canonicalPoints.Clear();
                _rawCoordinateToCanonical.Clear();
                _canonicalCatalogInitialized = false;
                _ambiguousCanonicalMatchCount = 0;
            }
        }

        private void DiscoverYearFiles()
        {
            var marker = _filePattern.IndexOf("{YYYY}", StringComparison.OrdinalIgnoreCase);
            var prefix = _filePattern.Substring(0, marker);
            var suffix = _filePattern.Substring(marker + "{YYYY}".Length);

            foreach (var path in Directory.EnumerateFiles(_rootPath))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var yearText = name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length);
                if (yearText.Length != 4
                    || !int.TryParse(yearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                {
                    continue;
                }

                _yearFiles[year] = path;
            }
        }

        private CompositeData GetCompositeData(DateTime requestedDay)
        {
            requestedDay = requestedDay.Date;
            lock (_cacheSync)
            {
                if (_compositeCache.TryGetValue(requestedDay, out var cached))
                {
                    TouchCompositeCache(requestedDay);
                    return cached;
                }
            }

            var loaded = BuildCompositeData(requestedDay);
            lock (_cacheSync)
            {
                _compositeCache[requestedDay] = loaded;
                TouchCompositeCache(requestedDay);
                while (_compositeCacheLru.Count > MaxCachedComposites)
                {
                    var oldest = _compositeCacheLru.First!.Value;
                    _compositeCacheLru.RemoveFirst();
                    _compositeCache.Remove(oldest);
                }

                return loaded;
            }
        }

        private CompositeData BuildCompositeData(DateTime requestedDay)
        {
            var startDay = requestedDay.AddDays(-_maxTemporalOffsetDays);
            var endDay = requestedDay.AddDays(_maxTemporalOffsetDays);
            var indexes = new List<YearIndex>();

            for (var year = startDay.Year; year <= endDay.Year; year++)
            {
                if (_yearFiles.ContainsKey(year))
                    indexes.Add(GetYearIndex(year));
            }

            if (indexes.Count == 0)
                return CompositeData.Empty(requestedDay, _maxTemporalOffsetDays);

            var canonicalIds = new HashSet<int>();
            var selected = new Dictionary<int, TemporalSample>();
            var coordinateVariants = new HashSet<(double Latitude, double Longitude)>();

            foreach (var index in indexes)
            {
                coordinateVariants.UnionWith(index.CoordinateVariants);

                foreach (var pair in index.SeriesByPoint)
                {
                    canonicalIds.Add(pair.Key);

                    var sample = pair.Value.FindNearest(requestedDay, _maxTemporalOffsetDays);
                    if (!sample.HasValue)
                        continue;

                    if (!selected.TryGetValue(pair.Key, out var existing)
                        || IsBetterSample(sample.Value, existing, requestedDay))
                    {
                        selected[pair.Key] = sample.Value;
                    }
                }
            }

            var points = selected.Values
                .Select(x => new CurrentPoint(
                    x.Latitude,
                    x.Longitude,
                    x.SpeedMetersPerSecond,
                    x.DirectionDegrees,
                    x.Date,
                    Math.Abs((x.Date - requestedDay).Days)))
                .ToList();

            var sourceFiles = indexes
                .Select(x => x.Path)
                .Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var skippedRows = indexes.Sum(x => x.SkippedRows);
            var parsedRows = indexes.Sum(x => x.ParsedRows);
            var approximateSpacingKm = EstimateApproximateSpacing(GetCanonicalCoordinates(canonicalIds));

            return new CompositeData(
                requestedDay,
                points,
                canonicalIds.Count,
                coordinateVariants.Count,
                parsedRows,
                skippedRows,
                sourceFiles,
                _maxTemporalOffsetDays,
                approximateSpacingKm);
        }

        private YearIndex GetYearIndex(int year)
        {
            lock (_cacheSync)
            {
                if (_yearCache.TryGetValue(year, out var cached))
                {
                    TouchYearCache(year);
                    return cached;
                }
            }

            var loaded = LoadYearIndex(year);
            lock (_cacheSync)
            {
                _yearCache[year] = loaded;
                TouchYearCache(year);
                while (_yearCacheLru.Count > MaxCachedYears)
                {
                    var oldest = _yearCacheLru.First!.Value;
                    _yearCacheLru.RemoveFirst();
                    _yearCache.Remove(oldest);
                }

                return loaded;
            }
        }

        private YearIndex LoadYearIndex(int year)
        {
            if (!_yearFiles.TryGetValue(year, out var path))
                return YearIndex.Empty(year);

            EnsureCanonicalCatalogInitialized();

            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var headerLine = reader.ReadLine();
            if (headerLine == null)
                return YearIndex.Empty(year, path);

            var layout = ResolveCsvLayout(SplitCsvLine(headerLine));
            var seriesByPoint = new Dictionary<int, PointSeries>();
            var coordinateVariants = new HashSet<(double Latitude, double Longitude)>();
            var localCanonicalByRaw = new Dictionary<(double Latitude, double Longitude), int>();
            var parsedRows = 0;
            var skippedRows = 0;
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var fields = SplitCsvLine(line);
                if (fields.Length <= layout.RequiredIndex)
                {
                    skippedRows++;
                    continue;
                }

                if (!TryParseSourceDate(fields[layout.DateIndex], out var rowDate)
                    || !TryParseDouble(fields[layout.LongitudeIndex], out var longitude)
                    || !TryParseDouble(fields[layout.LatitudeIndex], out var latitude)
                    || !TryParseDouble(fields[layout.SpeedIndex], out var speedCmPerSecond)
                    || !TryParseDouble(fields[layout.DirectionIndex], out var direction))
                {
                    skippedRows++;
                    continue;
                }

                if (latitude < -90 || latitude > 90
                    || longitude < -360 || longitude > 360
                    || speedCmPerSecond < 0)
                {
                    skippedRows++;
                    continue;
                }

                rowDate = rowDate.Date;
                direction = NormalizeDirection(direction);
                var rawCoordinate = (Latitude: latitude, Longitude: longitude);
                coordinateVariants.Add(rawCoordinate);

                if (!localCanonicalByRaw.TryGetValue(rawCoordinate, out var canonicalId))
                {
                    canonicalId = ResolveCanonicalPoint(latitude, longitude);
                    localCanonicalByRaw[rawCoordinate] = canonicalId;
                }

                if (!seriesByPoint.TryGetValue(canonicalId, out var series))
                {
                    var canonical = GetCanonicalPoint(canonicalId);
                    series = new PointSeries(canonicalId, canonical.Latitude, canonical.Longitude);
                    seriesByPoint[canonicalId] = series;
                }

                series.Samples.Add(new TemporalSample(
                    rowDate,
                    latitude,
                    longitude,
                    speedCmPerSecond / 100.0,
                    direction));
                parsedRows++;
            }

            foreach (var series in seriesByPoint.Values)
                series.Sort();

            return new YearIndex(year, path, seriesByPoint, coordinateVariants, parsedRows, skippedRows);
        }

        private void EnsureCanonicalCatalogInitialized()
        {
            lock (_canonicalSync)
            {
                if (_canonicalCatalogInitialized)
                    return;

                var baseYear = _yearFiles.Keys.Min();
                var path = _yearFiles[baseYear];
                var coordinates = ReadUniqueCoordinates(path);
                coordinates.Sort((a, b) =>
                {
                    var latitudeComparison = a.Latitude.CompareTo(b.Latitude);
                    return latitudeComparison != 0
                        ? latitudeComparison
                        : a.Longitude.CompareTo(b.Longitude);
                });

                foreach (var coordinate in coordinates)
                    ResolveCanonicalPointCore(coordinate.Latitude, coordinate.Longitude);

                _canonicalCatalogInitialized = true;
            }
        }

        private List<CoordinatePoint> ReadUniqueCoordinates(string path)
        {
            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var headerLine = reader.ReadLine();
            if (headerLine == null)
                return new List<CoordinatePoint>();

            var layout = ResolveCsvLayout(SplitCsvLine(headerLine));
            var unique = new HashSet<(double Latitude, double Longitude)>();
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var fields = SplitCsvLine(line);
                if (fields.Length <= layout.RequiredIndex)
                    continue;

                if (!TryParseDouble(fields[layout.LongitudeIndex], out var longitude)
                    || !TryParseDouble(fields[layout.LatitudeIndex], out var latitude))
                {
                    continue;
                }

                if (latitude < -90 || latitude > 90 || longitude < -360 || longitude > 360)
                    continue;

                unique.Add((latitude, longitude));
            }

            return unique.Select(x => new CoordinatePoint(x.Latitude, x.Longitude)).ToList();
        }

        private int ResolveCanonicalPoint(double latitude, double longitude)
        {
            EnsureCanonicalCatalogInitialized();
            lock (_canonicalSync)
            {
                return ResolveCanonicalPointCore(latitude, longitude);
            }
        }

        private int ResolveCanonicalPointCore(double latitude, double longitude)
        {
            var rawCoordinate = (Latitude: latitude, Longitude: longitude);
            if (_rawCoordinateToCanonical.TryGetValue(rawCoordinate, out var cached))
                return cached;

            var bestId = -1;
            var bestDistanceKm = double.MaxValue;
            var candidateCount = 0;

            for (var i = 0; i < _canonicalPoints.Count; i++)
            {
                var candidate = _canonicalPoints[i];
                var distanceKm = HaversineKilometers(
                    latitude,
                    longitude,
                    candidate.Latitude,
                    candidate.Longitude);

                if (distanceKm > CanonicalPointToleranceKm)
                    continue;

                candidateCount++;
                if (distanceKm < bestDistanceKm)
                {
                    bestDistanceKm = distanceKm;
                    bestId = candidate.Id;
                }
            }

            if (bestId < 0)
            {
                bestId = _canonicalPoints.Count;
                _canonicalPoints.Add(new CanonicalPoint(bestId, latitude, longitude));
            }
            else if (candidateCount > 1)
            {
                _ambiguousCanonicalMatchCount++;
            }

            _rawCoordinateToCanonical[rawCoordinate] = bestId;
            return bestId;
        }

        private CanonicalPoint GetCanonicalPoint(int id)
        {
            lock (_canonicalSync)
            {
                if ((uint)id >= (uint)_canonicalPoints.Count)
                    throw new ArgumentOutOfRangeException(nameof(id));
                return _canonicalPoints[id];
            }
        }

        private List<CoordinatePoint> GetCanonicalCoordinates(IEnumerable<int> ids)
        {
            lock (_canonicalSync)
            {
                return ids
                    .Where(id => (uint)id < (uint)_canonicalPoints.Count)
                    .Select(id => new CoordinatePoint(
                        _canonicalPoints[id].Latitude,
                        _canonicalPoints[id].Longitude))
                    .ToList();
            }
        }

        private Dictionary<string, object?> CreateMetadata(CompositeData data)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();

            metadata["dataset"] = metadata.TryGetValue("dataset", out var existing) && existing != null
                ? existing
                : "KHOA intelligent maritime traffic numerical tidal-current CSV";
            metadata["sourceGeometry"] = "IrregularCurvilinearPointCloud";
            metadata["sourceTemporalKey"] = "search date";
            metadata["timeOfDayAvailable"] = false;
            metadata["temporalMode"] = "PerPointNearestDateComposite";
            metadata["requestedDate"] = data.RequestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            metadata["maxTemporalOffsetDays"] = _maxTemporalOffsetDays;
            metadata["sourceSpeedUnit"] = "cm/s";
            metadata["outputSpeedUnit"] = "m/s";
            metadata["directionUnit"] = "degree";
            metadata["directionConvention"] = "treated as toward direction, clockwise from true north";
            metadata["directionConventionAssumed"] = true;
            metadata["sourcePointCount"] = data.Points.Count;
            metadata["canonicalPointCount"] = data.CanonicalPointCount;
            metadata["canonicalPointToleranceMeters"] = CanonicalPointToleranceMeters;
            metadata["sourceCoordinateVariantCount"] = data.SourceCoordinateVariantCount;
            metadata["coveragePercent"] = data.CoveragePercent;
            metadata["medianTemporalOffsetDays"] = data.MedianTemporalOffsetDays;
            metadata["maximumTemporalOffsetDaysUsed"] = data.MaximumTemporalOffsetDaysUsed;
            metadata["sourceDateMinimum"] = data.SourceDateMinimum?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            metadata["sourceDateMaximum"] = data.SourceDateMaximum?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            metadata["sourceFiles"] = data.SourceFiles.Length == 0
                ? null
                : string.Join(";", data.SourceFiles.Select(Path.GetFileName));
            metadata["sourceApproximateSpacingKm"] = data.ApproximateSpacingKm;
            metadata["maxNearestDistanceKm"] = _maxNearestDistanceKm;
            metadata["parsedRowsInIndexedYears"] = data.ParsedRows;
            metadata["skippedRowsInIndexedYears"] = data.SkippedRows;
            lock (_canonicalSync)
            {
                metadata["canonicalAmbiguousMatchCount"] = _ambiguousCanonicalMatchCount;
            }
            return metadata;
        }

        private void TouchYearCache(int year)
        {
            var node = _yearCacheLru.Find(year);
            if (node != null)
                _yearCacheLru.Remove(node);
            _yearCacheLru.AddLast(year);
        }

        private void TouchCompositeCache(DateTime day)
        {
            var node = _compositeCacheLru.Find(day);
            if (node != null)
                _compositeCacheLru.Remove(node);
            _compositeCacheLru.AddLast(day);
        }

        private static bool IsBetterSample(TemporalSample candidate, TemporalSample existing, DateTime requestedDay)
        {
            var candidateOffset = Math.Abs((candidate.Date - requestedDay).Days);
            var existingOffset = Math.Abs((existing.Date - requestedDay).Days);
            if (candidateOffset != existingOffset)
                return candidateOffset < existingOffset;

            var candidateIsPast = candidate.Date <= requestedDay;
            var existingIsPast = existing.Date <= requestedDay;
            if (candidateIsPast != existingIsPast)
                return candidateIsPast;

            if (candidate.Date != existing.Date)
                return candidateIsPast ? candidate.Date > existing.Date : candidate.Date < existing.Date;

            return false;
        }

        private static CurrentValue ToCurrentValue(CurrentPoint point)
        {
            // KHOA publishes speed + direction rather than U/V. Until the public metadata
            // explicitly states otherwise, the implementation treats 유향 as oceanographic
            // flow-toward bearing clockwise from true north. The assumption is exposed in metadata.
            var radians = point.DirectionDegrees * Math.PI / 180.0;
            var eastward = point.SpeedMetersPerSecond * Math.Sin(radians);
            var northward = point.SpeedMetersPerSecond * Math.Cos(radians);

            return new CurrentValue
            {
                EastwardVelocity = eastward,
                NorthwardVelocity = northward,
                Speed = point.SpeedMetersPerSecond,
                Direction = point.DirectionDegrees,
                ConstituentCount = 0
            };
        }

        private static bool TryParseSourceDate(string text, out DateTime date)
        {
            text = TrimCsvField(text);
            var formats = new[] { "yyyy-MM-dd", "yyyy.M.d", "yyyy.MM.dd", "yyyy/M/d", "yyyy/MM/dd" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;

            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
        }

        private static bool TryParseDouble(string text, out double value)
        {
            return double.TryParse(
                TrimCsvField(text),
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static CsvLayout ResolveCsvLayout(string[] header)
        {
            var dateIndex = FindHeaderIndex(header, "검색 시간", "검색시간");
            var longitudeIndex = FindHeaderIndex(header, "지점경도");
            var latitudeIndex = FindHeaderIndex(header, "지점위도");
            var speedIndex = FindHeaderIndex(header, "유속(cm_s)", "유속");
            var directionIndex = FindHeaderIndex(header, "유향");

            // Public-file schema fallback documented by data.go.kr/KHOA.
            if (dateIndex < 0) dateIndex = 0;
            if (longitudeIndex < 0) longitudeIndex = 5;
            if (latitudeIndex < 0) latitudeIndex = 6;
            if (speedIndex < 0) speedIndex = 7;
            if (directionIndex < 0) directionIndex = 8;

            return new CsvLayout(
                dateIndex,
                longitudeIndex,
                latitudeIndex,
                speedIndex,
                directionIndex);
        }

        private static int FindHeaderIndex(string[] header, params string[] candidates)
        {
            for (var i = 0; i < header.Length; i++)
            {
                var normalized = TrimCsvField(header[i]).Replace(" ", string.Empty);
                foreach (var candidate in candidates)
                {
                    var expected = candidate.Replace(" ", string.Empty);
                    if (string.Equals(normalized, expected, StringComparison.OrdinalIgnoreCase)
                        || (expected == "유속" && normalized.StartsWith("유속", StringComparison.OrdinalIgnoreCase)))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (ch == ',' && !quoted)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(ch);
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }

        private static string TrimCsvField(string value)
        {
            return value.Trim().Trim('\uFEFF').Trim('"').Trim();
        }

        private static double NormalizeDirection(double direction)
        {
            direction %= 360.0;
            return direction < 0 ? direction + 360.0 : direction;
        }

        private static double HaversineKilometers(double lat1, double lon1, double lat2, double lon2)
        {
            const double radius = 6371.0088;
            const double radians = Math.PI / 180.0;
            var dLat = (lat2 - lat1) * radians;
            var dLon = (lon2 - lon1) * radians;
            var a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0)
                + Math.Cos(lat1 * radians) * Math.Cos(lat2 * radians)
                * Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);
            return 2.0 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        private static double? EstimateApproximateSpacing(List<CoordinatePoint> points)
        {
            if (points.Count < 2)
                return null;

            var sampleCount = Math.Min(96, points.Count);
            var distances = new List<double>(sampleCount);

            for (var sample = 0; sample < sampleCount; sample++)
            {
                var index = sampleCount == 1
                    ? 0
                    : (int)Math.Round(sample * (points.Count - 1.0) / (sampleCount - 1.0));

                var source = points[index];
                var nearest = double.MaxValue;
                for (var i = 0; i < points.Count; i++)
                {
                    if (i == index)
                        continue;

                    var candidate = points[i];
                    var distance = HaversineKilometers(
                        source.Latitude,
                        source.Longitude,
                        candidate.Latitude,
                        candidate.Longitude);

                    if (distance > 0.001 && distance < nearest)
                        nearest = distance;
                }

                if (nearest < double.MaxValue)
                    distances.Add(nearest);
            }

            if (distances.Count == 0)
                return null;

            distances.Sort();
            var middle = distances.Count / 2;
            return distances.Count % 2 == 1
                ? distances[middle]
                : (distances[middle - 1] + distances[middle]) / 2.0;
        }

        private sealed class CompositeData
        {
            private readonly Dictionary<(int Lat, int Lon), List<CurrentPoint>> _buckets;

            public CompositeData(
                DateTime requestedDate,
                List<CurrentPoint> points,
                int canonicalPointCount,
                int sourceCoordinateVariantCount,
                int parsedRows,
                int skippedRows,
                string[] sourceFiles,
                int maxTemporalOffsetDays,
                double? approximateSpacingKm)
            {
                RequestedDate = requestedDate.Date;
                Points = points;
                CanonicalPointCount = canonicalPointCount;
                SourceCoordinateVariantCount = sourceCoordinateVariantCount;
                ParsedRows = parsedRows;
                SkippedRows = skippedRows;
                SourceFiles = sourceFiles;
                MaxTemporalOffsetDays = maxTemporalOffsetDays;
                ApproximateSpacingKm = approximateSpacingKm;
                _buckets = BuildBuckets(points);

                CoveragePercent = canonicalPointCount > 0
                    ? points.Count * 100.0 / canonicalPointCount
                    : 0.0;

                if (points.Count > 0)
                {
                    var offsets = points.Select(x => x.TemporalOffsetDays).OrderBy(x => x).ToArray();
                    var middle = offsets.Length / 2;
                    MedianTemporalOffsetDays = offsets.Length % 2 == 1
                        ? offsets[middle]
                        : (offsets[middle - 1] + offsets[middle]) / 2.0;
                    MaximumTemporalOffsetDaysUsed = offsets[offsets.Length - 1];
                    SourceDateMinimum = points.Min(x => x.SourceDate);
                    SourceDateMaximum = points.Max(x => x.SourceDate);
                }
            }

            public DateTime RequestedDate { get; }
            public List<CurrentPoint> Points { get; }
            public int CanonicalPointCount { get; }
            public int SourceCoordinateVariantCount { get; }
            public int ParsedRows { get; }
            public int SkippedRows { get; }
            public string[] SourceFiles { get; }
            public int MaxTemporalOffsetDays { get; }
            public double CoveragePercent { get; }
            public double? MedianTemporalOffsetDays { get; }
            public int? MaximumTemporalOffsetDaysUsed { get; }
            public DateTime? SourceDateMinimum { get; }
            public DateTime? SourceDateMaximum { get; }
            public double? ApproximateSpacingKm { get; }

            public static CompositeData Empty(DateTime requestedDate, int maxTemporalOffsetDays)
            {
                return new CompositeData(
                    requestedDate,
                    new List<CurrentPoint>(),
                    0,
                    0,
                    0,
                    0,
                    Array.Empty<string>(),
                    maxTemporalOffsetDays,
                    null);
            }

            public NearestPoint? FindNearest(double latitude, double longitude, double maxDistanceKm)
            {
                if (Points.Count == 0)
                    return null;

                var latBucket = ToLatBucket(latitude);
                var lonBucket = ToLonBucket(longitude);
                CurrentPoint? best = null;
                var bestDistance = double.MaxValue;

                for (var radius = 0; radius <= 2; radius++)
                {
                    for (var dLat = -radius; dLat <= radius; dLat++)
                    {
                        for (var dLon = -radius; dLon <= radius; dLon++)
                        {
                            if (radius > 0 && Math.Max(Math.Abs(dLat), Math.Abs(dLon)) != radius)
                                continue;

                            if (!_buckets.TryGetValue((latBucket + dLat, lonBucket + dLon), out var candidates))
                                continue;

                            foreach (var candidate in candidates)
                            {
                                var distance = HaversineKilometers(
                                    latitude,
                                    longitude,
                                    candidate.Latitude,
                                    candidate.Longitude);

                                if (distance < bestDistance)
                                {
                                    bestDistance = distance;
                                    best = candidate;
                                }
                            }
                        }
                    }

                    if (best != null && maxDistanceKm > 0 && bestDistance <= maxDistanceKm)
                        break;
                }

                if (best == null || (maxDistanceKm <= 0 && bestDistance == double.MaxValue))
                {
                    foreach (var candidate in Points)
                    {
                        var distance = HaversineKilometers(
                            latitude,
                            longitude,
                            candidate.Latitude,
                            candidate.Longitude);

                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            best = candidate;
                        }
                    }
                }

                if (best == null)
                    return null;
                if (maxDistanceKm > 0 && bestDistance > maxDistanceKm)
                    return null;

                return new NearestPoint(best, bestDistance);
            }

            private static Dictionary<(int Lat, int Lon), List<CurrentPoint>> BuildBuckets(IEnumerable<CurrentPoint> points)
            {
                var buckets = new Dictionary<(int Lat, int Lon), List<CurrentPoint>>();

                foreach (var point in points)
                {
                    var key = (ToLatBucket(point.Latitude), ToLonBucket(point.Longitude));
                    if (!buckets.TryGetValue(key, out var list))
                    {
                        list = new List<CurrentPoint>();
                        buckets[key] = list;
                    }

                    list.Add(point);
                }

                return buckets;
            }

            private static int ToLatBucket(double latitude)
                => (int)Math.Floor((latitude + 90.0) / BucketSizeDegrees);

            private static int ToLonBucket(double longitude)
                => (int)Math.Floor((longitude + 360.0) / BucketSizeDegrees);
        }

        private sealed class YearIndex
        {
            public YearIndex(
                int year,
                string path,
                Dictionary<int, PointSeries> seriesByPoint,
                HashSet<(double Latitude, double Longitude)> coordinateVariants,
                int parsedRows,
                int skippedRows)
            {
                Year = year;
                Path = path;
                SeriesByPoint = seriesByPoint;
                CoordinateVariants = coordinateVariants;
                ParsedRows = parsedRows;
                SkippedRows = skippedRows;
            }

            public int Year { get; }
            public string Path { get; }
            public Dictionary<int, PointSeries> SeriesByPoint { get; }
            public HashSet<(double Latitude, double Longitude)> CoordinateVariants { get; }
            public int ParsedRows { get; }
            public int SkippedRows { get; }

            public static YearIndex Empty(int year, string path = "")
            {
                return new YearIndex(
                    year,
                    path,
                    new Dictionary<int, PointSeries>(),
                    new HashSet<(double Latitude, double Longitude)>(),
                    0,
                    0);
            }
        }

        private sealed class PointSeries
        {
            public PointSeries(int canonicalId, double canonicalLatitude, double canonicalLongitude)
            {
                CanonicalId = canonicalId;
                CanonicalLatitude = canonicalLatitude;
                CanonicalLongitude = canonicalLongitude;
            }

            public int CanonicalId { get; }
            public double CanonicalLatitude { get; }
            public double CanonicalLongitude { get; }
            public List<TemporalSample> Samples { get; } = new List<TemporalSample>();

            public void Sort()
            {
                Samples.Sort((a, b) => a.Date.CompareTo(b.Date));
            }

            public TemporalSample? FindNearest(DateTime requestedDay, int maxTemporalOffsetDays)
            {
                if (Samples.Count == 0)
                    return null;

                var low = 0;
                var high = Samples.Count;
                while (low < high)
                {
                    var mid = low + ((high - low) / 2);
                    if (Samples[mid].Date < requestedDay)
                        low = mid + 1;
                    else
                        high = mid;
                }

                TemporalSample? best = null;
                if (low < Samples.Count)
                    best = Samples[low];
                if (low > 0)
                {
                    var previous = Samples[low - 1];
                    if (!best.HasValue || IsBetterSample(previous, best.Value, requestedDay))
                        best = previous;
                }

                if (!best.HasValue)
                    return null;

                return Math.Abs((best.Value.Date - requestedDay).Days) <= maxTemporalOffsetDays
                    ? best
                    : null;
            }
        }

        private sealed class CurrentPoint
        {
            public CurrentPoint(
                double latitude,
                double longitude,
                double speedMetersPerSecond,
                double directionDegrees,
                DateTime sourceDate,
                int temporalOffsetDays)
            {
                Latitude = latitude;
                Longitude = longitude;
                SpeedMetersPerSecond = speedMetersPerSecond;
                DirectionDegrees = directionDegrees;
                SourceDate = sourceDate.Date;
                TemporalOffsetDays = temporalOffsetDays;
            }

            public double Latitude { get; }
            public double Longitude { get; }
            public double SpeedMetersPerSecond { get; }
            public double DirectionDegrees { get; }
            public DateTime SourceDate { get; }
            public int TemporalOffsetDays { get; }
        }

        private readonly struct TemporalSample
        {
            public TemporalSample(
                DateTime date,
                double latitude,
                double longitude,
                double speedMetersPerSecond,
                double directionDegrees)
            {
                Date = date.Date;
                Latitude = latitude;
                Longitude = longitude;
                SpeedMetersPerSecond = speedMetersPerSecond;
                DirectionDegrees = directionDegrees;
            }

            public DateTime Date { get; }
            public double Latitude { get; }
            public double Longitude { get; }
            public double SpeedMetersPerSecond { get; }
            public double DirectionDegrees { get; }
        }

        private readonly struct CanonicalPoint
        {
            public CanonicalPoint(int id, double latitude, double longitude)
            {
                Id = id;
                Latitude = latitude;
                Longitude = longitude;
            }

            public int Id { get; }
            public double Latitude { get; }
            public double Longitude { get; }
        }

        private readonly struct CoordinatePoint
        {
            public CoordinatePoint(double latitude, double longitude)
            {
                Latitude = latitude;
                Longitude = longitude;
            }

            public double Latitude { get; }
            public double Longitude { get; }
        }

        private readonly struct CsvLayout
        {
            public CsvLayout(
                int dateIndex,
                int longitudeIndex,
                int latitudeIndex,
                int speedIndex,
                int directionIndex)
            {
                DateIndex = dateIndex;
                LongitudeIndex = longitudeIndex;
                LatitudeIndex = latitudeIndex;
                SpeedIndex = speedIndex;
                DirectionIndex = directionIndex;
                RequiredIndex = new[] { dateIndex, longitudeIndex, latitudeIndex, speedIndex, directionIndex }.Max();
            }

            public int DateIndex { get; }
            public int LongitudeIndex { get; }
            public int LatitudeIndex { get; }
            public int SpeedIndex { get; }
            public int DirectionIndex { get; }
            public int RequiredIndex { get; }
        }

        private readonly struct NearestPoint
        {
            public NearestPoint(CurrentPoint point, double distanceKm)
            {
                Point = point;
                DistanceKm = distanceKm;
            }

            public CurrentPoint Point { get; }
            public double DistanceKm { get; }
        }
    }
}
