using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Native;

namespace MarineEnvironment.Sources.Goci2
{
    /// <summary>
    /// Low-level GOCI-II Level-2 LA TSS mosaic reader.
    ///
    /// GOCI-II stores latitude/longitude as 2-D navigation arrays in /navigation_data and
    /// TSS/quality flags in /geophysical_data. The LA mosaic is therefore a curvilinear
    /// raster rather than a regular 1-D latitude/longitude grid.
    ///
    /// This class returns source TSS concentration from one selected mosaic. The public
    /// Goci2Tss source path is wrapped by Goci2TurbidityDataSource, which combines up to
    /// five mosaics at query time and converts the resulting mean TSS to derived turbidity.
    /// </summary>
    internal sealed class Goci2TssDataSource : IEnvironmentDataSource
    {
        private const string NavigationGroupName = "navigation_data";
        private const string GeophysicalGroupName = "geophysical_data";
        private const string DefaultTssVariable = "TSS";
        private const string DefaultLatitudeVariable = "latitude";
        private const string DefaultLongitudeVariable = "longitude";
        private const string FlagVariable = "flag";
        private const int GeoIndexStride = 20;
        private const int GridBlockRows = 64;
        private const double MaxPointDistanceKm = 1.0;
        private const int InvalidQualityMask = 0x0F; // Cloud/Ice, Land, AC_Fail, TSS_Fail

        private static readonly Regex MosaicFileRegex = new Regex(
            @"^GK2B_GOCI2_L2_(?<date>\d{8})_(?<time>\d{6})_LA_TSS\.nc$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private const int ProjectionCandidateCount = 8;
        private static readonly object SharedNavigationSync = new object();
        private static readonly Dictionary<string, GeoIndex> SharedGeoIndexes =
            new Dictionary<string, GeoIndex>(StringComparer.OrdinalIgnoreCase);
        private static string? _sharedProjectionKey;
        private static GridProjection? _sharedProjection;

        private readonly DataSourceOption _option;
        private readonly string _resolvedPath;
        private readonly object _indexSync = new object();
        private GeoIndex? _geoIndex;
        private string? _geoIndexFile;

        public Goci2TssDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option;
            _resolvedPath = resolvedPath;
            if (!option.Enabled) { Status = SourceStatus.Disabled; return; }
            try
            {
                var validationFile = ResolveValidationFile();
                if (validationFile == null || !File.Exists(validationFile))
                {
                    Status = SourceStatus.FileNotFound;
                    StatusMessage = validationFile ?? resolvedPath;
                    return;
                }
                using var file = Open(validationFile);
                using var context = OpenContext(file.Id);
                ValidateShape(context);
                Status = SourceStatus.Ready;
                StatusMessage = "GOCI-II LA TSS mosaic (2-D geolocation, 250 m nominal resolution)";
            }
            catch (DllNotFoundException ex) { Status = SourceStatus.NativeLibraryUnavailable; StatusMessage = ex.Message; }
            catch (Exception ex) { Status = SourceStatus.Error; StatusMessage = ex.Message; }
        }

        public string Id => _option.Id;
        public EnvironmentType Type => _option.Type;
        public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
        public string? StatusMessage { get; private set; }

