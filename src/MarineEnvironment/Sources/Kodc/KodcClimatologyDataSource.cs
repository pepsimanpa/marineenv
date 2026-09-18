using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Native;

namespace MarineEnvironment.Sources.Kodc
{
    /// <summary>
    /// Reader for KODC/NIFS 10-day climatology grids.
    ///
    /// The official product is defined as 37 climatological time points per year. Some distributed
    /// files contain a longer time dimension (for example 1110 = 37 x 30) while the coordinate
    /// variable itself is invalid. In that case only the first 37-value climatology cycle is used;
    /// the requested calendar year is intentionally ignored and month/day selects the climatology slot.
    /// </summary>
    internal sealed class KodcClimatologyDataSource : IEnvironmentDataSource
    {
        private const int ClimatologySlotCount = 37;
        private const long MaxSourceNativeCells = 12_000_000;

        private readonly DataSourceOption _option;
        private readonly string _resolvedPath;

        public KodcClimatologyDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option;
            _resolvedPath = resolvedPath;

            if (!option.Enabled)
            {
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                if (!File.Exists(_resolvedPath))
                {
                    Status = SourceStatus.FileNotFound;
                    StatusMessage = _resolvedPath;
                    return;
                }

                using var file = Open(_resolvedPath);
                var context = BuildReadContext(file.Id);
                ValidateContext(context);

                Status = SourceStatus.Ready;
                StatusMessage =
                    $"KODC 10-day climatology | {ClimatologySlotCount} slots/year | " +
                    $"time dimension {context.TimeLength:N0} = {context.TimeLength / ClimatologySlotCount:N0} cycle(s)";
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
        public EnvironmentType Type => _option.Type;
        public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
        public string? StatusMessage { get; private set; }

        public EnvironmentValue? Query(EnvironmentQuery query)
        {
            if (Status != SourceStatus.Ready)
                return null;

            using var file = Open(_resolvedPath);
            var context = BuildReadContext(file.Id);
            var normalizedLongitude = NormalizeLongitude(query.Longitude, context.LongitudeAxis);

            if (!IsWithinAxis(context.LatitudeAxis.Values, query.Latitude) ||
                !IsWithinAxis(context.LongitudeAxis.Values, normalizedLongitude))
            {
                return null;
            }

            if (query.Depth.HasValue &&
                !IsWithinAxis(context.DepthAxis.Values, query.Depth.Value))
            {
                return null;
            }

            var latitudeIndex = FindNearestIndex(context.LatitudeAxis.Values, query.Latitude);
            var longitudeIndex = FindNearestIndex(context.LongitudeAxis.Values, normalizedLongitude);
            var requestedDepth = query.Depth ?? context.DepthAxis.Values[0];
            var depthIndex = FindNearestIndex(context.DepthAxis.Values, requestedDepth);
            var requestedDate = query.DateTime ?? DateTime.Now;
            var timeSlot = MapDateToClimatologySlot(requestedDate);

            var value = ReadValue(
                file.Id,
                context,
                latitudeIndex,
                longitudeIndex,
                depthIndex,
                timeSlot);

            if (!value.HasValue)
                return null;

            var metadata = CreateMetadata(
                context,
                requestedDate,
                timeSlot,
                query.Sampling.ToString());

            return new EnvironmentValue(
                Id,
                Type,
                value.Value,
                EffectiveUnit,
                context.LatitudeAxis.Values[latitudeIndex],
                context.LongitudeAxis.Values[longitudeIndex],
                context.DepthAxis.Values[depthIndex],
                query.DateTime,
                _option.Variable,
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"Source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.MinLatitude >= query.MaxLatitude)
                throw new ArgumentException("MinLatitude must be less than MaxLatitude.", nameof(query));
            if (query.MinLongitude >= query.MaxLongitude)
                throw new ArgumentException("MinLongitude must be less than MaxLongitude.", nameof(query));
            if (query.ResolutionMode == GridResolutionMode.Custom)
            {
                if (query.Width < 2 || query.Width > 2048)
                    throw new ArgumentOutOfRangeException(nameof(query.Width), "Custom grid width must be between 2 and 2048.");
                if (query.Height < 2 || query.Height > 2048)
                    throw new ArgumentOutOfRangeException(nameof(query.Height), "Custom grid height must be between 2 and 2048.");
            }

            using var file = Open(_resolvedPath);
            var context = BuildReadContext(file.Id);

            if (query.Depth.HasValue &&
                !IsWithinAxis(context.DepthAxis.Values, query.Depth.Value))
            {
                return CreateEmptyGrid(query, context);
            }

            var requestedDepth = query.Depth ?? context.DepthAxis.Values[0];
            var depthIndex = FindNearestIndex(context.DepthAxis.Values, requestedDepth);
            var requestedDate = query.DateTime ?? DateTime.Now;
            var timeSlot = MapDateToClimatologySlot(requestedDate);

            var geometry = query.ResolutionMode == GridResolutionMode.SourceNative
                ? BuildSourceNativeGeometry(query, context.LatitudeAxis, context.LongitudeAxis)
                : BuildCustomGeometry(query, context.LatitudeAxis, context.LongitudeAxis);

            if (query.ResolutionMode == GridResolutionMode.SourceNative &&
                (long)geometry.Width * geometry.Height > MaxSourceNativeCells)
            {
                throw new InvalidOperationException(
                    $"Source-native selection is {geometry.Width:N0} x {geometry.Height:N0} " +
                    $"({(long)geometry.Width * geometry.Height:N0} cells), which exceeds the " +
                    $"{MaxSourceNativeCells:N0}-cell viewer safety limit. Reduce the view bounds or use Custom resolution.");
            }

            var values = new double?[geometry.Width * geometry.Height];
            double? minimum = null;
            double? maximum = null;

            var validLatitudeIndices = geometry.LatitudeIndices.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            var validLongitudeIndices = geometry.LongitudeIndices.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            int? latitudeStart = null;
            int? latitudeEnd = null;
            int? longitudeStart = null;
            int? longitudeEnd = null;

            if (validLatitudeIndices.Length > 0 && validLongitudeIndices.Length > 0)
            {
                latitudeStart = validLatitudeIndices.Min();
                latitudeEnd = validLatitudeIndices.Max();
                longitudeStart = validLongitudeIndices.Min();
                longitudeEnd = validLongitudeIndices.Max();

                var slab = ReadSlab(
                    file.Id,
                    context,
                    latitudeStart.Value,
                    latitudeEnd.Value - latitudeStart.Value + 1,
                    longitudeStart.Value,
                    longitudeEnd.Value - longitudeStart.Value + 1,
                    depthIndex,
                    timeSlot);

                for (var row = 0; row < geometry.Height; row++)
                {
                    if (!geometry.LatitudeIndices[row].HasValue)
                        continue;

                    var localLatitude = geometry.LatitudeIndices[row]!.Value - latitudeStart.Value;
                    for (var column = 0; column < geometry.Width; column++)
                    {
                        if (!geometry.LongitudeIndices[column].HasValue)
                            continue;

                        var localLongitude = geometry.LongitudeIndices[column]!.Value - longitudeStart.Value;
                        var value = slab.Get(localLatitude, localLongitude);
                        var outputIndex = (row * geometry.Width) + column;
                        values[outputIndex] = value;

                        if (!value.HasValue)
                            continue;

                        minimum = !minimum.HasValue ? value : Math.Min(minimum.Value, value.Value);
                        maximum = !maximum.HasValue ? value : Math.Max(maximum.Value, value.Value);
                    }
                }
            }

            var metadata = CreateMetadata(
                context,
                requestedDate,
                timeSlot,
                query.Sampling.ToString());
            metadata["requestedBounds"] = new[]
            {
                query.MinLatitude,
                query.MaxLatitude,
                query.MinLongitude,
                query.MaxLongitude
            };
            metadata["sourceBounds"] = new[]
            {
                context.LatitudeAxis.Values.Min(),
                context.LatitudeAxis.Values.Max(),
                context.LongitudeAxis.Values.Min(),
                context.LongitudeAxis.Values.Max()
            };
            metadata["resolutionMode"] = query.ResolutionMode.ToString();
            metadata["sourceNativeRaster"] = true;
            metadata["renderGrid"] = new[] { geometry.Width, geometry.Height };
            if (latitudeStart.HasValue && latitudeEnd.HasValue &&
                longitudeStart.HasValue && longitudeEnd.HasValue)
            {
                metadata["sourceSlab"] = new[]
                {
                    latitudeStart.Value,
                    latitudeEnd.Value,
                    longitudeStart.Value,
                    longitudeEnd.Value,
                    depthIndex,
                    timeSlot
                };
            }

            return new GridResult
            {
                SourceId = Id,
                Type = Type,
                Width = geometry.Width,
                Height = geometry.Height,
                Latitudes = geometry.Latitudes,
                Longitudes = geometry.Longitudes,
                Values = values,
                Unit = EffectiveUnit,
                Depth = context.DepthAxis.Values[depthIndex],
                DateTime = query.DateTime,
                Variable = _option.Variable,
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        private GridResult CreateEmptyGrid(GridQuery query, ReadContext context)
        {
            var geometry = query.ResolutionMode == GridResolutionMode.SourceNative
                ? BuildSourceNativeGeometry(query, context.LatitudeAxis, context.LongitudeAxis)
                : BuildCustomGeometry(query, context.LatitudeAxis, context.LongitudeAxis);
            var requestedDate = query.DateTime ?? DateTime.Now;
            var timeSlot = MapDateToClimatologySlot(requestedDate);
            var metadata = CreateMetadata(context, requestedDate, timeSlot, query.Sampling.ToString());
            metadata["resolutionMode"] = query.ResolutionMode.ToString();
            metadata["sourceNativeRaster"] = true;
            metadata["requestedDepthOutsideCoverage"] = true;

            return new GridResult
            {
                SourceId = Id,
                Type = Type,
                Width = geometry.Width,
                Height = geometry.Height,
                Latitudes = geometry.Latitudes,
                Longitudes = geometry.Longitudes,
                Values = new double?[geometry.Width * geometry.Height],
                Unit = EffectiveUnit,
                Depth = query.Depth,
                DateTime = query.DateTime,
                Variable = _option.Variable,
                Metadata = metadata
            };
        }

        private string EffectiveUnit =>
            !string.IsNullOrWhiteSpace(_option.Unit)
                ? _option.Unit!
                : Type == EnvironmentType.Temperature
                    ? "degC"
                    : Type == EnvironmentType.Salinity
                        ? "g/kg"
                        : string.Empty;

        private Dictionary<string, object?> CreateMetadata(
            ReadContext context,
            DateTime requestedDate,
            int timeSlot,
            string sampling)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();

            var nominalDate = new DateTime(2001, 1, 1).AddDays(timeSlot * 10);

            metadata["file"] = _resolvedPath;
            metadata["sampling"] = sampling;
            metadata["sourceGeometry"] = "RegularLatLonDepthTime";
            metadata["sourceLatitudeCount"] = context.LatitudeAxis.Values.Length;
            metadata["sourceLongitudeCount"] = context.LongitudeAxis.Values.Length;
            metadata["sourceDepthCount"] = context.DepthAxis.Values.Length;
            metadata["sourceDepthRangeMeters"] = new[]
            {
                context.DepthAxis.Values.Min(),
                context.DepthAxis.Values.Max()
            };
            AddSourceResolutionMetadata(metadata, context.LatitudeAxis, context.LongitudeAxis);

            metadata["sourceTemporalResolution"] = "10-day climatology / 37 slots per year";
            metadata["climatologySlotCount"] = ClimatologySlotCount;
            metadata["climatologySlot"] = timeSlot;
            metadata["climatologyNominalDayOfYear"] = 1 + (timeSlot * 10);
            metadata["climatologyNominalMonthDay"] = nominalDate.ToString("MM-dd", CultureInfo.InvariantCulture);
            metadata["requestedCalendarDate"] = requestedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            metadata["calendarYearIgnored"] = true;
            metadata["timeCoordinateIgnored"] = true;
            metadata["timeCoordinateReason"] = "KODC climatology slot mapping; distributed file may contain invalid time coordinates.";
            metadata["sourceTimeDimensionLength"] = context.TimeLength;
            metadata["sourceTimeCycleCount"] = context.TimeLength / ClimatologySlotCount;
            metadata["sourceTimeSlotUsed"] = timeSlot;
            return metadata;
        }

        /// <summary>
        /// Maps a requested date to the nearest 10-day climatology point.
        /// The nominal points are day-of-year 1, 11, 21, ... 361.
        /// Leap day is collapsed onto the non-leap climatological calendar.
        /// </summary>
        internal static int MapDateToClimatologySlot(DateTime date)
        {
            var dayOfYear = date.DayOfYear;
            if (DateTime.IsLeapYear(date.Year))
            {
                if (date.Month == 2 && date.Day == 29)
                    dayOfYear = 59;
                else if (date.Month > 2)
                    dayOfYear--;
            }

            var slot = (int)Math.Floor(((dayOfYear - 1) + 5.0) / 10.0);
            if (slot < 0)
                return 0;
            if (slot >= ClimatologySlotCount)
                return ClimatologySlotCount - 1;
            return slot;
        }

        private ReadContext BuildReadContext(int ncid)
        {
            var latitudeAxis = ReadAxis(ncid, _option.LatitudeVariable);
            var longitudeAxis = ReadAxis(ncid, _option.LongitudeVariable);
            var depthVariable = string.IsNullOrWhiteSpace(_option.DepthVariable) ? "depth" : _option.DepthVariable!;
            var timeVariable = string.IsNullOrWhiteSpace(_option.TimeVariable) ? "time" : _option.TimeVariable!;
            var depthAxis = ReadAxis(ncid, depthVariable);
            var timeAxis = ReadAxisDescriptor(ncid, timeVariable);

            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varid(ncid, _option.Variable, out var dataVariableId),
                $"Find variable '{_option.Variable}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varndims(ncid, dataVariableId, out var dimensionCount),
                $"Read dimensions for '{_option.Variable}'");

            var dimensionIds = new int[dimensionCount];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_vardimid(ncid, dataVariableId, dimensionIds),
                $"Read dimension ids for '{_option.Variable}'");

            return new ReadContext(
                dataVariableId,
                dimensionIds,
                latitudeAxis,
                longitudeAxis,
                depthAxis,
                timeAxis.DimensionId,
                timeAxis.Length);
        }

        private void ValidateContext(ReadContext context)
        {
            if (Type != EnvironmentType.Temperature && Type != EnvironmentType.Salinity)
                throw new InvalidDataException(
                    $"KODC climatology source '{Id}' must use Temperature or Salinity type.");

            if (context.DimensionIds.Length != 4)
                throw new InvalidDataException(
                    $"KODC variable '{_option.Variable}' must have exactly four dimensions (lon, lat, depth, time).");

            var expected = new[]
            {
                context.LongitudeAxis.DimensionId,
                context.LatitudeAxis.DimensionId,
                context.DepthAxis.DimensionId,
                context.TimeDimensionId
            };

            foreach (var dimensionId in expected)
            {
                if (Array.IndexOf(context.DimensionIds, dimensionId) < 0)
                    throw new InvalidDataException(
                        $"KODC variable '{_option.Variable}' does not contain all configured lon/lat/depth/time dimensions.");
            }

            if (context.TimeLength < ClimatologySlotCount ||
                context.TimeLength % ClimatologySlotCount != 0)
            {
                throw new InvalidDataException(
                    $"KODC time dimension must contain 37 slots or a whole-number repetition of 37; actual length is {context.TimeLength}.");
            }
        }

        private static double? ReadValue(
            int ncid,
            ReadContext context,
            int latitudeIndex,
            int longitudeIndex,
            int depthIndex,
            int timeSlot)
        {
            var indices = new UIntPtr[context.DimensionIds.Length];
            SetAxisIndex(context.DimensionIds, indices, context.LatitudeAxis.DimensionId, latitudeIndex);
            SetAxisIndex(context.DimensionIds, indices, context.LongitudeAxis.DimensionId, longitudeIndex);
            SetAxisIndex(context.DimensionIds, indices, context.DepthAxis.DimensionId, depthIndex);
            SetAxisIndex(context.DimensionIds, indices, context.TimeDimensionId, timeSlot);

            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_get_var1_double(ncid, context.DataVariableId, indices, out var raw),
                "Read KODC climatology value");

            return TransformRawValue(ncid, context.DataVariableId, raw);
        }

