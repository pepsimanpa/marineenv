using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KodcDiagnostics
{
    /// <summary>
    /// Diagnostic only: reads a few CT/SA scalar samples without modifying the NetCDF source.
    /// It does not assume that time blocks 0..29 actually correspond to calendar years.
    /// </summary>
    internal static class Program
    {
        private const int ExpectedSlotsPerBlock = 37;
        private const double MissingThreshold = 1e30;
        private static readonly int[] TestSlots = { 0, 9, 18, 27, 36 };
        private static readonly double[] CandidateFractions = { 0.15, 0.28, 0.41, 0.54, 0.67, 0.80, 0.91 };

        private static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--full-check") || args.Contains("--compact"))
                    return FullCycleTool.Run(args);

                var options = ParseArgs(args);
                if (options == null)
                    return 1;

                if (!File.Exists(options.FilePath))
                    throw new FileNotFoundException("KODC file not found.", options.FilePath);

                var report = Diagnose(options);
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine(report);
                var outputPath = options.OutputPath ??
                    Path.Combine(Directory.GetCurrentDirectory(),
                        "kodc-time-diagnostic-" + options.Variable + "-" +
                        DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                    Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(outputPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.WriteLine("Saved: " + Path.GetFullPath(outputPath));
                return 0;
            }
            catch (DllNotFoundException ex)
            {
                Console.Error.WriteLine("netcdf.dll or a dependent native DLL is missing. " +
                    "Place the complete x64 runtime under native\\win-x64 and rebuild. " + ex.Message);
                return 2;
            }
            catch (BadImageFormatException ex)
            {
                Console.Error.WriteLine("NetCDF DLL architecture mismatch; this project requires x64. " + ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("KODC diagnostic failed: " + ex);
                return 3;
            }
        }

        private static Options? ParseArgs(string[] args)
        {
            if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine("Usage: dotnet run --project src/KodcDiagnostics -- <file.nc> [--var CT|SA] [--lat 35.5 --lon 126.5] [--out report.txt]");
                return null;
            }

            string? file = null;
            string? variable = null;
            string? output = null;
            double? latitude = null;
            double? longitude = null;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--var":
                        variable = RequireNext(args, ref i);
                        break;
                    case "--lat":
                        latitude = double.Parse(RequireNext(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "--lon":
                        longitude = double.Parse(RequireNext(args, ref i), CultureInfo.InvariantCulture);
                        break;
                    case "--out":
                        output = RequireNext(args, ref i);
                        break;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException("Unknown argument: " + args[i]);
                        if (file != null)
                            throw new ArgumentException("Specify just one NetCDF path.");
                        file = args[i];
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("NetCDF file path required.");
            if (latitude.HasValue != longitude.HasValue)
                throw new ArgumentException("Specify both --lat and --lon, or neither.");

            variable ??= Path.GetFileName(file).StartsWith("SA_", StringComparison.OrdinalIgnoreCase) ? "SA" : "CT";
            if (string.IsNullOrWhiteSpace(variable))
                throw new ArgumentException("Variable name required.");

            return new Options(Path.GetFullPath(file), variable, latitude, longitude, output);
        }

        private static string RequireNext(string[] args, ref int i)
        {
            if (++i >= args.Length)
                throw new ArgumentException("Missing value after " + args[i - 1]);
            return args[i];
        }

        private static string Diagnose(Options options)
        {
            var report = new StringBuilder();
            report.AppendLine("KODC time-dimension diagnosis");
            report.AppendLine("File: " + options.FilePath);
            report.AppendLine("Variable: " + options.Variable);
            report.AppendLine("This program only reads the source; it does NOT average, modify or assign calendar years.");
            report.AppendLine();

            Native.Check(Native.nc_open(options.FilePath, 0, out var fileId), "nc_open");
            try
            {
                var lon = ReadAxis(fileId, "lon", out var lonDim);
                var lat = ReadAxis(fileId, "lat", out var latDim);
                var depth = ReadAxis(fileId, "depth", out var depthDim);
                var time = ReadAxis(fileId, "time", out var timeDim);

                Native.Check(Native.nc_inq_varid(fileId, options.Variable, out var dataId), "Find " + options.Variable);
                Native.Check(Native.nc_inq_varndims(fileId, dataId, out var ndim), "Variable dimensionality");
                if (ndim != 4)
                    throw new InvalidDataException(options.Variable + " is expected to have four dimensions.");
                var dataDims = new int[ndim];
                Native.Check(Native.nc_inq_vardimid(fileId, dataId, dataDims), "Variable dimension IDs");
                if (dataDims[0] != lonDim || dataDims[1] != latDim ||
                    dataDims[2] != depthDim || dataDims[3] != timeDim)
                    throw new InvalidDataException("Expected variable layout " +
                        options.Variable + "(lon,lat,depth,time). Actual dimension order differs.");

                report.AppendLine("Dimensions: lon=" + lon.Length + ", lat=" + lat.Length +
                    ", depth=" + depth.Length + ", time=" + time.Length);
                report.AppendLine("Coordinates: lat " + Format(lat.Min()) + ".." + Format(lat.Max()) +
                    ", lon " + Format(lon.Min()) + ".." + Format(lon.Max()));
                report.AppendLine("Time attributes: units=" + (TryReadTextAttribute(fileId, timeDim, "time", "units") ?? "<absent>") +
                    ", calendar=" + (TryReadTextAttribute(fileId, timeDim, "time", "calendar") ?? "<absent>"));
                var validTimes = time.Where(IsValidValue).ToArray();
                report.AppendLine("Finite/non-fill time coordinate count: " + validTimes.Length + "/" + time.Length);
                report.AppendLine("First time coordinates: " +
                    string.Join(", ", time.Take(6).Select(Format)));
                report.AppendLine("37-slot blocks if grouped mechanically: " +
                    (time.Length % ExpectedSlotsPerBlock == 0
                        ? (time.Length / ExpectedSlotsPerBlock).ToString(CultureInfo.InvariantCulture)
                        : "not divisible by 37"));
                report.AppendLine();

                if (time.Length < ExpectedSlotsPerBlock)
                {
                    report.AppendLine("Time length under 37; this diagnostic cannot perform 37-slot comparisons.");
                    return report.ToString();
                }

                var blockCount = time.Length / ExpectedSlotsPerBlock;
                if (time.Length % ExpectedSlotsPerBlock != 0)
                    report.AppendLine("Warning: trailing time records outside complete 37-slot blocks are ignored.");

                var locations = PickLocations(fileId, dataId, lon, lat, depth, time.Length, options, report);
                if (locations.Count == 0)
                {
                    report.AppendLine("No valid CT/SA test coordinates were located. Use --lat/--lon for a known sea point.");
                    return report.ToString();
                }

                var depthIndices = new[] { 0, Math.Min(3, depth.Length - 1) }.Distinct().ToArray();
                var comparisons = 0;
                var varying = 0;
                var identical = 0;
                var insufficient = 0;
                (SamplePoint Point, int Depth, int Slot, List<double?> Values)? representative = null;
                var representativeValidCount = -1;

                foreach (var point in locations)
                {
                    report.AppendLine("Point: lon=" + Format(lon[point.LonIndex]) +
                        " lat=" + Format(lat[point.LatIndex]) +
                        " (array indices lon=" + point.LonIndex + ", lat=" + point.LatIndex + ")");

                    foreach (var depthIndex in depthIndices)
                    {
                        foreach (var slot in TestSlots.Where(x => x < ExpectedSlotsPerBlock))
                        {
                            var values = new List<double?>();
                            for (var block = 0; block < blockCount; block++)
                                values.Add(ReadValue(fileId, dataId, point, depthIndex,
                                    block * ExpectedSlotsPerBlock + slot));

                            var valid = values.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                            if (valid.Length > representativeValidCount)
                            {
                                representative = (point, depthIndex, slot, values);
                                representativeValidCount = valid.Length;
                            }

                            var stat = valid.Length >= 2
                                ? ValuesDiffer(valid) ? "DIFFERENT" : "SAME"
                                : "INSUFFICIENT";

                            if (stat == "INSUFFICIENT")
                                insufficient++;
                            else
                            {
                                comparisons++;
                                if (stat == "DIFFERENT") varying++;
                                else identical++;
                            }

                            report.AppendLine("  depthIndex=" + depthIndex + " (depth=" + Format(depth[depthIndex]) +
                                ") slot=" + slot.ToString("00", CultureInfo.InvariantCulture) +
                                " valid=" + valid.Length + "/" + blockCount +
                                " range=" + (valid.Length > 0
                                    ? Format(valid.Min()) + ".." + Format(valid.Max())
                                    : "NoData") +
                                "  " + stat);
                        }
                    }
                    report.AppendLine();
                }

                if (representative.HasValue)
                {
                    var selected = representative.Value;
                    report.AppendLine("Representative values at the same within-block slot:");
                    report.AppendLine("Point: lon=" + Format(lon[selected.Point.LonIndex]) +
                        " lat=" + Format(lat[selected.Point.LatIndex]) +
                        ", depth=" + Format(depth[selected.Depth]) + ", slot=" + selected.Slot);
                    report.AppendLine("block | time index | " + options.Variable + " value");
                    for (var i = 0; i < selected.Values.Count; i++)
                        report.AppendLine(i.ToString("00", CultureInfo.InvariantCulture) + "    | " +
                            (i * ExpectedSlotsPerBlock + selected.Slot).ToString("0000", CultureInfo.InvariantCulture) +
                            "       | " +
                            (selected.Values[i].HasValue ? Format(selected.Values[i]!.Value) : "NoData"));
                    report.AppendLine();
                }

                report.AppendLine("Summary: " + comparisons + " usable position/depth/slot comparisons, " +
                    varying + " DIFFERENT, " + identical + " SAME, " + insufficient + " INSUFFICIENT.");
                if (varying > 0)
                    report.AppendLine("Finding: values in the 37-record blocks are not simply identical repetitions at all sampled points.");
                else if (comparisons >= 10)
                    report.AppendLine("Finding: tested blocks match within numerical tolerance at sampled points only; this does not prove the entire file repeats.");
                else
                    report.AppendLine("Finding: insufficient data for a useful repetition assessment.");
                report.AppendLine("Important: even if blocks differ, this alone does NOT prove that they are 1994..2023 annual records.");
                report.AppendLine("For calendar-year attribution, a producer's file layout specification or original generation code is required.");
                return report.ToString();
            }
            finally
            {
                Native.nc_close(fileId);
            }
        }

        private static List<SamplePoint> PickLocations(
            int fileId, int dataId, double[] lon, double[] lat, double[] depth,
            int timeLength, Options options, StringBuilder report)
        {
            if (options.Latitude.HasValue && options.Longitude.HasValue)
            {
                var requestedLatitude = options.Latitude.Value;
                var requestedLongitude = options.Longitude!.Value;
                var point = new SamplePoint(NearestIndex(lon, requestedLongitude),
                    NearestIndex(lat, requestedLatitude));
                report.AppendLine("Manual requested lat/lon: " + Format(requestedLatitude) + "/" +
                    Format(requestedLongitude) + " (nearest coordinate is used).");
                report.AppendLine();
                return new List<SamplePoint> { point };
            }

            var found = new List<SamplePoint>();
            var checkedPoints = new HashSet<string>();
            var times = new[] { 0, 9, 18, 37, 46, 55, 74, 83 }
                .Where(x => x < timeLength).Distinct().ToArray();

            // First try geographically dispersed positions instead of filling
            // all five samples along the same latitude band.
            var candidatePairs = new List<(double Latitude, double Longitude)>();
            for (var i = 0; i < CandidateFractions.Length; i++)
                candidatePairs.Add((CandidateFractions[i],
                    CandidateFractions[(i * 3 + 1) % CandidateFractions.Length]));
            foreach (var fLat in CandidateFractions)
                foreach (var fLon in CandidateFractions)
                    candidatePairs.Add((fLat, fLon));

            foreach (var candidate in candidatePairs)
            {
                var point = new SamplePoint(
                    (int)Math.Round((lon.Length - 1) * candidate.Longitude),
                    (int)Math.Round((lat.Length - 1) * candidate.Latitude));
                if (!checkedPoints.Add(point.LonIndex + ":" + point.LatIndex))
                    continue;

                var validCount = times.Count(t =>
                    ReadValue(fileId, dataId, point, 0, t).HasValue);
                if (validCount < 2)
                    continue;
                found.Add(point);
                if (found.Count >= 5)
                    return found;
            }

            if (found.Count == 0)
                report.AppendLine("Automatic sample grid found no points with 2+ valid time records.");
            return found;
        }

        private static double? ReadValue(int fileId, int dataId, SamplePoint point, int depthIndex, int timeIndex)
        {
            var index = new[]
            {
                (UIntPtr)(uint)point.LonIndex,
                (UIntPtr)(uint)point.LatIndex,
                (UIntPtr)(uint)depthIndex,
                (UIntPtr)(uint)timeIndex
            };
            Native.Check(Native.nc_get_var1_double(fileId, dataId, index, out var value), "Read scalar CT/SA");
            return IsValidValue(value) ? value : (double?)null;
        }

        private static bool ValuesDiffer(IReadOnlyList<double> values)
        {
            var minimum = values.Min();
            var maximum = values.Max();
            var tolerance = Math.Max(1e-5, Math.Max(Math.Abs(minimum), Math.Abs(maximum)) * 1e-6);
            return maximum - minimum > tolerance;
        }

        private static double[] ReadAxis(int fileId, string name, out int dimId)
        {
            Native.Check(Native.nc_inq_dimid(fileId, name, out dimId), "Find " + name + " dimension");
            Native.Check(Native.nc_inq_dimlen(fileId, dimId, out var len), "Read " + name + " length");
            Native.Check(Native.nc_inq_varid(fileId, name, out var varId), "Find " + name + " coordinate");
            var values = new double[checked((int)len.ToUInt64())];
            Native.Check(Native.nc_get_var_double(fileId, varId, values), "Read " + name + " coordinate");
            return values;
        }

        private static string? TryReadTextAttribute(int fileId, int dimId, string coordinate, string attribute)
        {
            _ = dimId;
            if (Native.nc_inq_varid(fileId, coordinate, out var varId) != 0 ||
                Native.nc_inq_attlen(fileId, varId, attribute, out var len) != 0)
                return null;

            var count = checked((int)len.ToUInt64());
            if (count <= 0 || count > 4096)
                return null;
            var bytes = new byte[count];
            return Native.nc_get_att_text(fileId, varId, attribute, bytes) == 0
                ? Encoding.UTF8.GetString(bytes).TrimEnd('\0')
                : null;
        }

        private static bool IsValidValue(double x) =>
            !double.IsNaN(x) && !double.IsInfinity(x) && Math.Abs(x) < MissingThreshold;

        private static int NearestIndex(IReadOnlyList<double> axis, double requested)
        {
            var best = 0;
            var bestDistance = double.PositiveInfinity;
            for (var i = 0; i < axis.Count; i++)
            {
                if (!IsValidValue(axis[i])) continue;
                var distance = Math.Abs(axis[i] - requested);
                if (distance >= bestDistance) continue;
                best = i;
                bestDistance = distance;
            }
            if (double.IsPositiveInfinity(bestDistance))
                throw new InvalidDataException("No valid latitude/longitude axis coordinates.");
            return best;
        }

        private static string Format(double value) =>
            value.ToString("G12", CultureInfo.InvariantCulture);

        private sealed class Options
        {
            public Options(string filePath, string variable, double? latitude, double? longitude, string? outputPath)
            {
                FilePath = filePath;
                Variable = variable;
                Latitude = latitude;
                Longitude = longitude;
                OutputPath = outputPath;
            }
            public string FilePath { get; }
            public string Variable { get; }
            public double? Latitude { get; }
            public double? Longitude { get; }
            public string? OutputPath { get; }
        }

        private readonly struct SamplePoint
        {
            public SamplePoint(int lonIndex, int latIndex)
            {
                LonIndex = lonIndex;
                LatIndex = latIndex;
            }
            public int LonIndex { get; }
            public int LatIndex { get; }
        }

        private static class Native
        {
            private const string LibraryName = "netcdf";

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_open(string path, int mode, out int id);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_close(int id);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_dimid(int id, string name, out int dimId);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_dimlen(int id, int dimId, out UIntPtr length);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varid(int id, string name, out int varId);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varndims(int id, int varId, out int ndims);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_vardimid(int id, int varId, [Out] int[] dimIds);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_var_double(int id, int varId, [Out] double[] values);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_var1_double(int id, int varId, UIntPtr[] index, out double value);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_attlen(int id, int varId, string name, out UIntPtr length);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_att_text(int id, int varId, string name, [Out] byte[] value);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr nc_strerror(int error);

            internal static void Check(int code, string operation)
            {
                if (code == 0) return;
                var text = Marshal.PtrToStringAnsi(nc_strerror(code)) ?? "unknown";
                throw new InvalidDataException(operation + ": NetCDF " + code + " (" + text + ")");
            }
        }
    }
}
