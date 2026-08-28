using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Native;

namespace MarineEnvironment.Sources.NetCdf
{
    internal sealed class NetCdfDataSource : IEnvironmentDataSource
    {
        private const long MaxSourceNativeCells = 12_000_000;
        private readonly DataSourceOption _option;
        private readonly string _resolvedPath;

        public NetCdfDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option;
            _resolvedPath = resolvedPath;
            if (!option.Enabled) { Status = SourceStatus.Disabled; return; }
            try
            {
                var validationFile = ResolveFile(DateTime.Now);
                if (!File.Exists(validationFile)) { Status = SourceStatus.FileNotFound; StatusMessage = validationFile; return; }
                using var file = Open(validationFile);
                ValidateVariable(file.Id, _option.Variable);
                ValidateVariable(file.Id, _option.LatitudeVariable);
                ValidateVariable(file.Id, _option.LongitudeVariable);
                if (!string.IsNullOrWhiteSpace(_option.DepthVariable)) ValidateVariable(file.Id, _option.DepthVariable!);
                Status = SourceStatus.Ready;
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
            var filePath = ResolveFile(query.DateTime ?? DateTime.Now);
            if (!File.Exists(filePath)) throw new FileNotFoundException($"NetCDF source file for '{Id}' was not found.", filePath);

            using var file = Open(filePath);
            var context = BuildReadContext(file.Id);
            var normalizedLon = NormalizeLongitude(query.Longitude, context.LongitudeAxis);

            // Nearest-neighbour sampling must not clamp requests outside the dataset extent
            // to the first/last cell. Outside coverage is NoData.
            if (!IsWithinAxis(context.LatitudeAxis.Values, query.Latitude) ||
                !IsWithinAxis(context.LongitudeAxis.Values, normalizedLon))
                return null;

            var latIndex = FindNearestIndex(context.LatitudeAxis.Values, query.Latitude);
            var lonIndex = FindNearestIndex(context.LongitudeAxis.Values, normalizedLon);
            int? depthIndex = context.DepthAxis.HasValue
                ? FindNearestIndex(context.DepthAxis.Value.Values, query.Depth ?? context.DepthAxis.Value.Values[0])
                : (int?)null;

            var value = ReadValue(file.Id, context, latIndex, lonIndex, depthIndex);
            if (!value.HasValue) return null;

            var metadata = CreateMetadata(filePath, query.Sampling.ToString());
            AddSourceResolutionMetadata(metadata, context.LatitudeAxis, context.LongitudeAxis);
            return new EnvironmentValue(Id, Type, value.Value, _option.Unit,
                context.LatitudeAxis.Values[latIndex], context.LongitudeAxis.Values[lonIndex],
                context.DepthAxis.HasValue && depthIndex.HasValue ? context.DepthAxis.Value.Values[depthIndex.Value] : (double?)null,
                query.DateTime, _option.Variable, metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready) throw new InvalidOperationException($"Source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.MinLatitude >= query.MaxLatitude) throw new ArgumentException("MinLatitude must be less than MaxLatitude.", nameof(query));
            if (query.MinLongitude >= query.MaxLongitude) throw new ArgumentException("MinLongitude must be less than MaxLongitude.", nameof(query));
            if (query.ResolutionMode == GridResolutionMode.Custom)
            {
                if (query.Width < 2 || query.Width > 2048) throw new ArgumentOutOfRangeException(nameof(query.Width), "Custom grid width must be between 2 and 2048.");
                if (query.Height < 2 || query.Height > 2048) throw new ArgumentOutOfRangeException(nameof(query.Height), "Custom grid height must be between 2 and 2048.");
            }

            var filePath = ResolveFile(query.DateTime ?? DateTime.Now);
            if (!File.Exists(filePath)) throw new FileNotFoundException($"NetCDF source file for '{Id}' was not found.", filePath);

            using var file = Open(filePath);
            var context = BuildReadContext(file.Id);
            int? depthIndex = context.DepthAxis.HasValue
                ? FindNearestIndex(context.DepthAxis.Value.Values, query.Depth ?? context.DepthAxis.Value.Values[0])
                : (int?)null;

            var geometry = query.ResolutionMode == GridResolutionMode.SourceNative
                ? BuildSourceNativeGeometry(query, context.LatitudeAxis, context.LongitudeAxis)
                : BuildCustomGeometry(query, context.LatitudeAxis, context.LongitudeAxis);

            if (query.ResolutionMode == GridResolutionMode.SourceNative &&
                (long)geometry.Width * geometry.Height > MaxSourceNativeCells)
            {
                throw new InvalidOperationException(
                    $"Source-native selection is {geometry.Width:N0} x {geometry.Height:N0} ({(long)geometry.Width * geometry.Height:N0} cells), " +
                    $"which exceeds the {MaxSourceNativeCells:N0}-cell viewer safety limit. Reduce the view bounds or use Custom resolution.");
            }

            var values = new double?[geometry.Width * geometry.Height];
            double? minimum = null, maximum = null;

            var validLatIndices = geometry.LatitudeIndices.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            var validLonIndices = geometry.LongitudeIndices.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            int? latStart = null, latEnd = null, lonStart = null, lonEnd = null;

            if (validLatIndices.Length > 0 && validLonIndices.Length > 0)
            {
                latStart = validLatIndices.Min();
                latEnd = validLatIndices.Max();
                lonStart = validLonIndices.Min();
                lonEnd = validLonIndices.Max();

                var slab = ReadSlab(
                    file.Id,
                    context,
                    latStart.Value,
                    latEnd.Value - latStart.Value + 1,
                    lonStart.Value,
                    lonEnd.Value - lonStart.Value + 1,
                    depthIndex);

                for (var row = 0; row < geometry.Height; row++)
                {
                    if (!geometry.LatitudeIndices[row].HasValue) continue;
                    var localLat = geometry.LatitudeIndices[row]!.Value - latStart.Value;

                    for (var column = 0; column < geometry.Width; column++)
                    {
                        if (!geometry.LongitudeIndices[column].HasValue) continue;
                        var localLon = geometry.LongitudeIndices[column]!.Value - lonStart.Value;
                        var value = slab.Get(localLat, localLon);
                        var outputIndex = (row * geometry.Width) + column;
                        values[outputIndex] = value;
                        if (!value.HasValue) continue;
                        minimum = !minimum.HasValue ? value : Math.Min(minimum.Value, value.Value);
                        maximum = !maximum.HasValue ? value : Math.Max(maximum.Value, value.Value);
                    }
                }
            }

            var metadata = CreateMetadata(filePath, query.Sampling.ToString());
            metadata["requestedBounds"] = new[] { query.MinLatitude, query.MaxLatitude, query.MinLongitude, query.MaxLongitude };
            metadata["sourceBounds"] = new[] {
                context.LatitudeAxis.Values.Min(), context.LatitudeAxis.Values.Max(),
                context.LongitudeAxis.Values.Min(), context.LongitudeAxis.Values.Max()
            };
            metadata["resolutionMode"] = query.ResolutionMode.ToString();
            metadata["sourceNativeRaster"] = true;
            metadata["renderGrid"] = new[] { geometry.Width, geometry.Height };
            if (latStart.HasValue && latEnd.HasValue && lonStart.HasValue && lonEnd.HasValue)
                metadata["sourceSlab"] = new[] { latStart.Value, latEnd.Value, lonStart.Value, lonEnd.Value };
            AddSourceResolutionMetadata(metadata, context.LatitudeAxis, context.LongitudeAxis);

            return new GridResult
            {
                SourceId = Id, Type = Type, Width = geometry.Width, Height = geometry.Height,
                Latitudes = geometry.Latitudes, Longitudes = geometry.Longitudes, Values = values,
                Unit = _option.Unit,
                Depth = context.DepthAxis.HasValue && depthIndex.HasValue ? context.DepthAxis.Value.Values[depthIndex.Value] : (double?)null,
                DateTime = query.DateTime, Variable = _option.Variable,
                Minimum = minimum, Maximum = maximum, Metadata = metadata
            };
        }

        private static GridGeometry BuildCustomGeometry(GridQuery query, Axis latitudeAxis, Axis longitudeAxis)
        {
            var outputLatitudes = new double[query.Height];
            var outputLongitudes = new double[query.Width];
            var latIndices = new int?[query.Height];
            var lonIndices = new int?[query.Width];

            for (var row = 0; row < query.Height; row++)
            {
                var t = row / (double)(query.Height - 1);
                var requested = query.MaxLatitude + ((query.MinLatitude - query.MaxLatitude) * t);
                outputLatitudes[row] = requested;
                if (IsWithinAxis(latitudeAxis.Values, requested))
                    latIndices[row] = FindNearestIndex(latitudeAxis.Values, requested);
            }

            for (var column = 0; column < query.Width; column++)
            {
                var t = column / (double)(query.Width - 1);
                var requested = query.MinLongitude + ((query.MaxLongitude - query.MinLongitude) * t);
                outputLongitudes[column] = requested;
                var normalized = NormalizeLongitude(requested, longitudeAxis);
                if (IsWithinAxis(longitudeAxis.Values, normalized))
                    lonIndices[column] = FindNearestIndex(longitudeAxis.Values, normalized);
            }

            return new GridGeometry(outputLatitudes, outputLongitudes, latIndices, lonIndices);
        }

        private static GridGeometry BuildSourceNativeGeometry(GridQuery query, Axis latitudeAxis, Axis longitudeAxis)
        {
            var latIndicesRaw = SelectNativeIndices(latitudeAxis.Values, query.MinLatitude, query.MaxLatitude, descending: true);
            var normalizedMinLon = NormalizeLongitude(query.MinLongitude, longitudeAxis);
            var normalizedMaxLon = NormalizeLongitude(query.MaxLongitude, longitudeAxis);
            if (normalizedMinLon > normalizedMaxLon)
                throw new NotSupportedException("Source-native rendering across a longitude wrap/dateline is not supported yet. Use Custom resolution for this view.");

            var lonIndicesRaw = SelectNativeIndices(longitudeAxis.Values, normalizedMinLon, normalizedMaxLon, descending: false);
            var latitudes = latIndicesRaw.Select(i => latitudeAxis.Values[i]).ToArray();
            var longitudes = lonIndicesRaw.Select(i => ToRequestedLongitudeConvention(longitudeAxis.Values[i], query.MinLongitude, query.MaxLongitude)).ToArray();
            var latIndices = latIndicesRaw.Select(i => (int?)i).ToArray();
            var lonIndices = lonIndicesRaw.Select(i => (int?)i).ToArray();
            return new GridGeometry(latitudes, longitudes, latIndices, lonIndices);
        }

        private static int[] SelectNativeIndices(double[] values, double min, double max, bool descending)
        {
            var lower = Math.Min(min, max);
            var upper = Math.Max(min, max);
            var selected = Enumerable.Range(0, values.Length)
                .Where(i => values[i] >= lower && values[i] <= upper);
            var ordered = descending
                ? selected.OrderByDescending(i => values[i]).ToArray()
                : selected.OrderBy(i => values[i]).ToArray();

            if (ordered.Length > 0)
                return ordered;

            var axisMin = values.Min();
            var axisMax = values.Max();
            if (upper < axisMin || lower > axisMax)
                return Array.Empty<int>();

            var nearest = FindNearestIndex(values, (lower + upper) / 2.0);
            return new[] { nearest };
        }

        private Dictionary<string, object?> CreateMetadata(string filePath, string sampling)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();
            metadata["file"] = filePath;
            metadata["sampling"] = sampling;
            return metadata;
        }