        private static DataSlab ReadSlab(
            int ncid,
            ReadContext context,
            int latitudeStart,
            int latitudeCount,
            int longitudeStart,
            int longitudeCount,
            int depthIndex,
            int timeSlot)
        {
            var start = new UIntPtr[context.DimensionIds.Length];
            var count = new UIntPtr[context.DimensionIds.Length];
            var counts = new int[context.DimensionIds.Length];

            for (var i = 0; i < count.Length; i++)
            {
                count[i] = (UIntPtr)1u;
                counts[i] = 1;
            }

            SetAxisRange(
                context.DimensionIds,
                start,
                count,
                counts,
                context.LatitudeAxis.DimensionId,
                latitudeStart,
                latitudeCount);
            SetAxisRange(
                context.DimensionIds,
                start,
                count,
                counts,
                context.LongitudeAxis.DimensionId,
                longitudeStart,
                longitudeCount);
            SetAxisRange(
                context.DimensionIds,
                start,
                count,
                counts,
                context.DepthAxis.DimensionId,
                depthIndex,
                1);
            SetAxisRange(
                context.DimensionIds,
                start,
                count,
                counts,
                context.TimeDimensionId,
                timeSlot,
                1);

            var total = 1L;
            for (var i = 0; i < counts.Length; i++)
                total *= counts[i];

            var raw = new double[checked((int)total)];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_get_vara_double(
                    ncid,
                    context.DataVariableId,
                    start,
                    count,
                    raw),
                "Read KODC climatology slab");

