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
    /// Reads the Ministry of Oceans and Fisheries / KHOA intelligent maritime traffic
    /// daily numerical tidal-current CSV files. Each yearly file contains daily point
    /// vectors with longitude, latitude, speed (cm/s), and current direction (degrees).
    /// </summary>
    internal sealed class KhoaDailyCurrentCsvDataSource : IEnvironmentDataSource
    {
        private const int MaxCachedDays = 3;
        private const double BucketSizeDegrees = 0.25;
        private readonly DataSourceOption _option;
        private readonly string _rootPath;
        private readonly string _filePattern;
        private readonly Dictionary<int, string> _yearFiles = new Dictionary<int, string>();
        private readonly object _cacheSync = new object();
        private readonly Dictionary<DateTime, DayData> _dayCache = new Dictionary<DateTime, DayData>();
        private readonly LinkedList<DateTime> _cacheLru = new LinkedList<DateTime>();
        private readonly double _maxNearestDistanceKm;

        public KhoaDailyCurrentCsvDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option ?? throw new ArgumentNullException(nameof(option));
            _rootPath = resolvedPath ?? throw new ArgumentNullException(nameof(resolvedPath));
            _filePattern = string.IsNullOrWhiteSpace(option.FilePattern)
                ? "해양수산부_지능형해상교통정보_수치조류도_{YYYY}.csv"
                : option.FilePattern!;
            _maxNearestDistanceKm = option.MaxNearestDistanceKm ?? 30.0;

            if (!option.Enabled)
            {
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                if (!Directory.Exists(_rootPath))
                    throw new DirectoryNotFoundException($"KHOA daily-current CSV directory was not found: {_rootPath}");
                if (_filePattern.IndexOf("{YYYY}", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidDataException("KHOA daily-current filePattern must contain the {YYYY} token.");

                DiscoverYearFiles();
                if (_yearFiles.Count == 0)
                    throw new FileNotFoundException($"No KHOA daily-current CSV files matched '{_filePattern}' under '{_rootPath}'.");

                var minYear = _yearFiles.Keys.Min();
                var maxYear = _yearFiles.Keys.Max();
                Status = SourceStatus.Ready;
                StatusMessage = $"{_yearFiles.Count} yearly CSV file(s), {minYear}-{maxYear} / daily vector points";
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

            var day = (query.DateTime ?? DateTime.Today).Date;
            var data = GetDayData(day);
            if (data.Points.Count == 0)
                return null;

            var nearest = data.FindNearest(query.Latitude, query.Longitude, _maxNearestDistanceKm);
            if (nearest == null)
                return null;

            var point = nearest.Value.Point;
            var current = ToCurrentValue(point);
            var metadata = CreateMetadata(data);
            metadata["nearestDistanceKm"] = nearest.Value.DistanceKm;
            metadata["sourceSpeedCmPerSecond"] = point.SpeedMetersPerSecond * 100.0;

            return new EnvironmentValue(
                Id,
                EnvironmentType.Current,
                current,
                "m/s",
                point.Latitude,
                point.Longitude,
                null,
                day,
                "KHOA daily numerical tidal-current vector",
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"KHOA daily-current source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.Width < 2 || query.Height < 2)
                throw new ArgumentOutOfRangeException(nameof(query), "KHOA point-cloud rendering requires Width and Height of at least 2.");

            var day = (query.DateTime ?? DateTime.Today).Date;
            var data = GetDayData(day);
            if (data.Points.Count == 0)
                throw new InvalidOperationException($"KHOA daily-current source '{Id}' has no records for {day:yyyy-MM-dd}.");

            // The published CSV is an irregular/curvilinear point set, not a regular lat/lon raster.
            // Both SourceNative and Custom therefore use a display raster sampled from the native points.
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

            var metadata = CreateMetadata(data);
            metadata["resolutionMode"] = "DisplayRaster";
            metadata["requestedResolutionMode"] = query.ResolutionMode.ToString();
            metadata["displayRasterWidth"] = width;
            metadata["displayRasterHeight"] = height;

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
                Unit = "m/s",
                DateTime = day,
                Variable = "KHOA daily numerical tidal-current speed",
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        public void Dispose()
        {
            lock (_cacheSync)
            {
                _dayCache.Clear();
                _cacheLru.Clear();
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

        private DayData GetDayData(DateTime day)
        {
            day = day.Date;
            lock (_cacheSync)
            {
                if (_dayCache.TryGetValue(day, out var cached))
                {
                    TouchCache(day);
                    return cached;
                }
            }

            var loaded = LoadDayData(day);
            lock (_cacheSync)
            {
                _dayCache[day] = loaded;
                TouchCache(day);
                while (_cacheLru.Count > MaxCachedDays)
                {
                    var oldest = _cacheLru.First!.Value;
                    _cacheLru.RemoveFirst();
                    _dayCache.Remove(oldest);
                }
                return loaded;
            }
        }

        private void TouchCache(DateTime day)
        {
            var node = _cacheLru.Find(day);
            if (node != null)
                _cacheLru.Remove(node);
            _cacheLru.AddLast(day);
        }

        private DayData LoadDayData(DateTime day)
        {
            if (!_yearFiles.TryGetValue(day.Year, out var path))
                return DayData.Empty(day, null);

            using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
            var headerLine = reader.ReadLine();
            if (headerLine == null)
                return DayData.Empty(day, path);

            var header = SplitCsvLine(headerLine);
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
            var requiredIndex = new[] { dateIndex, longitudeIndex, latitudeIndex, speedIndex, directionIndex }.Max();

            var points = new List<CurrentPoint>();
            var skippedRows = 0;
            var foundRequestedDay = false;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var fields = SplitCsvLine(line);
                if (fields.Length <= requiredIndex)
                {
                    skippedRows++;
                    continue;
                }

                if (!TryParseSourceDate(fields[dateIndex], out var rowDate))
                {
                    skippedRows++;
                    continue;
                }

                rowDate = rowDate.Date;
                if (rowDate < day)
                    continue;
                if (rowDate > day)
                {
                    // The published annual files are date ordered. Once the target day has
                    // been consumed, stop instead of scanning the remainder of a large year file.
                    if (foundRequestedDay || points.Count == 0)
                        break;
                    continue;
                }

                foundRequestedDay = true;
                if (!TryParseDouble(fields[longitudeIndex], out var longitude)
                    || !TryParseDouble(fields[latitudeIndex], out var latitude)
                    || !TryParseDouble(fields[speedIndex], out var speedCmPerSecond)
                    || !TryParseDouble(fields[directionIndex], out var direction))
                {
                    skippedRows++;
                    continue;
                }

                if (latitude < -90 || latitude > 90 || longitude < -360 || longitude > 360 || speedCmPerSecond < 0)
                {
                    skippedRows++;
                    continue;
                }

                direction = NormalizeDirection(direction);
                points.Add(new CurrentPoint(latitude, longitude, speedCmPerSecond / 100.0, direction));
            }

            return new DayData(day, path, points, skippedRows);
        }

        private Dictionary<string, object?> CreateMetadata(DayData data)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();
            metadata["dataset"] = metadata.TryGetValue("dataset", out var existing) && existing != null
                ? existing
                : "KHOA intelligent maritime traffic numerical tidal-current daily CSV";
            metadata["sourceGeometry"] = "IrregularPointCloud";
            metadata["sourceTemporalResolution"] = "1 day";
            metadata["timeOfDayAvailable"] = false;
            metadata["sourceSpeedUnit"] = "cm/s";
            metadata["outputSpeedUnit"] = "m/s";
            metadata["directionUnit"] = "degree";
            metadata["directionConvention"] = "toward direction, clockwise from true north";
            metadata["sourceDate"] = data.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            metadata["sourceFile"] = data.Path == null ? null : Path.GetFileName(data.Path);
            metadata["sourcePointCount"] = data.Points.Count;
            metadata["sourceApproximateSpacingKm"] = data.ApproximateSpacingKm;
            metadata["maxNearestDistanceKm"] = _maxNearestDistanceKm;
            metadata["skippedRows"] = data.SkippedRows;
            return metadata;
        }

        private static CurrentValue ToCurrentValue(CurrentPoint point)
        {
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

        private sealed class DayData
        {
            private readonly Dictionary<(int Lat, int Lon), List<CurrentPoint>> _buckets;

            public DayData(DateTime date, string? path, List<CurrentPoint> points, int skippedRows)
            {
                Date = date.Date;
                Path = path;
                Points = points;
                SkippedRows = skippedRows;
                _buckets = BuildBuckets(points);
                ApproximateSpacingKm = EstimateApproximateSpacing(points);
            }

            public DateTime Date { get; }
            public string? Path { get; }
            public List<CurrentPoint> Points { get; }
            public int SkippedRows { get; }
            public double? ApproximateSpacingKm { get; }

            public static DayData Empty(DateTime date, string? path)
                => new DayData(date, path, new List<CurrentPoint>(), 0);

            public NearestPoint? FindNearest(double latitude, double longitude, double maxDistanceKm)
            {
                if (Points.Count == 0)
                    return null;

                var latBucket = ToLatBucket(latitude);
                var lonBucket = ToLonBucket(longitude);
                CurrentPoint? best = null;
                var bestDistance = double.MaxValue;

                // A 3-bucket radius covers substantially more than the ~16 km public-point
                // spacing. Search it first to keep display-raster sampling inexpensive.
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
                                var distance = HaversineKilometers(latitude, longitude, candidate.Latitude, candidate.Longitude);
                                if (distance < bestDistance)
                                {
                                    bestDistance = distance;
                                    best = candidate;
                                }
                            }
                        }
                    }
                }

                if (best == null && maxDistanceKm <= 0)
                {
                    // Unlimited-distance mode is uncommon; fall back to an exhaustive search.
                    foreach (var candidate in Points)
                    {
                        var distance = HaversineKilometers(latitude, longitude, candidate.Latitude, candidate.Longitude);
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

            private static double? EstimateApproximateSpacing(List<CurrentPoint> points)
            {
                if (points.Count < 2)
                    return null;

                var sampleCount = Math.Min(48, points.Count);
                var distances = new List<double>(sampleCount);
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    var index = sampleCount == 1 ? 0 : (int)Math.Round(sample * (points.Count - 1.0) / (sampleCount - 1.0));
                    var source = points[index];
                    var nearest = double.MaxValue;
                    for (var i = 0; i < points.Count; i++)
                    {
                        if (i == index)
                            continue;
                        var candidate = points[i];
                        var distance = HaversineKilometers(source.Latitude, source.Longitude, candidate.Latitude, candidate.Longitude);
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

            private static int ToLatBucket(double latitude) => (int)Math.Floor((latitude + 90.0) / BucketSizeDegrees);
            private static int ToLonBucket(double longitude) => (int)Math.Floor((longitude + 360.0) / BucketSizeDegrees);
        }

        private sealed class CurrentPoint
        {
            public CurrentPoint(double latitude, double longitude, double speedMetersPerSecond, double directionDegrees)
            {
                Latitude = latitude;
                Longitude = longitude;
                SpeedMetersPerSecond = speedMetersPerSecond;
                DirectionDegrees = directionDegrees;
            }

            public double Latitude { get; }
            public double Longitude { get; }
            public double SpeedMetersPerSecond { get; }
            public double DirectionDegrees { get; }
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
