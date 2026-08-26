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

                // The M2 headers supplied by AVISO define the common 1/16-degree grid.
                // Validate the variables on one selected constituent; all selected files
                // are subsequently checked naturally when queried.
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

            // FES is a harmonic prediction model, so a time is essential. If the
            // caller omits it, use the current UTC instant rather than a month-only climatology.
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

                // Currents are not coast-extrapolated in FES2014a. A constituent
                // with undefined U or V at this cell cannot safely contribute to
                // the vector synthesis.
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
            // Oceanographic 'toward' direction: 0=N, 90=E, clockwise from true north.
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

            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();
            metadata["dataset"] = "FES2014a tide currents";
            metadata["nativeAmplitudeUnit"] = "cm/s";
            metadata["phaseUnit"] = "degrees";
            metadata["outputUnit"] = "m/s";
            metadata["directionConvention"] = "toward, clockwise from true north";
            metadata["constituentMode"] = _option.CurrentConstituentMode.ToString();
            metadata["constituents"] = usedNames.ToArray();
            metadata["requestedConstituentCount"] = _constituents.Count;
            metadata["usedConstituentCount"] = used;

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
            throw new NotSupportedException(
                "FES2014 current raster synthesis is not enabled yet. Use point Query() for the current implementation; grid-speed synthesis will be added with block NetCDF reads for performance.");
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
            return value;
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

        private sealed class NetCdfFile : IDisposable
        {
            public NetCdfFile(int id) { Id = id; }
            public int Id { get; }
            public void Dispose() { NetCdfNative.nc_close(Id); }
        }
    }
}