            var hasFill = TryGetAttribute(ncid, context.DataVariableId, "_FillValue", out var fill);
            var hasMissing = TryGetAttribute(ncid, context.DataVariableId, "missing_value", out var missing);
            var hasScale = TryGetAttribute(ncid, context.DataVariableId, "scale_factor", out var scale);
            var hasOffset = TryGetAttribute(ncid, context.DataVariableId, "add_offset", out var offset);

            for (var i = 0; i < raw.Length; i++)
            {
                var value = raw[i];
                if (double.IsNaN(value) ||
                    double.IsInfinity(value) ||
                    (hasFill && NearlyEqual(value, fill)) ||
                    (hasMissing && NearlyEqual(value, missing)))
                {
                    raw[i] = double.NaN;
                    continue;
                }

                if (hasScale)
                    value *= scale;
                if (hasOffset)
                    value += offset;
                raw[i] = value;
            }

            return new DataSlab(
                raw,
                counts,
                Array.IndexOf(context.DimensionIds, context.LatitudeAxis.DimensionId),
                Array.IndexOf(context.DimensionIds, context.LongitudeAxis.DimensionId));
        }

        private static Axis ReadAxis(int ncid, string variableName)
        {
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varid(ncid, variableName, out var variableId),
                $"Find axis '{variableName}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varndims(ncid, variableId, out var dimensionCount),
                $"Read axis dimensions '{variableName}'");

