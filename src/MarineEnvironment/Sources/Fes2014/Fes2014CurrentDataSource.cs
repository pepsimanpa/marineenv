using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Native;
using MarineEnvironment.Sources;

namespace MarineEnvironment.Sources.Fes2014
{
    internal sealed class Fes2014CurrentDataSource : IEnvironmentDataSource
    {
        private const long MaxSourceNativeCells = 4_000_000;
        private readonly DataSourceOption _option;
        private readonly string _rootPath;
        private readonly IReadOnlyList<Fes2014Constituent> _constituents;
        private readonly string _eastPath;
        private readonly string _northPath;

        public Fes2014CurrentDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option;
            _rootPath = resolvedPath;
            _eastPath = Path.Combine(_rootPath, "eastward_velocity");
            _northPath = Path.Combine(_rootPath, "northward_velocity");
            _constituents = Fes2014Constituents.Get(option.CurrentConstituentMode);

            if (!option.Enabled)
            {
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                if (!Directory.Exists(_eastPath))
                    throw new DirectoryNotFoundException($"FES2014 eastward directory was not found: {_eastPath}");
                if (!Directory.Exists(_northPath))
                    throw new DirectoryNotFoundException($"FES2014 northward directory was not found: {_northPath}");

                foreach (var constituent in _constituents)
                {
                    var eastFile = EastFile(constituent);
                    var northFile = NorthFile(constituent);
                    if (!File.Exists(eastFile))
                        throw new FileNotFoundException($"FES2014 eastward constituent '{constituent.Name}' was not found.", eastFile);
                    if (!File.Exists(northFile))
                        throw new FileNotFoundException($"FES2014 northward constituent '{constituent.Name}' was not found.", northFile);
                }

                var sample = _constituents.First();
                using (var east = Open(EastFile(sample)))
                {
                    ValidateVariable(east.Id, "lat");
                    ValidateVariable(east.Id, "lon");
                    ValidateVariable(east.Id, "Ua");
                    ValidateVariable(east.Id, "Ug");
                }
                using (var north = Open(NorthFile(sample)))
                {
                    ValidateVariable(north.Id, "lat");
                    ValidateVariable(north.Id, "lon");
                    ValidateVariable(north.Id, "Va");
                    ValidateVariable(north.Id, "Vg");
                }

                Status = SourceStatus.Ready;
                StatusMessage = $"{option.CurrentConstituentMode} / {_constituents.Count} constituent(s)";
            }
            catch (DllNotFoundException ex)
            {
                Status = SourceStatus.NativeLibraryUnavailable;
                StatusMessage = ex.Message;
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

            var when = query.DateTime ?? DateTime.UtcNow;
            var sample = _constituents.First();

            using var sampleFile = Open(EastFile(sample));
            var latAxis = ReadAxis(sampleFile.Id, "lat");
            var lonAxis = ReadAxis(sampleFile.Id, "lon");
            var normalizedLon = NormalizeLongitude(query.Longitude, lonAxis.Values);
            if (!IsWithinAxis(latAxis.Values, query.Latitude) || !IsWithinAxis(lonAxis.Values, normalizedLon))
                return null;

            var latIndex = FindNearestIndex(latAxis.Values, query.Latitude);
            var lonIndex = FindNearestIndex(lonAxis.Values, normalizedLon);

            double uCmPerSecond = 0;
            double vCmPerSecond = 0;
            var used = 0;
            var usedNames = new List<string>(_constituents.Count);

            foreach (var constituent in _constituents)
            {
                using var east = Open(EastFile(constituent));
                using var north = Open(NorthFile(constituent));

                var ua = Read2D(east.Id, "Ua", latIndex, lonIndex);
                var ug = Read2D(east.Id, "Ug", latIndex, lonIndex);
                var va = Read2D(north.Id, "Va", latIndex, lonIndex);
                var vg = Read2D(north.Id, "Vg", latIndex, lonIndex);

                if (!ua.HasValue || !ug.HasValue || !va.HasValue || !vg.HasValue)
                    continue;

                var harmonic = Fes2014Harmonics.Calculate(constituent.Name, when);
                uCmPerSecond += harmonic.Factor * ua.Value * Math.Cos(harmonic.Argument - DegreesToRadians(ug.Value));
                vCmPerSecond += harmonic.Factor * va.Value * Math.Cos(harmonic.Argument - DegreesToRadians(vg.Value));
                used++;
                usedNames.Add(constituent.Name);
            }

            if (used == 0)
                return null;

            var u = uCmPerSecond / 100.0;
            var v = vCmPerSecond / 100.0;
            var speed = Math.Sqrt((u * u) + (v * v));
            var direction = (Math.Atan2(u, v) * 180.0 / Math.PI + 360.0) % 360.0;

            var current = new CurrentValue
            {
                EastwardVelocity = u,
                NorthwardVelocity = v,
                Speed = speed,
                Direction = direction,
                ConstituentMode = _option.CurrentConstituentMode,
                ConstituentCount = used
            };

            var metadata = CreateMetadata();
            metadata["constituents"] = usedNames.ToArray();
            metadata["usedConstituentCount"] = used;
            AddSourceResolutionMetadata(metadata, latAxis, lonAxis);

            return new EnvironmentValue(
                Id,
                EnvironmentType.Current,
                current,
                "m/s",
                latAxis.Values[latIndex],
                lonAxis.Values[lonIndex],
                null,
                when,
                "FES2014a harmonic current",
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"FES2014 current source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.ResolutionMode == GridResolutionMode.Custom && (query.Width < 2 || query.Height < 2))
                throw new ArgumentOutOfRangeException(nameof(query), "FES2014 custom raster width and height must both be at least 2.");

            var when = query.DateTime ?? DateTime.UtcNow;
            var sample = _constituents.First();

            using var sampleFile = Open(EastFile(sample));
            var latAxis = ReadAxis(sampleFile.Id, "lat");
            var lonAxis = ReadAxis(sampleFile.Id, "lon");
            var geometry = query.ResolutionMode == GridResolutionMode.SourceNative
                ? BuildSourceNativeGeometry(query, latAxis, lonAxis)
                : BuildCustomGeometry(query, latAxis, lonAxis);

            if (query.ResolutionMode == GridResolutionMode.SourceNative &&
                (long)geometry.Width * geometry.Height > MaxSourceNativeCells)
            {
                throw new InvalidOperationException(
                    $"FES2014 source-native selection is {geometry.Width:N0} x {geometry.Height:N0} ({(long)geometry.Width * geometry.Height:N0} cells), " +
                    $"which exceeds the {MaxSourceNativeCells:N0}-cell safety limit. Reduce the view bounds or use Custom resolution.");
            }

            var validLatIndices = geometry.LatitudeIndices.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            var validLonIndices = geometry.LongitudeIndices.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
            var values = new double?[geometry.Width * geometry.Height];

            if (validLatIndices.Length == 0 || validLonIndices.Length == 0)
            {
                return new GridResult
                {
                    SourceId = Id,
                    Type = EnvironmentType.Current,
                    Width = geometry.Width,
                    Height = geometry.Height,
                    Latitudes = geometry.Latitudes,
                    Longitudes = geometry.Longitudes,
                    Values = values,
                    Unit = "m/s",
                    DateTime = when,
                    Variable = "FES2014a harmonic current speed",
                    Metadata = CreateGridMetadata(query, latAxis, lonAxis, when, geometry.Width, geometry.Height)
                };
            }

            var latStart = validLatIndices.Min();
            var latEnd = validLatIndices.Max();
            var lonStart = validLonIndices.Min();
            var lonEnd = validLonIndices.Max();
            var latCount = latEnd - latStart + 1;
            var lonCount = lonEnd - lonStart + 1;

            var uSum = new double[values.Length];
            var vSum = new double[values.Length];
            var usedCount = new int[values.Length];

            foreach (var constituent in _constituents)
            {
                var harmonic = Fes2014Harmonics.Calculate(constituent.Name, when);

                using var east = Open(EastFile(constituent));
                using var north = Open(NorthFile(constituent));

                var ua = ReadSlab(east.Id, "Ua", latStart, latCount, lonStart, lonCount);
                var ug = ReadSlab(east.Id, "Ug", latStart, latCount, lonStart, lonCount);
                var va = ReadSlab(north.Id, "Va", latStart, latCount, lonStart, lonCount);
                var vg = ReadSlab(north.Id, "Vg", latStart, latCount, lonStart, lonCount);

                for (var row = 0; row < geometry.Height; row++)
                {
                    if (!geometry.LatitudeIndices[row].HasValue)
                        continue;

                    var sourceRow = geometry.LatitudeIndices[row]!.Value;
                    for (var column = 0; column < geometry.Width; column++)
                    {
                        if (!geometry.LongitudeIndices[column].HasValue)
                            continue;

                        var sourceColumn = geometry.LongitudeIndices[column]!.Value;
                        var uaValue = ua.Get(sourceRow, sourceColumn);
                        var ugValue = ug.Get(sourceRow, sourceColumn);
                        var vaValue = va.Get(sourceRow, sourceColumn);
                        var vgValue = vg.Get(sourceRow, sourceColumn);

                        if (!uaValue.HasValue || !ugValue.HasValue || !vaValue.HasValue || !vgValue.HasValue)
                            continue;

                        var outputIndex = (row * geometry.Width) + column;
                        uSum[outputIndex] += harmonic.Factor * uaValue.Value * Math.Cos(harmonic.Argument - DegreesToRadians(ugValue.Value));
                        vSum[outputIndex] += harmonic.Factor * vaValue.Value * Math.Cos(harmonic.Argument - DegreesToRadians(vgValue.Value));
                        usedCount[outputIndex]++;
                    }
                }
            }

            double? minimum = null;
            double? maximum = null;
            for (var index = 0; index < values.Length; index++)
            {
                if (usedCount[index] == 0)
                {
                    values[index] = null;
                    continue;
                }

                var u = uSum[index] / 100.0;
                var v = vSum[index] / 100.0;
                var speed = Math.Sqrt((u * u) + (v * v));
                values[index] = speed;
                minimum = !minimum.HasValue ? speed : Math.Min(minimum.Value, speed);
                maximum = !maximum.HasValue ? speed : Math.Max(maximum.Value, speed);
            }

            var metadata = CreateGridMetadata(query, latAxis, lonAxis, when, geometry.Width, geometry.Height);
            metadata["sourceSlab"] = new[] { latStart, latEnd, lonStart, lonEnd };
            metadata["sourceSlabSize"] = new[] { latCount, lonCount };

            return new GridResult
            {
                SourceId = Id,
                Type = EnvironmentType.Current,
                Width = geometry.Width,
                Height = geometry.Height,
                Latitudes = geometry.Latitudes,
                Longitudes = geometry.Longitudes,
                Values = values,
                Unit = "m/s",
                DateTime = when,
                Variable = "FES2014a harmonic current speed",
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        private static GridGeometry BuildCustomGeometry(GridQuery query, Axis latAxis, Axis lonAxis)
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
                if (IsWithinAxis(latAxis.Values, requested))
                    latIndices[row] = FindNearestIndex(latAxis.Values, requested);
            }

            for (var column = 0; column < query.Width; column++)
            {
                var t = column / (double)(query.Width - 1);
                var requested = query.MinLongitude + ((query.MaxLongitude - query.MinLongitude) * t);
                outputLongitudes[column] = requested;
                var normalized = NormalizeLongitude(requested, lonAxis.Values);
                if (IsWithinAxis(lonAxis.Values, normalized))
                    lonIndices[column] = FindNearestIndex(lonAxis.Values, normalized);
            }

            return new GridGeometry(outputLatitudes, outputLongitudes, latIndices, lonIndices);
        }

        private static GridGeometry BuildSourceNativeGeometry(GridQuery query, Axis latAxis, Axis lonAxis)
        {
            var latIndicesRaw = SelectNativeIndices(latAxis.Values, query.MinLatitude, query.MaxLatitude, descending: true);
            var normalizedMinLon = NormalizeLongitude(query.MinLongitude, lonAxis.Values);
            var normalizedMaxLon = NormalizeLongitude(query.MaxLongitude, lonAxis.Values);
            if (normalizedMinLon > normalizedMaxLon)
                throw new NotSupportedException("FES2014 source-native rendering across a longitude wrap/dateline is not supported yet. Use Custom resolution.");

            var lonIndicesRaw = SelectNativeIndices(lonAxis.Values, normalizedMinLon, normalizedMaxLon, descending: false);
            var latitudes = latIndicesRaw.Select(i => latAxis.Values[i]).ToArray();
            var longitudes = lonIndicesRaw.Select(i => ToRequestedLongitudeConvention(lonAxis.Values[i], query.MinLongitude, query.MaxLongitude)).ToArray();
            return new GridGeometry(
                latitudes,
                longitudes,
                latIndicesRaw.Select(i => (int?)i).ToArray(),
                lonIndicesRaw.Select(i => (int?)i).ToArray());
        }

        private static int[] SelectNativeIndices(double[] values, double min, double max, bool descending)
        {
            var lower = Math.Min(min, max);
            var upper = Math.Max(min, max);
            var selected = Enumerable.Range(0, values.Length).Where(i => values[i] >= lower && values[i] <= upper);
            var ordered = descending
                ? selected.OrderByDescending(i => values[i]).ToArray()
                : selected.OrderBy(i => values[i]).ToArray();
            if (ordered.Length > 0) return ordered;

            var axisMin = values.Min();
            var axisMax = values.Max();
            if (upper < axisMin || lower > axisMax) return Array.Empty<int>();
            return new[] { FindNearestIndex(values, (lower + upper) / 2.0) };
        }

        private Dictionary<string, object?> CreateMetadata()
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();
            metadata["dataset"] = "FES2014a tide currents";
            metadata["nativeAmplitudeUnit"] = "cm/s";
            metadata["phaseUnit"] = "degrees";
            metadata["outputUnit"] = "m/s";
            metadata["directionConvention"] = "toward, clockwise from true north";
            metadata["constituentMode"] = _option.CurrentConstituentMode.ToString();
            metadata["requestedConstituentCount"] = _constituents.Count;
            return metadata;
        }

