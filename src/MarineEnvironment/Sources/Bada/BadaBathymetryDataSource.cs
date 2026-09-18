using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Native;

namespace MarineEnvironment.Sources.Bada
{
    /// <summary>
    /// Reader for the BADA2024 gridded-bathymetry distribution whose NetCDF layout is a
    /// paired one-dimensional point table:
    /// LAT[length], LON[length], MSL[length].
    ///
    /// LAT/LON are not independent raster axes. The same index across all three variables
    /// forms one source-native bathymetry sample. A compact in-memory spatial-bin index is
    /// built lazily on first query so point and display-raster queries do not scan all
    /// ~15 million source records repeatedly.
    /// </summary>
    internal sealed class BadaBathymetryDataSource : IEnvironmentDataSource
    {
        private const int ReadChunkSize = 250_000;
        private const double PreferredBinSizeDegrees = 0.01;
        private const long MaxSpatialBins = 2_000_000;
        private const double DefaultMaxNearestDistanceKm = 10.0;

        private readonly DataSourceOption _option;
        private readonly string _resolvedPath;
        private readonly object _indexSync = new object();
        private readonly double _maxNearestDistanceKm;
        private int _pointCount;
        private SpatialIndex? _index;

        public BadaBathymetryDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option ?? throw new ArgumentNullException(nameof(option));
            _resolvedPath = resolvedPath ?? throw new ArgumentNullException(nameof(resolvedPath));
            _maxNearestDistanceKm = option.MaxNearestDistanceKm ?? DefaultMaxNearestDistanceKm;

            if (!option.Enabled)
            {
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                if (_maxNearestDistanceKm <= 0)
                    throw new InvalidDataException("BADA maxNearestDistanceKm must be greater than zero.");
                if (!File.Exists(_resolvedPath))
                {
                    Status = SourceStatus.FileNotFound;
                    StatusMessage = _resolvedPath;
                    return;
                }

                using var file = Open(_resolvedPath);
                var layout = ReadLayout(file.Id);
                _pointCount = layout.PointCount;

                Status = SourceStatus.Ready;
                StatusMessage =
                    $"{_pointCount:N0} paired bathymetry points / LAT-LON-MSL same-index records / spatial index lazy-loaded";
            }
            catch (DllNotFoundException ex)
            {
                Status = SourceStatus.NativeLibraryUnavailable;
                StatusMessage = ex.Message;
            }
            catch (Exception ex)
            {
                Status = SourceStatus.Error;
                StatusMessage = ex.Message;
            }
        }

        public string Id => _option.Id;
        public EnvironmentType Type => EnvironmentType.Bathymetry;
        public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
        public string? StatusMessage { get; private set; }