        public EnvironmentValue? Query(EnvironmentQuery query)
        {
            if (Status != SourceStatus.Ready) return null;
            var filePath = ResolveFile(query.DateTime);
            if (filePath == null || !File.Exists(filePath)) return null;

            var index = GetGeoIndex(filePath);
            if (index.Samples.Count == 0) return null;

            var coarse = FindNearest(index.Samples, query.Latitude, query.Longitude);
            var rowStart = Math.Max(0, coarse.Row - GeoIndexStride);
            var rowEnd = Math.Min(index.Rows - 1, coarse.Row + GeoIndexStride);
            var colStart = Math.Max(0, coarse.Column - GeoIndexStride);
            var colEnd = Math.Min(index.Columns - 1, coarse.Column + GeoIndexStride);

            using var file = Open(filePath);
            using var context = OpenContext(file.Id);
            var nearest = FindNearestPixel(context, rowStart, rowEnd, colStart, colEnd, query.Latitude, query.Longitude);
            if (!nearest.HasValue || nearest.Value.DistanceKm > MaxPointDistanceKm) return null;

            var pixel = nearest.Value;
            var rawTss = ReadCell(context.GeophysicalGroupId, context.TssVariableId, pixel.Row, pixel.Column);
            var tss = TransformTss(context, rawTss);
            if (!tss.HasValue) return null;

            var qualityFlag = context.FlagVariableId.HasValue
                ? (int)Math.Round(ReadCell(context.GeophysicalGroupId, context.FlagVariableId.Value, pixel.Row, pixel.Column))
                : 0;
            if ((qualityFlag & InvalidQualityMask) != 0) return null;

            var observationUtc = ParseObservationUtc(filePath);
            var metadata = CreateMetadata(filePath, observationUtc);
            metadata["qualityFlag"] = qualityFlag;
            metadata["sourcePixel"] = new[] { pixel.Row, pixel.Column };
            metadata["sourceDistanceKm"] = pixel.DistanceKm;

            return new EnvironmentValue(
                Id, Type, tss.Value, _option.Unit ?? "g/m^3",
                pixel.Latitude, pixel.Longitude, null, observationUtc,
                GetTssVariableName(), metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"Source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.Width < 2 || query.Width > 2048)
                throw new ArgumentOutOfRangeException(nameof(query.Width), "Grid width must be between 2 and 2048.");
            if (query.Height < 2 || query.Height > 2048)
                throw new ArgumentOutOfRangeException(nameof(query.Height), "Grid height must be between 2 and 2048.");

            var filePath = ResolveFile(query.DateTime);
            if (filePath == null || !File.Exists(filePath))
                throw new FileNotFoundException($"GOCI-II TSS mosaic file for '{Id}' was not found.", filePath ?? _resolvedPath);

            var index = GetGeoIndex(filePath);
            var latitudes = BuildDescendingAxis(query.MaxLatitude, query.MinLatitude, query.Height);
            var longitudes = BuildAscendingAxis(query.MinLongitude, query.MaxLongitude, query.Width);
            var projection = GetOrBuildGridProjection(filePath, index, query, latitudes, longitudes);

            using var file = Open(filePath);
            using var context = OpenContext(file.Id);
            if (context.Rows != projection.SourceRows || context.Columns != projection.SourceColumns)
                throw new InvalidDataException("GOCI-II mosaic navigation geometry changed between files; reload the source before rendering.");

            var values = ReadProjectedTss(context, projection);
            double? minimum = null, maximum = null;
            var validCells = 0;
            foreach (var value in values)
            {
                if (!value.HasValue) continue;
                validCells++;
                minimum = !minimum.HasValue ? value : Math.Min(minimum.Value, value.Value);
                maximum = !maximum.HasValue ? value : Math.Max(maximum.Value, value.Value);
            }

            var observationUtc = ParseObservationUtc(filePath);
            var metadata = CreateMetadata(filePath, observationUtc);
            metadata["requestedBounds"] = new[] { query.MinLatitude, query.MaxLatitude, query.MinLongitude, query.MaxLongitude };
            metadata["renderGrid"] = new[] { query.Width, query.Height };
            metadata["resolutionMode"] = query.ResolutionMode.ToString();
            metadata["sourceNativeRaster"] = false;
            metadata["curvilinearGeolocation"] = true;
            metadata["qualityMask"] = "Cloud_or_Ice | Land | AC_Fail | TSS_Fail";
            metadata["navigationProjectionCache"] = true;
            metadata["projectionCandidateCount"] = projection.CandidateCount;
            metadata["validRenderedCells"] = validCells;

            return new GridResult
            {
                SourceId = Id,
                Type = Type,
                Width = query.Width,
                Height = query.Height,
                Latitudes = latitudes,
                Longitudes = longitudes,
                Values = values,
                Unit = _option.Unit ?? "g/m^3",
                DateTime = observationUtc,
                Variable = GetTssVariableName(),
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        private GeoIndex GetGeoIndex(string filePath)
        {
            lock (_indexSync)
            {
                if (_geoIndex != null && string.Equals(_geoIndexFile, filePath, StringComparison.OrdinalIgnoreCase))
                    return _geoIndex;
            }

            var sharedKey = BuildSharedNavigationKey(filePath);
            lock (SharedNavigationSync)
            {
                GeoIndex shared;
                if (SharedGeoIndexes.TryGetValue(sharedKey, out shared))
                {
                    lock (_indexSync)
                    {
                        _geoIndex = shared;
                        _geoIndexFile = filePath;
                    }
                    return shared;
                }
            }

            using var file = Open(filePath);
            using var context = OpenContext(file.Id);
            var samples = new List<GeoSample>((context.Rows / GeoIndexStride + 1) * (context.Columns / GeoIndexStride + 1));
            var latRow = new double[context.Columns];
            var lonRow = new double[context.Columns];
            for (var row = 0; row < context.Rows; row += GeoIndexStride)
            {
                ReadRow(context.NavigationGroupId, context.LatitudeVariableId, row, latRow);
                ReadRow(context.NavigationGroupId, context.LongitudeVariableId, row, lonRow);
                for (var column = 0; column < context.Columns; column += GeoIndexStride)
                {
                    var lat = latRow[column];
                    var lon = lonRow[column];
                    if (IsValidCoordinate(lat, lon)) samples.Add(new GeoSample(lat, lon, row, column));
                }
            }
            if ((context.Rows - 1) % GeoIndexStride != 0)
                AppendIndexRow(context, context.Rows - 1, samples, latRow, lonRow);

            var built = new GeoIndex(context.Rows, context.Columns, samples);
            lock (SharedNavigationSync)
            {
                SharedGeoIndexes[sharedKey] = built;
            }
            lock (_indexSync)
            {
                _geoIndex = built;
                _geoIndexFile = filePath;
            }
            return built;
        }

        private string BuildSharedNavigationKey(string filePath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
            var latitudeName = string.IsNullOrWhiteSpace(_option.LatitudeVariable) ? DefaultLatitudeVariable : _option.LatitudeVariable;
            var longitudeName = string.IsNullOrWhiteSpace(_option.LongitudeVariable) ? DefaultLongitudeVariable : _option.LongitudeVariable;
            return directory + "|" + latitudeName + "|" + longitudeName;
        }

        private static void AppendIndexRow(FileContext context, int row, List<GeoSample> samples, double[] latRow, double[] lonRow)
        {
            ReadRow(context.NavigationGroupId, context.LatitudeVariableId, row, latRow);
            ReadRow(context.NavigationGroupId, context.LongitudeVariableId, row, lonRow);
            for (var column = 0; column < context.Columns; column += GeoIndexStride)
            {
                var lat = latRow[column];
                var lon = lonRow[column];
                if (IsValidCoordinate(lat, lon)) samples.Add(new GeoSample(lat, lon, row, column));
            }
        }

        private static GeoSample FindNearest(IReadOnlyList<GeoSample> samples, double latitude, double longitude)
        {
            var best = samples[0];
            var bestDistance = GeographicDistanceSquaredKm(best.Latitude, best.Longitude, latitude, longitude);
            for (var i = 1; i < samples.Count; i++)
            {
                var candidate = samples[i];
                var distance = GeographicDistanceSquaredKm(candidate.Latitude, candidate.Longitude, latitude, longitude);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static PixelMatch? FindNearestPixel(FileContext context, int rowStart, int rowEnd, int colStart, int colEnd, double latitude, double longitude)
        {
            var rowCount = rowEnd - rowStart + 1;
            var colCount = colEnd - colStart + 1;
            var lats = ReadBlock(context.NavigationGroupId, context.LatitudeVariableId, rowStart, rowCount, colStart, colCount);
            var lons = ReadBlock(context.NavigationGroupId, context.LongitudeVariableId, rowStart, rowCount, colStart, colCount);
            PixelMatch? best = null;
            var bestDistance2 = double.PositiveInfinity;
            for (var localRow = 0; localRow < rowCount; localRow++)
            {
                for (var localCol = 0; localCol < colCount; localCol++)
                {
                    var index = (localRow * colCount) + localCol;
                    var lat = lats[index];
                    var lon = lons[index];
                    if (!IsValidCoordinate(lat, lon)) continue;
                    var distance2 = GeographicDistanceSquaredKm(lat, lon, latitude, longitude);
                    if (distance2 >= bestDistance2) continue;
                    bestDistance2 = distance2;
                    best = new PixelMatch(rowStart + localRow, colStart + localCol, lat, lon, Math.Sqrt(distance2));
                }
            }
            return best;
        }

        private static bool TryGetSourceWindow(GeoIndex index, GridQuery query, out SourceWindow window)
        {
            var marginDegrees = 0.25;
            var candidates = index.Samples.Where(x =>
                x.Latitude >= query.MinLatitude - marginDegrees &&
                x.Latitude <= query.MaxLatitude + marginDegrees &&
                x.Longitude >= query.MinLongitude - marginDegrees &&
                x.Longitude <= query.MaxLongitude + marginDegrees).ToArray();
            if (candidates.Length == 0) { window = default; return false; }
            window = new SourceWindow(
                Math.Max(0, candidates.Min(x => x.Row) - GeoIndexStride),
                Math.Min(index.Rows - 1, candidates.Max(x => x.Row) + GeoIndexStride),
                Math.Max(0, candidates.Min(x => x.Column) - GeoIndexStride),
                Math.Min(index.Columns - 1, candidates.Max(x => x.Column) + GeoIndexStride));
            return true;
        }

        private GridProjection GetOrBuildGridProjection(
            string filePath,
            GeoIndex index,
            GridQuery query,
            double[] latitudes,
            double[] longitudes)
        {
            var key = BuildGridProjectionKey(filePath, query);
            lock (SharedNavigationSync)
            {
                if (_sharedProjection != null
                    && string.Equals(_sharedProjectionKey, key, StringComparison.Ordinal)
                    && _sharedProjection.SourceRows == index.Rows
                    && _sharedProjection.SourceColumns == index.Columns)
                {
                    return _sharedProjection;
                }
            }

            var built = BuildGridProjection(filePath, index, query, latitudes, longitudes);
            lock (SharedNavigationSync)
            {
                _sharedProjectionKey = key;
                _sharedProjection = built;
            }
            return built;
        }

        private string BuildGridProjectionKey(string filePath, GridQuery query)
        {
            return string.Join("|",
                BuildSharedNavigationKey(filePath),
                query.MinLatitude.ToString("R", CultureInfo.InvariantCulture),
                query.MaxLatitude.ToString("R", CultureInfo.InvariantCulture),
                query.MinLongitude.ToString("R", CultureInfo.InvariantCulture),
                query.MaxLongitude.ToString("R", CultureInfo.InvariantCulture),
                query.Width.ToString(CultureInfo.InvariantCulture),
                query.Height.ToString(CultureInfo.InvariantCulture));
        }

        private GridProjection BuildGridProjection(
            string filePath,
            GeoIndex index,
            GridQuery query,
            double[] latitudes,
            double[] longitudes)
        {
            var outputCellCount = checked(query.Width * query.Height);
            var candidateCount = outputCellCount > 1_000_000 ? 2 : ProjectionCandidateCount;
            var candidates = new ProjectionCandidate[checked(outputCellCount * candidateCount)];
            var counts = new byte[outputCellCount];

            SourceWindow window;
            if (!TryGetSourceWindow(index, query, out window))
                return new GridProjection(index.Rows, index.Columns, query.Width, query.Height, candidateCount, candidates, counts);

            using var file = Open(filePath);
            var rootId = file.Id;
            // Navigation variable IDs are resolved directly because this projection is shared
            // across all mosaics with the same LA geometry.
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_ncid(rootId, NavigationGroupName, out var navigationGroupId), "Find GOCI-II navigation_data group");
            var latitudeName = string.IsNullOrWhiteSpace(_option.LatitudeVariable) ? DefaultLatitudeVariable : _option.LatitudeVariable;
            var longitudeName = string.IsNullOrWhiteSpace(_option.LongitudeVariable) ? DefaultLongitudeVariable : _option.LongitudeVariable;
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(navigationGroupId, latitudeName, out var latitudeVariableId), $"Find GOCI-II latitude variable '{latitudeName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(navigationGroupId, longitudeName, out var longitudeVariableId), $"Find GOCI-II longitude variable '{longitudeName}'");

            for (var row = window.RowStart; row <= window.RowEnd; row += GridBlockRows)
            {
                var rowCount = Math.Min(GridBlockRows, window.RowEnd - row + 1);
                var colCount = window.ColumnEnd - window.ColumnStart + 1;
                var lats = ReadBlock(navigationGroupId, latitudeVariableId, row, rowCount, window.ColumnStart, colCount);
                var lons = ReadBlock(navigationGroupId, longitudeVariableId, row, rowCount, window.ColumnStart, colCount);

                for (var localRow = 0; localRow < rowCount; localRow++)
                {
                    for (var localCol = 0; localCol < colCount; localCol++)
                    {
                        var sourceIndex = (localRow * colCount) + localCol;
                        var lat = lats[sourceIndex];
                        var lon = lons[sourceIndex];
                        if (!IsValidCoordinate(lat, lon)) continue;
                        if (lat < query.MinLatitude || lat > query.MaxLatitude || lon < query.MinLongitude || lon > query.MaxLongitude)
                            continue;

                        var outputRow = NearestOutputIndexDescending(latitudes, lat);
                        var outputColumn = NearestOutputIndexAscending(longitudes, lon);
                        var outputIndex = (outputRow * query.Width) + outputColumn;
                        var distance2 = GeographicDistanceSquaredKm(lat, lon, latitudes[outputRow], longitudes[outputColumn]);

                        InsertProjectionCandidate(
                            candidates,
                            counts,
                            candidateCount,
                            outputIndex,
                            new ProjectionCandidate(row + localRow, window.ColumnStart + localCol, distance2));
                    }
                }
            }

            return new GridProjection(index.Rows, index.Columns, query.Width, query.Height, candidateCount, candidates, counts);
        }

        private static void InsertProjectionCandidate(
            ProjectionCandidate[] candidates,
            byte[] counts,
            int candidateCount,
            int outputIndex,
            ProjectionCandidate candidate)
        {
            var baseIndex = outputIndex * candidateCount;
            var count = counts[outputIndex];
            var insertAt = count;

            for (var i = 0; i < count; i++)
            {
                if (candidate.Distance2 < candidates[baseIndex + i].Distance2)
                {
                    insertAt = i;
                    break;
                }
            }

            if (count >= candidateCount && insertAt >= candidateCount)
                return;

            var newCount = Math.Min(candidateCount, count + 1);
            for (var i = newCount - 1; i > insertAt; i--)
                candidates[baseIndex + i] = candidates[baseIndex + i - 1];

            candidates[baseIndex + insertAt] = candidate;
            counts[outputIndex] = (byte)newCount;
        }

        private static double?[] ReadProjectedTss(FileContext context, GridProjection projection)
        {
            var candidateValues = new double?[projection.Candidates.Length];
            var rowPlans = new Dictionary<int, ProjectionRowPlan>();

            for (var outputIndex = 0; outputIndex < projection.Counts.Length; outputIndex++)
            {
                var count = projection.Counts[outputIndex];
                var baseIndex = outputIndex * projection.CandidateCount;
                for (var i = 0; i < count; i++)
                {
                    var slot = baseIndex + i;
                    var candidate = projection.Candidates[slot];
                    ProjectionRowPlan plan;
                    if (!rowPlans.TryGetValue(candidate.Row, out plan))
                    {
                        plan = new ProjectionRowPlan(candidate.Column, candidate.Column);
                        rowPlans.Add(candidate.Row, plan);
                    }
                    else
                    {
                        if (candidate.Column < plan.MinColumn) plan.MinColumn = candidate.Column;
                        if (candidate.Column > plan.MaxColumn) plan.MaxColumn = candidate.Column;
                    }
                    plan.CandidateSlots.Add(slot);
                }
            }

            foreach (var pair in rowPlans)
            {
                var row = pair.Key;
                var plan = pair.Value;
                var columnCount = plan.MaxColumn - plan.MinColumn + 1;
                var tss = ReadBlock(context.GeophysicalGroupId, context.TssVariableId, row, 1, plan.MinColumn, columnCount);
                var flags = context.FlagVariableId.HasValue
                    ? ReadBlock(context.GeophysicalGroupId, context.FlagVariableId.Value, row, 1, plan.MinColumn, columnCount)
                    : null;

                foreach (var slot in plan.CandidateSlots)
                {
                    var candidate = projection.Candidates[slot];
                    var localColumn = candidate.Column - plan.MinColumn;
                    var value = TransformTss(context, tss[localColumn]);
                    if (!value.HasValue)
                        continue;

                    if (flags != null)
                    {
                        var flag = (int)Math.Round(flags[localColumn]);
                        if ((flag & InvalidQualityMask) != 0)
                            continue;
                    }

                    candidateValues[slot] = value.Value;
                }
            }

            var result = new double?[projection.Counts.Length];
            for (var outputIndex = 0; outputIndex < result.Length; outputIndex++)
            {
                var count = projection.Counts[outputIndex];
                var baseIndex = outputIndex * projection.CandidateCount;
                for (var i = 0; i < count; i++)
                {
                    var value = candidateValues[baseIndex + i];
                    if (!value.HasValue)
                        continue;
                    result[outputIndex] = value.Value;
                    break;
                }
            }

            return result;
        }

        private FileContext OpenContext(int rootId)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_ncid(rootId, NavigationGroupName, out var navigationGroupId), "Find GOCI-II navigation_data group");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_ncid(rootId, GeophysicalGroupName, out var geophysicalGroupId), "Find GOCI-II geophysical_data group");
            var latitudeName = string.IsNullOrWhiteSpace(_option.LatitudeVariable) ? DefaultLatitudeVariable : _option.LatitudeVariable;
            var longitudeName = string.IsNullOrWhiteSpace(_option.LongitudeVariable) ? DefaultLongitudeVariable : _option.LongitudeVariable;
            var tssName = GetTssVariableName();
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(navigationGroupId, latitudeName, out var latitudeVariableId), $"Find GOCI-II latitude variable '{latitudeName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(navigationGroupId, longitudeName, out var longitudeVariableId), $"Find GOCI-II longitude variable '{longitudeName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(geophysicalGroupId, tssName, out var tssVariableId), $"Find GOCI-II TSS variable '{tssName}'");
            int? flagVariableId = null;
            if (NetCdfNative.nc_inq_varid(geophysicalGroupId, FlagVariable, out var flagId) == NetCdfNative.NoError) flagVariableId = flagId;

            var shape = Get2DShape(geophysicalGroupId, tssVariableId, tssName);
            var latShape = Get2DShape(navigationGroupId, latitudeVariableId, latitudeName);
            var lonShape = Get2DShape(navigationGroupId, longitudeVariableId, longitudeName);
            if (shape.Rows != latShape.Rows || shape.Columns != latShape.Columns || shape.Rows != lonShape.Rows || shape.Columns != lonShape.Columns)
                throw new InvalidDataException("GOCI-II TSS and navigation latitude/longitude dimensions do not match.");
            if (flagVariableId.HasValue)
            {
                var flagShape = Get2DShape(geophysicalGroupId, flagVariableId.Value, FlagVariable);
                if (shape.Rows != flagShape.Rows || shape.Columns != flagShape.Columns)
                    throw new InvalidDataException("GOCI-II TSS and quality flag dimensions do not match.");
            }

            return new FileContext(
                navigationGroupId, geophysicalGroupId, latitudeVariableId, longitudeVariableId,
                tssVariableId, flagVariableId, shape.Rows, shape.Columns,
                TryReadAttribute(geophysicalGroupId, tssVariableId, "_FillValue"),
                TryReadAttribute(geophysicalGroupId, tssVariableId, "scale_factor") ?? 1.0,
                TryReadAttribute(geophysicalGroupId, tssVariableId, "add_offset") ?? 0.0,
                TryReadAttribute(geophysicalGroupId, tssVariableId, "valid_min") ?? 0.0,
                TryReadAttribute(geophysicalGroupId, tssVariableId, "valid_max") ?? 1000.0);
        }

        private static Shape Get2DShape(int groupId, int variableId, string variableName)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(groupId, variableId, out var ndims), $"Read dimensions for '{variableName}'");
            if (ndims != 2) throw new InvalidDataException($"GOCI-II variable '{variableName}' must be 2-D, but has {ndims} dimensions.");
            var dimIds = new int[2];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(groupId, variableId, dimIds), $"Read dimension ids for '{variableName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_dimlen(groupId, dimIds[0], out var rows), $"Read rows for '{variableName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_dimlen(groupId, dimIds[1], out var columns), $"Read columns for '{variableName}'");
            return new Shape(checked((int)rows.ToUInt64()), checked((int)columns.ToUInt64()));
        }