        private static void AddSourceResolutionMetadata(Dictionary<string, object?> metadata, Axis latitudeAxis, Axis longitudeAxis)
        {
            var latSpacing = GetAxisSpacingDegrees(latitudeAxis.Values);
            var lonSpacing = GetAxisSpacingDegrees(longitudeAxis.Values);
            metadata["sourceLatitudeCount"] = latitudeAxis.Values.Length;
            metadata["sourceLongitudeCount"] = longitudeAxis.Values.Length;
            metadata["sourceLatitudeSpacingDegrees"] = latSpacing;
            metadata["sourceLongitudeSpacingDegrees"] = lonSpacing;
            if (latSpacing.HasValue) metadata["sourceLatitudeSpacingArcSeconds"] = latSpacing.Value * 3600.0;
            if (lonSpacing.HasValue) metadata["sourceLongitudeSpacingArcSeconds"] = lonSpacing.Value * 3600.0;
        }

        private ReadContext BuildReadContext(int ncid)
        {
            var latAxis = ReadAxis(ncid, _option.LatitudeVariable);
            var lonAxis = ReadAxis(ncid, _option.LongitudeVariable);
            Axis? depthAxis = string.IsNullOrWhiteSpace(_option.DepthVariable) ? null : ReadAxis(ncid, _option.DepthVariable!);
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, _option.Variable, out var dataVarId), $"Find variable '{_option.Variable}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, dataVarId, out var ndims), $"Read dimensions for '{_option.Variable}'");
            var dimIds = new int[ndims];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(ncid, dataVarId, dimIds), $"Read dimension ids for '{_option.Variable}'");
            return new ReadContext(dataVarId, dimIds, latAxis, lonAxis, depthAxis);
        }

        private static double? ReadValue(int ncid, ReadContext context, int latIndex, int lonIndex, int? depthIndex)
        {
            var indices = new UIntPtr[context.DimensionIds.Length];
            SetAxisIndex(context.DimensionIds, indices, context.LatitudeAxis.DimensionId, latIndex);
            SetAxisIndex(context.DimensionIds, indices, context.LongitudeAxis.DimensionId, lonIndex);
            if (context.DepthAxis.HasValue && depthIndex.HasValue) SetAxisIndex(context.DimensionIds, indices, context.DepthAxis.Value.DimensionId, depthIndex.Value);
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var1_double(ncid, context.DataVariableId, indices, out var raw), "Read NetCDF value");
            return TransformRawValue(ncid, context.DataVariableId, raw);
        }

        private static DataSlab ReadSlab(int ncid, ReadContext context, int latStart, int latCount, int lonStart, int lonCount, int? depthIndex)
        {
            var start = new UIntPtr[context.DimensionIds.Length];
            var count = new UIntPtr[context.DimensionIds.Length];
            var counts = new int[context.DimensionIds.Length];
            for (var i = 0; i < count.Length; i++)
            {
                count[i] = (UIntPtr)1u;
                counts[i] = 1;
            }

            SetAxisRange(context.DimensionIds, start, count, counts, context.LatitudeAxis.DimensionId, latStart, latCount);
            SetAxisRange(context.DimensionIds, start, count, counts, context.LongitudeAxis.DimensionId, lonStart, lonCount);
            if (context.DepthAxis.HasValue && depthIndex.HasValue)
                SetAxisRange(context.DimensionIds, start, count, counts, context.DepthAxis.Value.DimensionId, depthIndex.Value, 1);

            var total = 1L;
            for (var i = 0; i < counts.Length; i++) total *= counts[i];
            var raw = new double[checked((int)total)];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_vara_double(ncid, context.DataVariableId, start, count, raw), "Read NetCDF slab");

            var hasFill = TryGetAttribute(ncid, context.DataVariableId, "_FillValue", out var fill);
            var hasMissing = TryGetAttribute(ncid, context.DataVariableId, "missing_value", out var missing);
            var hasScale = TryGetAttribute(ncid, context.DataVariableId, "scale_factor", out var scale);
            var hasOffset = TryGetAttribute(ncid, context.DataVariableId, "add_offset", out var offset);

            for (var i = 0; i < raw.Length; i++)
            {
                var value = raw[i];
                if (double.IsNaN(value) || double.IsInfinity(value) ||
                    (hasFill && NearlyEqual(value, fill)) ||
                    (hasMissing && NearlyEqual(value, missing)))
                {
                    raw[i] = double.NaN;
                    continue;
                }
                if (hasScale) value *= scale;
                if (hasOffset) value += offset;
                raw[i] = value;
            }

            return new DataSlab(
                raw,
                counts,
                Array.IndexOf(context.DimensionIds, context.LatitudeAxis.DimensionId),
                Array.IndexOf(context.DimensionIds, context.LongitudeAxis.DimensionId));
        }

        private static double? TransformRawValue(int ncid, int varId, double raw)
        {
            // NetCDF4/GMT grids may use NaN directly as the fill value (Martin et al. 2015
            // porosity is one example). Treat all non-finite raw samples as NoData before
            // attempting numeric fill-value comparisons.
            if (double.IsNaN(raw) || double.IsInfinity(raw)) return null;
            if (TryGetAttribute(ncid, varId, "_FillValue", out var fill) && NearlyEqual(raw, fill)) return null;
            if (TryGetAttribute(ncid, varId, "missing_value", out var missing) && NearlyEqual(raw, missing)) return null;
            var value = raw;
            if (TryGetAttribute(ncid, varId, "scale_factor", out var scale)) value *= scale;
            if (TryGetAttribute(ncid, varId, "add_offset", out var offset)) value += offset;
            return value;
        }

        private string ResolveFile(DateTime time)
        {
            if (string.IsNullOrWhiteSpace(_option.FilePattern)) return _resolvedPath;
            return Path.Combine(_resolvedPath, _option.FilePattern.Replace("{MM}", time.Month.ToString("00")));
        }

        private static Axis ReadAxis(int ncid, string variableName)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variableName, out var varId), $"Find axis '{variableName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, varId, out var ndims), $"Read axis dimensions '{variableName}'");
            if (ndims != 1) throw new InvalidDataException($"Axis '{variableName}' must be one-dimensional in the generic NetCDF reader.");
            var dimIds = new int[1];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(ncid, varId, dimIds), $"Read axis dimension '{variableName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_dimlen(ncid, dimIds[0], out var length), $"Read axis length '{variableName}'");
            var values = new double[checked((int)length.ToUInt64())];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var_double(ncid, varId, values), $"Read axis '{variableName}'");
            return new Axis(dimIds[0], values);
        }

        private static bool IsWithinAxis(double[] values, double target)
        {
            if (values.Length == 0) return false;
            var min = Math.Min(values[0], values[values.Length - 1]);
            var max = Math.Max(values[0], values[values.Length - 1]);
            return target >= min && target <= max;
        }

        private static int FindNearestIndex(double[] values, double target)
        {
            if (values.Length == 0) throw new InvalidDataException("Coordinate axis is empty.");
            if (values.Length == 1) return 0;
            var ascending = values[values.Length - 1] >= values[0];
            var lo = 0; var hi = values.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2); var current = values[mid];
                if (current == target) return mid;
                if ((ascending && current < target) || (!ascending && current > target)) lo = mid + 1; else hi = mid - 1;
            }
            if (lo <= 0) return 0;
            if (lo >= values.Length) return values.Length - 1;
            return Math.Abs(values[lo] - target) < Math.Abs(values[lo - 1] - target) ? lo : lo - 1;
        }

        private static double NormalizeLongitude(double longitude, Axis axis)
        {
            var min = axis.Values.Min(); var max = axis.Values.Max();
            if (min >= 0 && max > 180 && longitude < 0) return longitude + 360;
            if (min < 0 && max <= 180 && longitude > 180) return longitude - 360;
            return longitude;
        }

        private static double ToRequestedLongitudeConvention(double sourceLongitude, double requestedMin, double requestedMax)
        {
            if (requestedMin < 0 && requestedMax <= 180 && sourceLongitude > 180) return sourceLongitude - 360;
            if (requestedMin >= 0 && requestedMax > 180 && sourceLongitude < 0) return sourceLongitude + 360;
            return sourceLongitude;
        }

        private static double? GetAxisSpacingDegrees(double[] values)
        {
            if (values.Length < 2) return null;
            for (var i = 1; i < values.Length; i++)
            {
                var spacing = Math.Abs(values[i] - values[i - 1]);
                if (spacing > 0 && !double.IsNaN(spacing) && !double.IsInfinity(spacing)) return spacing;
            }
            return null;
        }

        private static void SetAxisIndex(int[] dims, UIntPtr[] indices, int axisDim, int axisIndex)
        {
            var position = Array.IndexOf(dims, axisDim); if (position >= 0) indices[position] = (UIntPtr)(uint)axisIndex;
        }

        private static void SetAxisRange(int[] dims, UIntPtr[] start, UIntPtr[] count, int[] counts, int axisDim, int axisStart, int axisCount)
        {
            var position = Array.IndexOf(dims, axisDim);
            if (position < 0) return;
            start[position] = (UIntPtr)(uint)axisStart;
            count[position] = (UIntPtr)(uint)axisCount;
            counts[position] = axisCount;
        }

        private static bool TryGetAttribute(int ncid, int varId, string name, out double value) => NetCdfNative.nc_get_att_double(ncid, varId, name, out value) == NetCdfNative.NoError;
        private static bool NearlyEqual(double a, double b) => a.Equals(b) || Math.Abs(a - b) <= Math.Max(1e-12, Math.Abs(b) * 1e-12);
        private static void ValidateVariable(int ncid, string variable) => NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variable, out _), $"Find variable '{variable}'");
        private static NetCdfFile Open(string path) { NetCdfNative.ThrowIfError(NetCdfNative.nc_open(path, NetCdfNative.Nowrite, out var ncid), $"Open NetCDF '{path}'"); return new NetCdfFile(ncid); }
        public void Dispose() { }

        private readonly struct Axis
        {
            public Axis(int dimensionId, double[] values) { DimensionId = dimensionId; Values = values; }
            public int DimensionId { get; }
            public double[] Values { get; }
        }

        private readonly struct ReadContext
        {
            public ReadContext(int dataVariableId, int[] dimensionIds, Axis latitudeAxis, Axis longitudeAxis, Axis? depthAxis)
            { DataVariableId = dataVariableId; DimensionIds = dimensionIds; LatitudeAxis = latitudeAxis; LongitudeAxis = longitudeAxis; DepthAxis = depthAxis; }
            public int DataVariableId { get; }
            public int[] DimensionIds { get; }
            public Axis LatitudeAxis { get; }
            public Axis LongitudeAxis { get; }
            public Axis? DepthAxis { get; }
        }

        private readonly struct GridGeometry
        {
            public GridGeometry(double[] latitudes, double[] longitudes, int?[] latitudeIndices, int?[] longitudeIndices)
            {
                Latitudes = latitudes;
                Longitudes = longitudes;
                LatitudeIndices = latitudeIndices;
                LongitudeIndices = longitudeIndices;
            }
            public int Width => Longitudes.Length;
            public int Height => Latitudes.Length;
            public double[] Latitudes { get; }
            public double[] Longitudes { get; }
            public int?[] LatitudeIndices { get; }
            public int?[] LongitudeIndices { get; }
        }

        private sealed class DataSlab
        {
            private readonly double[] _values;
            private readonly int _latitudeStride;
            private readonly int _longitudeStride;

            public DataSlab(double[] values, int[] counts, int latitudePosition, int longitudePosition)
            {
                if (latitudePosition < 0 || longitudePosition < 0)
                    throw new InvalidDataException("NetCDF data variable does not contain the configured latitude/longitude dimensions.");
                _values = values;
                _latitudeStride = CalculateStride(counts, latitudePosition);
                _longitudeStride = CalculateStride(counts, longitudePosition);
            }

            public double? Get(int localLatitude, int localLongitude)
            {
                var value = _values[(localLatitude * _latitudeStride) + (localLongitude * _longitudeStride)];
                return double.IsNaN(value) || double.IsInfinity(value) ? (double?)null : value;
            }

            private static int CalculateStride(int[] counts, int position)
            {
                var stride = 1;
                for (var i = position + 1; i < counts.Length; i++) stride = checked(stride * counts[i]);
                return stride;
            }
        }

        private sealed class NetCdfFile : IDisposable
        {
            public NetCdfFile(int id) { Id = id; }
            public int Id { get; }
            public void Dispose() { NetCdfNative.nc_close(Id); }
        }
    }
}