        public EnvironmentValue? Query(EnvironmentQuery query)
        {
            if (Status != SourceStatus.Ready)
                return null;

            var index = EnsureIndex();
            var nearest = index.FindNearest(
                query.Latitude,
                query.Longitude,
                _maxNearestDistanceKm);

            if (!nearest.HasValue)
                return null;

            var sample = nearest.Value;
            var metadata = CreateMetadata(index);
            metadata["sourceIndex"] = sample.SourceIndex;
            metadata["nearestDistanceKm"] = sample.DistanceKm;
            metadata["verticalReference"] = "MSL";
            metadata["sourceGeometry"] = "IrregularPointCloud";
            metadata["sourcePointCount"] = index.Count;

            return new EnvironmentValue(
                Id,
                EnvironmentType.Bathymetry,
                sample.DepthMeters,
                string.IsNullOrWhiteSpace(_option.Unit) ? "m" : _option.Unit,
                sample.Latitude,
                sample.Longitude,
                null,
                null,
                _option.Variable,
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException(
                    $"BADA bathymetry source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.Width < 2 || query.Width > 2048)
                throw new ArgumentOutOfRangeException(
                    nameof(query.Width),
                    "BADA display-raster width must be between 2 and 2048.");
            if (query.Height < 2 || query.Height > 2048)
                throw new ArgumentOutOfRangeException(
                    nameof(query.Height),
                    "BADA display-raster height must be between 2 and 2048.");

            var index = EnsureIndex();
            var width = query.Width;
            var height = query.Height;
            var latitudes = new double[height];
            var longitudes = new double[width];

            for (var row = 0; row < height; row++)
            {
                var t = row / (double)(height - 1);
                latitudes[row] =
                    query.MaxLatitude +
                    ((query.MinLatitude - query.MaxLatitude) * t);
            }

            for (var column = 0; column < width; column++)
            {
                var t = column / (double)(width - 1);
                longitudes[column] =
                    query.MinLongitude +
                    ((query.MaxLongitude - query.MinLongitude) * t);
            }

            var values = new double?[checked(width * height)];

            // SpatialIndex is immutable after construction, so rows can be sampled in parallel.
            Parallel.For(0, height, row =>
            {
                var latitude = latitudes[row];
                for (var column = 0; column < width; column++)
                {
                    var nearest = index.FindNearest(
                        latitude,
                        longitudes[column],
                        _maxNearestDistanceKm);
                    if (!nearest.HasValue)
                        continue;

                    values[(row * width) + column] = nearest.Value.DepthMeters;
                }
            });

            double? minimum = null;
            double? maximum = null;
            foreach (var value in values)
            {
                if (!value.HasValue)
                    continue;
                minimum = !minimum.HasValue
                    ? value
                    : Math.Min(minimum.Value, value.Value);
                maximum = !maximum.HasValue
                    ? value
                    : Math.Max(maximum.Value, value.Value);
            }

            var metadata = CreateMetadata(index);
            metadata["sourceGeometry"] = "IrregularPointCloud";
            metadata["sourcePointCount"] = index.Count;
            metadata["resolutionMode"] = "DisplayRaster";
            metadata["requestedResolutionMode"] = query.ResolutionMode.ToString();
            metadata["displayRasterWidth"] = width;
            metadata["displayRasterHeight"] = height;
            metadata["verticalReference"] = "MSL";
            metadata["requestedBounds"] = new[]
            {
                query.MinLatitude,
                query.MaxLatitude,
                query.MinLongitude,
                query.MaxLongitude
            };
            metadata["sourceBounds"] = new[]
            {
                index.MinLatitude,
                index.MaxLatitude,
                index.MinLongitude,
                index.MaxLongitude
            };

            return new GridResult
            {
                SourceId = Id,
                Type = EnvironmentType.Bathymetry,
                Width = width,
                Height = height,
                Latitudes = latitudes,
                Longitudes = longitudes,
                Values = values,
                Unit = string.IsNullOrWhiteSpace(_option.Unit) ? "m" : _option.Unit,
                DateTime = query.DateTime,
                Variable = _option.Variable,
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        public void Dispose()
        {
            lock (_indexSync)
                _index = null;
        }

        private SpatialIndex EnsureIndex()
        {
            var existing = _index;
            if (existing != null)
                return existing;

            lock (_indexSync)
            {
                if (_index != null)
                    return _index;

                _index = LoadSpatialIndex();
                StatusMessage =
                    $"{_index.Count:N0} paired bathymetry points / spatial bins {_index.LatitudeBinCount:N0} x {_index.LongitudeBinCount:N0} / nearest cutoff {_maxNearestDistanceKm:0.###} km";
                return _index;
            }
        }

        private SpatialIndex LoadSpatialIndex()
        {
            using var file = Open(_resolvedPath);
            var layout = ReadLayout(file.Id);
            var count = layout.PointCount;

            var latitudes = new float[count];
            var longitudes = new float[count];
            var depths = new float[count];

            var minimumLatitude = double.PositiveInfinity;
            var maximumLatitude = double.NegativeInfinity;
            var minimumLongitude = double.PositiveInfinity;
            var maximumLongitude = double.NegativeInfinity;

            var latitudeBuffer = new double[Math.Min(ReadChunkSize, count)];
            var longitudeBuffer = new double[Math.Min(ReadChunkSize, count)];
            var depthBuffer = new double[Math.Min(ReadChunkSize, count)];

            for (var startIndex = 0; startIndex < count; startIndex += ReadChunkSize)
            {
                var chunkCount = Math.Min(ReadChunkSize, count - startIndex);
                ReadChunk(file.Id, layout.LatitudeVariableId, startIndex, chunkCount, latitudeBuffer);
                ReadChunk(file.Id, layout.LongitudeVariableId, startIndex, chunkCount, longitudeBuffer);
                ReadChunk(file.Id, layout.DepthVariableId, startIndex, chunkCount, depthBuffer);

                for (var offset = 0; offset < chunkCount; offset++)
                {
                    var latitude = latitudeBuffer[offset];
                    var longitude = longitudeBuffer[offset];
                    var depth = depthBuffer[offset];

                    if (!IsFinite(latitude) ||
                        !IsFinite(longitude) ||
                        !IsFinite(depth) ||
                        latitude < -90 || latitude > 90 ||
                        longitude < -360 || longitude > 360)
                    {
                        latitudes[startIndex + offset] = float.NaN;
                        longitudes[startIndex + offset] = float.NaN;
                        depths[startIndex + offset] = float.NaN;
                        continue;
                    }

                    latitudes[startIndex + offset] = (float)latitude;
                    longitudes[startIndex + offset] = (float)longitude;
                    depths[startIndex + offset] = (float)depth;

                    minimumLatitude = Math.Min(minimumLatitude, latitude);
                    maximumLatitude = Math.Max(maximumLatitude, latitude);
                    minimumLongitude = Math.Min(minimumLongitude, longitude);
                    maximumLongitude = Math.Max(maximumLongitude, longitude);
                }
            }

            if (!IsFinite(minimumLatitude) ||
                !IsFinite(maximumLatitude) ||
                !IsFinite(minimumLongitude) ||
                !IsFinite(maximumLongitude))
            {
                throw new InvalidDataException("BADA source contains no valid LAT/LON/MSL records.");
            }

            var binSize = PreferredBinSizeDegrees;
            int latitudeBinCount;
            int longitudeBinCount;
            while (true)
            {
                latitudeBinCount = Math.Max(
                    1,
                    checked((int)Math.Ceiling((maximumLatitude - minimumLatitude) / binSize) + 1));
                longitudeBinCount = Math.Max(
                    1,
                    checked((int)Math.Ceiling((maximumLongitude - minimumLongitude) / binSize) + 1));

                if ((long)latitudeBinCount * longitudeBinCount <= MaxSpatialBins)
                    break;

                binSize *= 2.0;
            }

            var heads = new int[checked(latitudeBinCount * longitudeBinCount)];
            Array.Fill(heads, -1);
            var next = new int[count];
            Array.Fill(next, -1);

            for (var i = 0; i < count; i++)
            {
                if (float.IsNaN(latitudes[i]) ||
                    float.IsNaN(longitudes[i]) ||
                    float.IsNaN(depths[i]))
                {
                    continue;
                }

                var latitudeBin = ToBin(
                    latitudes[i],
                    minimumLatitude,
                    binSize,
                    latitudeBinCount);
                var longitudeBin = ToBin(
                    longitudes[i],
                    minimumLongitude,
                    binSize,
                    longitudeBinCount);
                var binIndex = (latitudeBin * longitudeBinCount) + longitudeBin;

                next[i] = heads[binIndex];
                heads[binIndex] = i;
            }

            return new SpatialIndex(
                latitudes,
                longitudes,
                depths,
                heads,
                next,
                minimumLatitude,
                maximumLatitude,
                minimumLongitude,
                maximumLongitude,
                binSize,
                latitudeBinCount,
                longitudeBinCount);
        }

        private Layout ReadLayout(int ncid)
        {
            var latitudeVariable = string.IsNullOrWhiteSpace(_option.LatitudeVariable)
                ? "LAT"
                : _option.LatitudeVariable;
            var longitudeVariable = string.IsNullOrWhiteSpace(_option.LongitudeVariable)
                ? "LON"
                : _option.LongitudeVariable;
            var depthVariable = string.IsNullOrWhiteSpace(_option.Variable)
                ? "MSL"
                : _option.Variable;

            var latitude = ReadOneDimensionalVariable(ncid, latitudeVariable);
            var longitude = ReadOneDimensionalVariable(ncid, longitudeVariable);
            var depth = ReadOneDimensionalVariable(ncid, depthVariable);

            if (latitude.DimensionId != longitude.DimensionId ||
                latitude.DimensionId != depth.DimensionId)
            {
                throw new InvalidDataException(
                    "BADA LAT, LON and MSL variables must share the same one-dimensional point dimension.");
            }

            if (latitude.Length != longitude.Length ||
                latitude.Length != depth.Length)
            {
                throw new InvalidDataException(
                    "BADA LAT, LON and MSL variables must have identical lengths.");
            }

            if (latitude.Length <= 0 || latitude.Length > int.MaxValue)
                throw new InvalidDataException(
                    $"BADA point dimension length {latitude.Length:N0} is not supported.");

            return new Layout(
                latitude.VariableId,
                longitude.VariableId,
                depth.VariableId,
                checked((int)latitude.Length));
        }

        private static VariableLayout ReadOneDimensionalVariable(
            int ncid,
            string variableName)
        {
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varid(ncid, variableName, out var variableId),
                $"Find BADA variable '{variableName}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varndims(ncid, variableId, out var dimensionCount),
                $"Read dimensions for BADA variable '{variableName}'");

            if (dimensionCount != 1)
                throw new InvalidDataException(
                    $"BADA variable '{variableName}' must be one-dimensional.");

            var dimensionIds = new int[1];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_vardimid(ncid, variableId, dimensionIds),
                $"Read dimension id for BADA variable '{variableName}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_dimlen(ncid, dimensionIds[0], out var length),
                $"Read dimension length for BADA variable '{variableName}'");

            var rawLength = length.ToUInt64();
            if (rawLength > long.MaxValue)
                throw new InvalidDataException(
                    $"BADA variable '{variableName}' is too large.");

            return new VariableLayout(
                variableId,
                dimensionIds[0],
                checked((long)rawLength));
        }

        private static void ReadChunk(
            int ncid,
            int variableId,
            int startIndex,
            int count,
            double[] buffer)
        {
            var start = new[] { new UIntPtr((uint)startIndex) };
            var counts = new[] { new UIntPtr((uint)count) };

            if (buffer.Length == count)
            {
                NetCdfNative.ThrowIfError(
                    NetCdfNative.nc_get_vara_double(
                        ncid,
                        variableId,
                        start,
                        counts,
                        buffer),
                    "Read BADA NetCDF chunk");
                return;
            }

            var temporary = new double[count];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_get_vara_double(
                    ncid,
                    variableId,
                    start,
                    counts,
                    temporary),
                "Read BADA NetCDF chunk");
            Array.Copy(temporary, 0, buffer, 0, count);
        }

        private Dictionary<string, object?> CreateMetadata(SpatialIndex index)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();

            metadata["file"] = _resolvedPath;
            metadata["sampling"] = "Nearest";
            metadata["sourceLatitudeVariable"] = _option.LatitudeVariable;
            metadata["sourceLongitudeVariable"] = _option.LongitudeVariable;
            metadata["sourceDepthVariable"] = _option.Variable;
            metadata["spatialBinSizeDegrees"] = index.BinSizeDegrees;
            metadata["maxNearestDistanceKm"] = _maxNearestDistanceKm;
            return metadata;
        }