        private Dictionary<string, object?> CreateGridMetadata(GridQuery query, Axis latAxis, Axis lonAxis, DateTime when, int width, int height)
        {
            var metadata = CreateMetadata();
            metadata["rasterValue"] = "current speed";
            metadata["sampling"] = query.Sampling.ToString();
            metadata["predictionTime"] = when;
            metadata["requestedBounds"] = new[] { query.MinLatitude, query.MaxLatitude, query.MinLongitude, query.MaxLongitude };
            metadata["sourceBounds"] = new[]
            {
                latAxis.Values.Min(), latAxis.Values.Max(),
                lonAxis.Values.Min(), lonAxis.Values.Max()
            };
            metadata["resolutionMode"] = query.ResolutionMode.ToString();
            metadata["sourceNativeRaster"] = true;
            metadata["renderGrid"] = new[] { width, height };
            AddSourceResolutionMetadata(metadata, latAxis, lonAxis);
            return metadata;
        }

        private static void AddSourceResolutionMetadata(Dictionary<string, object?> metadata, Axis latAxis, Axis lonAxis)
        {
            var latSpacing = GetAxisSpacingDegrees(latAxis.Values);
            var lonSpacing = GetAxisSpacingDegrees(lonAxis.Values);
            metadata["sourceLatitudeCount"] = latAxis.Values.Length;
            metadata["sourceLongitudeCount"] = lonAxis.Values.Length;
            metadata["sourceLatitudeSpacingDegrees"] = latSpacing;
            metadata["sourceLongitudeSpacingDegrees"] = lonSpacing;
            if (latSpacing.HasValue) metadata["sourceLatitudeSpacingArcSeconds"] = latSpacing.Value * 3600.0;
            if (lonSpacing.HasValue) metadata["sourceLongitudeSpacingArcSeconds"] = lonSpacing.Value * 3600.0;
        }