            if (dimensionCount != 1)
                throw new InvalidDataException($"Axis '{variableName}' must be one-dimensional.");

            var dimensionIds = new int[1];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_vardimid(ncid, variableId, dimensionIds),
                $"Read axis dimension '{variableName}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_dimlen(ncid, dimensionIds[0], out var length),
                $"Read axis length '{variableName}'");

            var values = new double[checked((int)length.ToUInt64())];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_get_var_double(ncid, variableId, values),
                $"Read axis '{variableName}'");

            return new Axis(dimensionIds[0], values);
        }

        private static AxisDescriptor ReadAxisDescriptor(int ncid, string variableName)
        {
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varid(ncid, variableName, out var variableId),
                $"Find axis '{variableName}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_varndims(ncid, variableId, out var dimensionCount),
                $"Read axis dimensions '{variableName}'");

            if (dimensionCount != 1)
                throw new InvalidDataException($"Axis '{variableName}' must be one-dimensional.");

            var dimensionIds = new int[1];
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_vardimid(ncid, variableId, dimensionIds),
                $"Read axis dimension '{variableName}'");
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_inq_dimlen(ncid, dimensionIds[0], out var length),
                $"Read axis length '{variableName}'");

            var rawLength = length.ToUInt64();
            if (rawLength > int.MaxValue)
                throw new InvalidDataException($"Axis '{variableName}' is too large.");

            return new AxisDescriptor(dimensionIds[0], (int)rawLength);
        }

        private static GridGeometry BuildCustomGeometry(
            GridQuery query,
            Axis latitudeAxis,
            Axis longitudeAxis)
        {
            var outputLatitudes = new double[query.Height];
            var outputLongitudes = new double[query.Width];
            var latitudeIndices = new int?[query.Height];
            var longitudeIndices = new int?[query.Width];

            for (var row = 0; row < query.Height; row++)
            {
                var t = row / (double)(query.Height - 1);
                var requested =
                    query.MaxLatitude +
                    ((query.MinLatitude - query.MaxLatitude) * t);
                outputLatitudes[row] = requested;

                if (IsWithinAxis(latitudeAxis.Values, requested))
                    latitudeIndices[row] = FindNearestIndex(latitudeAxis.Values, requested);
            }

            for (var column = 0; column < query.Width; column++)
            {
                var t = column / (double)(query.Width - 1);
                var requested =
                    query.MinLongitude +
                    ((query.MaxLongitude - query.MinLongitude) * t);
                outputLongitudes[column] = requested;

                var normalized = NormalizeLongitude(requested, longitudeAxis);
                if (IsWithinAxis(longitudeAxis.Values, normalized))
                    longitudeIndices[column] = FindNearestIndex(longitudeAxis.Values, normalized);
            }

            return new GridGeometry(
                outputLatitudes,
                outputLongitudes,
                latitudeIndices,
                longitudeIndices);
        }

        private static GridGeometry BuildSourceNativeGeometry(
            GridQuery query,
            Axis latitudeAxis,
            Axis longitudeAxis)
        {
            var latitudeIndicesRaw = SelectNativeIndices(
                latitudeAxis.Values,
                query.MinLatitude,
                query.MaxLatitude,
                descending: true);

            var normalizedMinLongitude = NormalizeLongitude(query.MinLongitude, longitudeAxis);
            var normalizedMaxLongitude = NormalizeLongitude(query.MaxLongitude, longitudeAxis);
            if (normalizedMinLongitude > normalizedMaxLongitude)
            {
                throw new NotSupportedException(
                    "Source-native rendering across a longitude wrap/dateline is not supported yet. Use Custom resolution for this view.");
            }

            var longitudeIndicesRaw = SelectNativeIndices(
                longitudeAxis.Values,
                normalizedMinLongitude,
                normalizedMaxLongitude,
                descending: false);

            var latitudes = latitudeIndicesRaw.Select(i => latitudeAxis.Values[i]).ToArray();
            var longitudes = longitudeIndicesRaw
                .Select(i => ToRequestedLongitudeConvention(
                    longitudeAxis.Values[i],
                    query.MinLongitude,
                    query.MaxLongitude))
                .ToArray();

            return new GridGeometry(
                latitudes,
                longitudes,
                latitudeIndicesRaw.Select(i => (int?)i).ToArray(),
                longitudeIndicesRaw.Select(i => (int?)i).ToArray());
        }

        private static int[] SelectNativeIndices(
            double[] values,
            double min,
            double max,
            bool descending)
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

            return new[]
            {
                FindNearestIndex(values, (lower + upper) / 2.0)
            };
        }

        private static bool IsWithinAxis(double[] values, double target)
        {
            if (values.Length == 0)
                return false;

            var minimum = Math.Min(values[0], values[values.Length - 1]);
            var maximum = Math.Max(values[0], values[values.Length - 1]);
            return target >= minimum && target <= maximum;
        }

        private static int FindNearestIndex(double[] values, double target)
        {
            if (values.Length == 0)
                throw new InvalidDataException("Coordinate axis is empty.");
            if (values.Length == 1)
                return 0;

            var ascending = values[values.Length - 1] >= values[0];
            var low = 0;
            var high = values.Length - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var current = values[middle];
                if (current == target)
                    return middle;

                if ((ascending && current < target) ||
                    (!ascending && current > target))
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (low <= 0)
                return 0;
            if (low >= values.Length)
                return values.Length - 1;

            return Math.Abs(values[low] - target) <
                   Math.Abs(values[low - 1] - target)
                ? low
                : low - 1;
        }

        private static double NormalizeLongitude(double longitude, Axis axis)
        {
            var minimum = axis.Values.Min();
            var maximum = axis.Values.Max();
            if (minimum >= 0 && maximum > 180 && longitude < 0)
                return longitude + 360;
            if (minimum < 0 && maximum <= 180 && longitude > 180)
                return longitude - 360;
            return longitude;
        }

        private static double ToRequestedLongitudeConvention(
            double sourceLongitude,
            double requestedMinimum,
            double requestedMaximum)
        {
            if (requestedMinimum < 0 &&
                requestedMaximum <= 180 &&
                sourceLongitude > 180)
            {
                return sourceLongitude - 360;
            }

            if (requestedMinimum >= 0 &&
                requestedMaximum > 180 &&
                sourceLongitude < 0)
            {
                return sourceLongitude + 360;
            }

            return sourceLongitude;
        }

        private static void AddSourceResolutionMetadata(
            Dictionary<string, object?> metadata,
            Axis latitudeAxis,
            Axis longitudeAxis)
        {
            var latitudeSpacing = GetAxisSpacingDegrees(latitudeAxis.Values);
            var longitudeSpacing = GetAxisSpacingDegrees(longitudeAxis.Values);
            metadata["sourceLatitudeSpacingDegrees"] = latitudeSpacing;
            metadata["sourceLongitudeSpacingDegrees"] = longitudeSpacing;
            if (latitudeSpacing.HasValue)
                metadata["sourceLatitudeSpacingArcSeconds"] = latitudeSpacing.Value * 3600.0;
            if (longitudeSpacing.HasValue)
                metadata["sourceLongitudeSpacingArcSeconds"] = longitudeSpacing.Value * 3600.0;
        }

        private static double? GetAxisSpacingDegrees(double[] values)
        {
            if (values.Length < 2)
                return null;

            for (var i = 1; i < values.Length; i++)
            {
                var spacing = Math.Abs(values[i] - values[i - 1]);
                if (spacing > 0 &&
                    !double.IsNaN(spacing) &&
                    !double.IsInfinity(spacing))
                {
                    return spacing;
                }
            }

            return null;
        }

        private static double? TransformRawValue(int ncid, int variableId, double raw)
        {
            if (double.IsNaN(raw) || double.IsInfinity(raw))
                return null;
            if (TryGetAttribute(ncid, variableId, "_FillValue", out var fill) &&
                NearlyEqual(raw, fill))
                return null;
            if (TryGetAttribute(ncid, variableId, "missing_value", out var missing) &&
                NearlyEqual(raw, missing))
                return null;

            var value = raw;
            if (TryGetAttribute(ncid, variableId, "scale_factor", out var scale))
                value *= scale;
            if (TryGetAttribute(ncid, variableId, "add_offset", out var offset))
                value += offset;
            return value;
        }

        private static void SetAxisIndex(
            int[] dimensions,
            UIntPtr[] indices,
            int axisDimension,
            int axisIndex)
        {
            var position = Array.IndexOf(dimensions, axisDimension);
            if (position >= 0)
                indices[position] = (UIntPtr)(uint)axisIndex;
        }

        private static void SetAxisRange(
            int[] dimensions,
            UIntPtr[] start,
            UIntPtr[] count,
            int[] counts,
            int axisDimension,
            int axisStart,
            int axisCount)
        {
            var position = Array.IndexOf(dimensions, axisDimension);
            if (position < 0)
                return;

            start[position] = (UIntPtr)(uint)axisStart;
            count[position] = (UIntPtr)(uint)axisCount;
            counts[position] = axisCount;
        }

        private static bool TryGetAttribute(
            int ncid,
            int variableId,
            string name,
            out double value)
        {
            return NetCdfNative.nc_get_att_double(
                ncid,
                variableId,
                name,
                out value) == NetCdfNative.NoError;
        }

        private static bool NearlyEqual(double a, double b)
        {
            return a.Equals(b) ||
                   Math.Abs(a - b) <= Math.Max(1e-12, Math.Abs(b) * 1e-12);
        }

        private static NetCdfFile Open(string path)
        {
            NetCdfNative.ThrowIfError(
                NetCdfNative.nc_open(path, NetCdfNative.Nowrite, out var ncid),
                $"Open NetCDF '{path}'");
            return new NetCdfFile(ncid);
        }

        public void Dispose()
        {
        }

        private readonly struct Axis
        {
            public Axis(int dimensionId, double[] values)
            {
                DimensionId = dimensionId;
                Values = values;
            }

            public int DimensionId { get; }
            public double[] Values { get; }
        }

        private readonly struct AxisDescriptor
        {
            public AxisDescriptor(int dimensionId, int length)
            {
                DimensionId = dimensionId;
                Length = length;
            }

            public int DimensionId { get; }
            public int Length { get; }
        }

        private readonly struct ReadContext
        {
            public ReadContext(
                int dataVariableId,
                int[] dimensionIds,
                Axis latitudeAxis,
                Axis longitudeAxis,
                Axis depthAxis,
                int timeDimensionId,
                int timeLength)
            {
                DataVariableId = dataVariableId;
                DimensionIds = dimensionIds;
                LatitudeAxis = latitudeAxis;
                LongitudeAxis = longitudeAxis;
                DepthAxis = depthAxis;
                TimeDimensionId = timeDimensionId;
                TimeLength = timeLength;
            }

            public int DataVariableId { get; }
            public int[] DimensionIds { get; }
            public Axis LatitudeAxis { get; }
            public Axis LongitudeAxis { get; }
            public Axis DepthAxis { get; }
            public int TimeDimensionId { get; }
            public int TimeLength { get; }
        }

        private readonly struct GridGeometry
        {
            public GridGeometry(
                double[] latitudes,
                double[] longitudes,
                int?[] latitudeIndices,
                int?[] longitudeIndices)
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

            public DataSlab(
                double[] values,
                int[] counts,
                int latitudePosition,
                int longitudePosition)
            {
                if (latitudePosition < 0 || longitudePosition < 0)
                    throw new InvalidDataException(
                        "KODC data variable does not contain configured latitude/longitude dimensions.");

                _values = values;
                _latitudeStride = CalculateStride(counts, latitudePosition);
                _longitudeStride = CalculateStride(counts, longitudePosition);
            }

            public double? Get(int localLatitude, int localLongitude)
            {
                var value =
                    _values[
                        (localLatitude * _latitudeStride) +
                        (localLongitude * _longitudeStride)];

                return double.IsNaN(value) || double.IsInfinity(value)
                    ? (double?)null
                    : value;
            }

            private static int CalculateStride(int[] counts, int position)
            {
                var stride = 1;
                for (var i = position + 1; i < counts.Length; i++)
                    stride = checked(stride * counts[i]);
                return stride;
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