        private static int ToBin(
            double value,
            double minimum,
            double binSize,
            int binCount)
        {
            var index = (int)Math.Floor((value - minimum) / binSize);
            if (index < 0)
                return 0;
            if (index >= binCount)
                return binCount - 1;
            return index;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double HaversineKilometers(
            double latitude1,
            double longitude1,
            double latitude2,
            double longitude2)
        {
            const double earthRadiusKm = 6371.0088;
            var radians = Math.PI / 180.0;
            var dLatitude = (latitude2 - latitude1) * radians;
            var dLongitude = (longitude2 - longitude1) * radians;
            var lat1 = latitude1 * radians;
            var lat2 = latitude2 * radians;

            var a =
                (Math.Sin(dLatitude / 2.0) * Math.Sin(dLatitude / 2.0)) +
                (Math.Cos(lat1) *
                 Math.Cos(lat2) *
                 Math.Sin(dLongitude / 2.0) *
                 Math.Sin(dLongitude / 2.0));

            return 2.0 * earthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
        }

        private static NetCdfFile Open(string path)
        {
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_open(
                    path,
                    NetCdfNative.Nowrite,
                    out var ncid),
                $"Open NetCDF '{path}'");
            return new NetCdfFile(ncid);
        }

        private readonly struct Layout
        {
            public Layout(
                int latitudeVariableId,
                int longitudeVariableId,
                int depthVariableId,
                int pointCount)
            {
                LatitudeVariableId = latitudeVariableId;
                LongitudeVariableId = longitudeVariableId;
                DepthVariableId = depthVariableId;
                PointCount = pointCount;
            }

            public int LatitudeVariableId { get; }
            public int LongitudeVariableId { get; }
            public int DepthVariableId { get; }
            public int PointCount { get; }
        }