        private static void ValidateShape(FileContext context)
        {
            if (context.Rows <= 0 || context.Columns <= 0) throw new InvalidDataException("GOCI-II TSS has an empty raster.");
        }

        private static double[] ReadBlock(int groupId, int variableId, int rowStart, int rowCount, int columnStart, int columnCount)
        {
            var start = new[] { (UIntPtr)(uint)rowStart, (UIntPtr)(uint)columnStart };
            var count = new[] { (UIntPtr)(uint)rowCount, (UIntPtr)(uint)columnCount };
            var values = new double[checked(rowCount * columnCount)];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_vara_double(groupId, variableId, start, count, values), "Read GOCI-II NetCDF block");
            return values;
        }

        private static void ReadRow(int groupId, int variableId, int row, double[] buffer)
        {
            var start = new[] { (UIntPtr)(uint)row, UIntPtr.Zero };
            var count = new[] { (UIntPtr)1u, (UIntPtr)(uint)buffer.Length };
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_vara_double(groupId, variableId, start, count, buffer), "Read GOCI-II navigation row");
        }

        private static double ReadCell(int groupId, int variableId, int row, int column)
        {
            var index = new[] { (UIntPtr)(uint)row, (UIntPtr)(uint)column };
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var1_double(groupId, variableId, index, out var value), "Read GOCI-II NetCDF cell");
            return value;
        }

        private static double? TransformTss(FileContext context, double raw)
        {
            if (double.IsNaN(raw) || double.IsInfinity(raw)) return null;
            if (context.FillValue.HasValue && NearlyEqual(raw, context.FillValue.Value)) return null;
            if (raw < context.ValidMin || raw > context.ValidMax) return null;
            var value = (raw * context.ScaleFactor) + context.AddOffset;
            return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
        }

        private static bool NearlyEqual(double left, double right)
        {
            var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
            return Math.Abs(left - right) <= 1e-10 * scale;
        }

        private static double? TryReadAttribute(int groupId, int variableId, string name)
        {
            var code = NetCdfNative.nc_get_att_double(groupId, variableId, name, out var value);
            return code == NetCdfNative.NoError ? value : (double?)null;
        }

        private static bool IsValidCoordinate(double latitude, double longitude)
        {
            return !double.IsNaN(latitude) && !double.IsInfinity(latitude) &&
                   !double.IsNaN(longitude) && !double.IsInfinity(longitude) &&
                   latitude >= -90.0 && latitude <= 90.0 && longitude >= -180.0 && longitude <= 180.0;
        }

        private static double GeographicDistanceSquaredKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double kmPerDegree = 111.32;
            var meanLatRadians = ((lat1 + lat2) * 0.5) * Math.PI / 180.0;
            var dy = (lat1 - lat2) * kmPerDegree;
            var dx = (lon1 - lon2) * kmPerDegree * Math.Cos(meanLatRadians);
            return (dx * dx) + (dy * dy);
        }

        private static double[] BuildDescendingAxis(double max, double min, int count)
        {
            var result = new double[count];
            for (var i = 0; i < count; i++) result[i] = max + ((min - max) * (i / (double)(count - 1)));
            return result;
        }

        private static double[] BuildAscendingAxis(double min, double max, int count)
        {
            var result = new double[count];
            for (var i = 0; i < count; i++) result[i] = min + ((max - min) * (i / (double)(count - 1)));
            return result;
        }

        private static int NearestOutputIndexDescending(double[] axis, double value)
        {
            var t = (axis[0] - value) / (axis[0] - axis[axis.Length - 1]);
            return ClampIndex((int)Math.Round(t * (axis.Length - 1)), axis.Length);
        }

        private static int NearestOutputIndexAscending(double[] axis, double value)
        {
            var t = (value - axis[0]) / (axis[axis.Length - 1] - axis[0]);
            return ClampIndex((int)Math.Round(t * (axis.Length - 1)), axis.Length);
        }

        private static int ClampIndex(int value, int length)
        {
            if (value < 0) return 0;
            if (value >= length) return length - 1;
            return value;
        }

        private string GetTssVariableName() => string.IsNullOrWhiteSpace(_option.Variable) ? DefaultTssVariable : _option.Variable;

        private Dictionary<string, object?> CreateMetadata(string filePath, DateTime? observationUtc)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();
            metadata["file"] = filePath;
            metadata["sensor"] = "GOCI-II";
            metadata["processingLevel"] = "L2";
            metadata["observationMode"] = "LA";
            metadata["productLayout"] = IsMosaicFile(filePath) ? "Mosaic" : "Slot/other";
            metadata["parameter"] = "Total Suspended Solids concentration";
            metadata["parameterNote"] = "Raw source TSS concentration; the public Goci2Tss source aggregates valid observations and exposes turbidity only as a derived result.";
            metadata["nominalSpatialResolution"] = "250 m";
            metadata["curvilinearGeolocation"] = true;
            metadata["navigationVariables"] = $"/{NavigationGroupName}/{_option.LatitudeVariable}, /{NavigationGroupName}/{_option.LongitudeVariable}";
            metadata["tssVariable"] = $"/{GeophysicalGroupName}/{GetTssVariableName()}";
            if (observationUtc.HasValue) metadata["observationUtc"] = observationUtc.Value;
            return metadata;
        }

        private string? ResolveValidationFile()
        {
            if (File.Exists(_resolvedPath)) return _resolvedPath;
            if (!Directory.Exists(_resolvedPath)) return null;
            return EnumerateMosaicFiles(_resolvedPath).OrderByDescending(x => x.ObservationUtc).Select(x => x.Path).FirstOrDefault();
        }

        private string? ResolveFile(DateTime? requestedDateTime)
        {
            if (File.Exists(_resolvedPath)) return _resolvedPath;
            if (!Directory.Exists(_resolvedPath)) return null;
            var files = EnumerateMosaicFiles(_resolvedPath).ToArray();
            if (files.Length == 0) return null;
            if (!requestedDateTime.HasValue) return files.OrderByDescending(x => x.ObservationUtc).First().Path;
            var requestedUtc = requestedDateTime.Value.Kind == DateTimeKind.Local
                ? requestedDateTime.Value.ToUniversalTime()
                : DateTime.SpecifyKind(requestedDateTime.Value, DateTimeKind.Utc);
            return files.OrderBy(x => Math.Abs((x.ObservationUtc - requestedUtc).Ticks)).First().Path;
        }

        private static IEnumerable<MosaicFile> EnumerateMosaicFiles(string directory)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.nc", SearchOption.TopDirectoryOnly))
            {
                var observationUtc = ParseObservationUtc(path);
                if (observationUtc.HasValue && IsMosaicFile(path)) yield return new MosaicFile(path, observationUtc.Value);
            }
        }

        private static bool IsMosaicFile(string path) => MosaicFileRegex.IsMatch(Path.GetFileName(path));

        private static DateTime? ParseObservationUtc(string path)
        {
            var match = MosaicFileRegex.Match(Path.GetFileName(path));
            if (!match.Success) return null;
            DateTime value;
            if (DateTime.TryParseExact(match.Groups["date"].Value + match.Groups["time"].Value,
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)) return value;
            return null;
        }

        private static NetCdfFile Open(string filePath)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_open(filePath, NetCdfNative.Nowrite, out var id), $"Open NetCDF '{filePath}'");
            return new NetCdfFile(id);
        }

        public void Dispose()
        {
            lock (_indexSync) { _geoIndex = null; _geoIndexFile = null; }
        }

        private sealed class NetCdfFile : IDisposable
        {
            public NetCdfFile(int id) { Id = id; }
            public int Id { get; }
            public void Dispose() { NetCdfNative.nc_close(Id); }
        }

        private sealed class FileContext : IDisposable
        {
            public FileContext(int navigationGroupId, int geophysicalGroupId, int latitudeVariableId, int longitudeVariableId,
                int tssVariableId, int? flagVariableId, int rows, int columns, double? fillValue,
                double scaleFactor, double addOffset, double validMin, double validMax)
            {
                NavigationGroupId = navigationGroupId; GeophysicalGroupId = geophysicalGroupId;
                LatitudeVariableId = latitudeVariableId; LongitudeVariableId = longitudeVariableId;
                TssVariableId = tssVariableId; FlagVariableId = flagVariableId; Rows = rows; Columns = columns;
                FillValue = fillValue; ScaleFactor = scaleFactor; AddOffset = addOffset; ValidMin = validMin; ValidMax = validMax;
            }
            public int NavigationGroupId { get; }
            public int GeophysicalGroupId { get; }
            public int LatitudeVariableId { get; }
            public int LongitudeVariableId { get; }
            public int TssVariableId { get; }
            public int? FlagVariableId { get; }
            public int Rows { get; }
            public int Columns { get; }
            public double? FillValue { get; }
            public double ScaleFactor { get; }
            public double AddOffset { get; }
            public double ValidMin { get; }
            public double ValidMax { get; }
            public void Dispose() { }
        }

        private sealed class GeoIndex
        {
            public GeoIndex(int rows, int columns, List<GeoSample> samples) { Rows = rows; Columns = columns; Samples = samples; }
            public int Rows { get; }
            public int Columns { get; }
            public List<GeoSample> Samples { get; }
        }

        private readonly struct GeoSample
        {
            public GeoSample(double latitude, double longitude, int row, int column)
            { Latitude = latitude; Longitude = longitude; Row = row; Column = column; }
            public double Latitude { get; }
            public double Longitude { get; }
            public int Row { get; }
            public int Column { get; }
        }

        private readonly struct PixelMatch
        {
            public PixelMatch(int row, int column, double latitude, double longitude, double distanceKm)
            { Row = row; Column = column; Latitude = latitude; Longitude = longitude; DistanceKm = distanceKm; }
            public int Row { get; }
            public int Column { get; }
            public double Latitude { get; }
            public double Longitude { get; }
            public double DistanceKm { get; }
        }

        private readonly struct Shape
        {
            public Shape(int rows, int columns) { Rows = rows; Columns = columns; }
            public int Rows { get; }
            public int Columns { get; }
        }

        private readonly struct SourceWindow
        {
            public SourceWindow(int rowStart, int rowEnd, int columnStart, int columnEnd)
            { RowStart = rowStart; RowEnd = rowEnd; ColumnStart = columnStart; ColumnEnd = columnEnd; }
            public int RowStart { get; }
            public int RowEnd { get; }
            public int ColumnStart { get; }
            public int ColumnEnd { get; }
        }

        private sealed class GridProjection
        {
            public GridProjection(
                int sourceRows,
                int sourceColumns,
                int outputWidth,
                int outputHeight,
                int candidateCount,
                ProjectionCandidate[] candidates,
                byte[] counts)
            {
                SourceRows = sourceRows;
                SourceColumns = sourceColumns;
                OutputWidth = outputWidth;
                OutputHeight = outputHeight;
                CandidateCount = candidateCount;
                Candidates = candidates;
                Counts = counts;
            }

            public int SourceRows { get; }
            public int SourceColumns { get; }
            public int OutputWidth { get; }
            public int OutputHeight { get; }
            public int CandidateCount { get; }
            public ProjectionCandidate[] Candidates { get; }
            public byte[] Counts { get; }
        }

        private sealed class ProjectionRowPlan
        {
            public ProjectionRowPlan(int minColumn, int maxColumn)
            {
                MinColumn = minColumn;
                MaxColumn = maxColumn;
            }

            public int MinColumn { get; set; }
            public int MaxColumn { get; set; }
            public List<int> CandidateSlots { get; } = new List<int>();
        }

        private readonly struct ProjectionCandidate
        {
            public ProjectionCandidate(int row, int column, double distance2)
            {
                Row = row;
                Column = column;
                Distance2 = distance2;
            }

            public int Row { get; }
            public int Column { get; }
            public double Distance2 { get; }
        }

        private readonly struct MosaicFile
        {
            public MosaicFile(string path, DateTime observationUtc) { Path = path; ObservationUtc = observationUtc; }
            public string Path { get; }
            public DateTime ObservationUtc { get; }
        }
    }
}