        private string EastFile(Fes2014Constituent c) => Path.Combine(_eastPath, c.FileStem + ".nc");
        private string NorthFile(Fes2014Constituent c) => Path.Combine(_northPath, c.FileStem + ".nc");

        private static double? Read2D(int ncid, string variable, int latIndex, int lonIndex)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variable, out var varId), $"Find FES variable '{variable}'");
            var indices = new[] { (UIntPtr)(uint)latIndex, (UIntPtr)(uint)lonIndex };
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var1_double(ncid, varId, indices, out var value), $"Read FES variable '{variable}'");
            if (TryGetAttribute(ncid, varId, "_FillValue", out var fill) && NearlyEqual(value, fill))
                return null;
            if (TryGetAttribute(ncid, varId, "missing_value", out var missing) && NearlyEqual(value, missing))
                return null;
            if (TryGetAttribute(ncid, varId, "scale_factor", out var scale))
                value *= scale;
            if (TryGetAttribute(ncid, varId, "add_offset", out var offset))
                value += offset;
            return value;
        }

        private static Slab ReadSlab(int ncid, string variable, int latStart, int latCount, int lonStart, int lonCount)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variable, out var varId), $"Find FES variable '{variable}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, varId, out var ndims), $"Read dimensions for FES variable '{variable}'");
            if (ndims != 2)
                throw new InvalidDataException($"FES variable '{variable}' must have dimensions (lat, lon).");

            var start = new[] { (UIntPtr)(uint)latStart, (UIntPtr)(uint)lonStart };
            var count = new[] { (UIntPtr)(uint)latCount, (UIntPtr)(uint)lonCount };
            var raw = new double[checked(latCount * lonCount)];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_vara_double(ncid, varId, start, count, raw), $"Read FES slab '{variable}'");

            double? fill = null;
            double? missing = null;
            var scale = 1.0;
            var offset = 0.0;
            if (TryGetAttribute(ncid, varId, "_FillValue", out var fillValue)) fill = fillValue;
            if (TryGetAttribute(ncid, varId, "missing_value", out var missingValue)) missing = missingValue;
            if (TryGetAttribute(ncid, varId, "scale_factor", out var scaleValue)) scale = scaleValue;
            if (TryGetAttribute(ncid, varId, "add_offset", out var offsetValue)) offset = offsetValue;

            return new Slab(latStart, lonStart, lonCount, raw, fill, missing, scale, offset);
        }

        private static Axis ReadAxis(int ncid, string variableName)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variableName, out var varId), $"Find FES axis '{variableName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varndims(ncid, varId, out var ndims), $"Read FES axis dimensions '{variableName}'");
            if (ndims != 1)
                throw new InvalidDataException($"FES axis '{variableName}' must be one-dimensional.");
            var dimIds = new int[1];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_vardimid(ncid, varId, dimIds), $"Read FES axis dimension '{variableName}'");
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_dimlen(ncid, dimIds[0], out var len), $"Read FES axis length '{variableName}'");
            var values = new double[checked((int)len.ToUInt64())];
            NetCdfNative.ThrowIfError(NetCdfNative.nc_get_var_double(ncid, varId, values), $"Read FES axis '{variableName}'");
            return new Axis(values);
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
            var ascending = values[values.Length - 1] >= values[0];
            var lo = 0;
            var hi = values.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + ((hi - lo) / 2);
                if (values[mid] == target) return mid;
                if ((ascending && values[mid] < target) || (!ascending && values[mid] > target)) lo = mid + 1;
                else hi = mid - 1;
            }
            if (lo <= 0) return 0;
            if (lo >= values.Length) return values.Length - 1;
            return Math.Abs(values[lo] - target) < Math.Abs(values[lo - 1] - target) ? lo : lo - 1;
        }

        private static double NormalizeLongitude(double longitude, double[] values)
        {
            var min = values.Min();
            var max = values.Max();
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

        private static double DegreesToRadians(double value) => value * Math.PI / 180.0;
        private static bool TryGetAttribute(int ncid, int varId, string name, out double value) =>
            NetCdfNative.nc_get_att_double(ncid, varId, name, out value) == NetCdfNative.NoError;
        private static bool NearlyEqual(double a, double b) =>
            a.Equals(b) || Math.Abs(a - b) <= Math.Max(1e-12, Math.Abs(b) * 1e-12);
        private static void ValidateVariable(int ncid, string variable) =>
            NetCdfNative.ThrowIfError(NetCdfNative.nc_inq_varid(ncid, variable, out _), $"Find FES variable '{variable}'");
        private static NetCdfFile Open(string path)
        {
            NetCdfNative.ThrowIfError(NetCdfNative.nc_open(path, NetCdfNative.Nowrite, out var ncid), $"Open FES NetCDF '{path}'");
            return new NetCdfFile(ncid);
        }

        public void Dispose() { }

        private readonly struct Axis
        {
            public Axis(double[] values) { Values = values; }
            public double[] Values { get; }
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

        private readonly struct Slab
        {
            public Slab(int latStart, int lonStart, int lonCount, double[] values,
                        double? fill, double? missing, double scale, double offset)
            {
                LatStart = latStart;
                LonStart = lonStart;
                LonCount = lonCount;
                Values = values;
                Fill = fill;
                Missing = missing;
                Scale = scale;
                Offset = offset;
            }

            public int LatStart { get; }
            public int LonStart { get; }
            public int LonCount { get; }
            public double[] Values { get; }
            public double? Fill { get; }
            public double? Missing { get; }
            public double Scale { get; }
            public double Offset { get; }

            public double? Get(int latIndex, int lonIndex)
            {
                var relativeRow = latIndex - LatStart;
                var relativeColumn = lonIndex - LonStart;
                var raw = Values[(relativeRow * LonCount) + relativeColumn];
                if (double.IsNaN(raw) || double.IsInfinity(raw)) return null;
                if (Fill.HasValue && NearlyEqual(raw, Fill.Value)) return null;
                if (Missing.HasValue && NearlyEqual(raw, Missing.Value)) return null;
                return (raw * Scale) + Offset;
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