        private readonly struct VariableLayout
        {
            public VariableLayout(int variableId, int dimensionId, long length)
            {
                VariableId = variableId;
                DimensionId = dimensionId;
                Length = length;
            }

            public int VariableId { get; }
            public int DimensionId { get; }
            public long Length { get; }
        }

        private readonly struct NearestSample
        {
            public NearestSample(
                int sourceIndex,
                double latitude,
                double longitude,
                double depthMeters,
                double distanceKm)
            {
                SourceIndex = sourceIndex;
                Latitude = latitude;
                Longitude = longitude;
                DepthMeters = depthMeters;
                DistanceKm = distanceKm;
            }

            public int SourceIndex { get; }
            public double Latitude { get; }
            public double Longitude { get; }
            public double DepthMeters { get; }
            public double DistanceKm { get; }
        }

        private sealed class SpatialIndex
        {
            private readonly float[] _latitudes;
            private readonly float[] _longitudes;
            private readonly float[] _depths;
            private readonly int[] _heads;
            private readonly int[] _next;

            public SpatialIndex(
                float[] latitudes,
                float[] longitudes,
                float[] depths,
                int[] heads,
                int[] next,
                double minLatitude,
                double maxLatitude,
                double minLongitude,
                double maxLongitude,
                double binSizeDegrees,
                int latitudeBinCount,
                int longitudeBinCount)
            {
                _latitudes = latitudes;
                _longitudes = longitudes;
                _depths = depths;
                _heads = heads;
                _next = next;
                MinLatitude = minLatitude;
                MaxLatitude = maxLatitude;
                MinLongitude = minLongitude;
                MaxLongitude = maxLongitude;
                BinSizeDegrees = binSizeDegrees;
                LatitudeBinCount = latitudeBinCount;
                LongitudeBinCount = longitudeBinCount;
            }

            public int Count => _latitudes.Length;
            public double MinLatitude { get; }
            public double MaxLatitude { get; }
            public double MinLongitude { get; }
            public double MaxLongitude { get; }
            public double BinSizeDegrees { get; }
            public int LatitudeBinCount { get; }
            public int LongitudeBinCount { get; }

            public NearestSample? FindNearest(
                double latitude,
                double longitude,
                double maxDistanceKm)
            {
                if (latitude < MinLatitude ||
                    latitude > MaxLatitude ||
                    longitude < MinLongitude ||
                    longitude > MaxLongitude)
                {
                    return null;
                }

                var centerLatitudeBin = ToBin(
                    latitude,
                    MinLatitude,
                    BinSizeDegrees,
                    LatitudeBinCount);
                var centerLongitudeBin = ToBin(
                    longitude,
                    MinLongitude,
                    BinSizeDegrees,
                    LongitudeBinCount);

                var cosineLatitude = Math.Max(
                    0.05,
                    Math.Abs(Math.Cos(latitude * Math.PI / 180.0)));
                var conservativeBinKm = Math.Min(
                    BinSizeDegrees * 110.0,
                    BinSizeDegrees * 110.0 * cosineLatitude);
                var maximumRing = Math.Max(
                    1,
                    (int)Math.Ceiling(maxDistanceKm / conservativeBinKm) + 1);

                var bestIndex = -1;
                var bestDistance = double.PositiveInfinity;

                for (var ring = 0; ring <= maximumRing; ring++)
                {
                    var minLatitudeBin = Math.Max(0, centerLatitudeBin - ring);
                    var maxLatitudeBin = Math.Min(
                        LatitudeBinCount - 1,
                        centerLatitudeBin + ring);
                    var minLongitudeBin = Math.Max(0, centerLongitudeBin - ring);
                    var maxLongitudeBin = Math.Min(
                        LongitudeBinCount - 1,
                        centerLongitudeBin + ring);

                    if (ring == 0)
                    {
                        SearchBin(
                            minLatitudeBin,
                            minLongitudeBin,
                            latitude,
                            longitude,
                            ref bestIndex,
                            ref bestDistance);
                    }
                    else
                    {
                        for (var longitudeBin = minLongitudeBin;
                             longitudeBin <= maxLongitudeBin;
                             longitudeBin++)
                        {
                            SearchBin(
                                minLatitudeBin,
                                longitudeBin,
                                latitude,
                                longitude,
                                ref bestIndex,
                                ref bestDistance);

                            if (maxLatitudeBin != minLatitudeBin)
                            {
                                SearchBin(
                                    maxLatitudeBin,
                                    longitudeBin,
                                    latitude,
                                    longitude,
                                    ref bestIndex,
                                    ref bestDistance);
                            }
                        }

                        for (var latitudeBin = minLatitudeBin + 1;
                             latitudeBin < maxLatitudeBin;
                             latitudeBin++)
                        {
                            SearchBin(
                                latitudeBin,
                                minLongitudeBin,
                                latitude,
                                longitude,
                                ref bestIndex,
                                ref bestDistance);

                            if (maxLongitudeBin != minLongitudeBin)
                            {
                                SearchBin(
                                    latitudeBin,
                                    maxLongitudeBin,
                                    latitude,
                                    longitude,
                                    ref bestIndex,
                                    ref bestDistance);
                            }
                        }
                    }

                    if (bestIndex >= 0)
                    {
                        var searchedMinLatitude =
                            MinLatitude + (minLatitudeBin * BinSizeDegrees);
                        var searchedMaxLatitude =
                            MinLatitude + ((maxLatitudeBin + 1) * BinSizeDegrees);
                        var searchedMinLongitude =
                            MinLongitude + (minLongitudeBin * BinSizeDegrees);
                        var searchedMaxLongitude =
                            MinLongitude + ((maxLongitudeBin + 1) * BinSizeDegrees);

                        var latitudeBoundaryDistance =
                            Math.Min(
                                Math.Abs(latitude - searchedMinLatitude),
                                Math.Abs(searchedMaxLatitude - latitude)) *
                            110.0;
                        var longitudeBoundaryDistance =
                            Math.Min(
                                Math.Abs(longitude - searchedMinLongitude),
                                Math.Abs(searchedMaxLongitude - longitude)) *
                            110.0 *
                            cosineLatitude;

                        var conservativeUnseenDistance =
                            Math.Max(
                                0.0,
                                Math.Min(
                                    latitudeBoundaryDistance,
                                    longitudeBoundaryDistance));

                        if (bestDistance <= conservativeUnseenDistance)
                            break;
                    }
                }

                if (bestIndex < 0 || bestDistance > maxDistanceKm)
                    return null;

                return new NearestSample(
                    bestIndex,
                    _latitudes[bestIndex],
                    _longitudes[bestIndex],
                    _depths[bestIndex],
                    bestDistance);
            }

            private void SearchBin(
                int latitudeBin,
                int longitudeBin,
                double targetLatitude,
                double targetLongitude,
                ref int bestIndex,
                ref double bestDistance)
            {
                if (latitudeBin < 0 ||
                    latitudeBin >= LatitudeBinCount ||
                    longitudeBin < 0 ||
                    longitudeBin >= LongitudeBinCount)
                {
                    return;
                }

                var binIndex =
                    (latitudeBin * LongitudeBinCount) +
                    longitudeBin;

                for (var pointIndex = _heads[binIndex];
                     pointIndex >= 0;
                     pointIndex = _next[pointIndex])
                {
                    var distance = HaversineKilometers(
                        targetLatitude,
                        targetLongitude,
                        _latitudes[pointIndex],
                        _longitudes[pointIndex]);

                    if (distance >= bestDistance)
                        continue;

                    bestDistance = distance;
                    bestIndex = pointIndex;
                }
            }
        }

        private sealed class NetCdfFile : IDisposable
        {
            public NetCdfFile(int id)
            {
                Id = id;
            }

            public int Id { get; }

            public void Dispose()
            {
                NetCdfNative.nc_close(Id);
            }
        }
    }
}
