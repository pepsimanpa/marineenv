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
    /// Complete, bitwise audit of the decoded NetCDF DOUBLE data. Compaction is
    /// conditional on every original 37-slot cycle matching cycle zero exactly.
    /// Original NC and current KODC reader are never modified.
    /// </summary>
    internal static class FullCycleTool
    {
        private const int SlotCount = 37;
        private const int LatitudeChunk = 8;
        private const int NoWrite = 0;
        private const int NetCdf4 = 0x1000;
        private const int NoClobber = 0x0004;
        private const int DoubleType = 6;
        private const int GlobalVarId = -1;
        private const int MaxExampleCount = 8;

        public static int Run(string[] args)
        {
            var config = ReadOptions(args);
            if (!File.Exists(config.InputPath))
                throw new FileNotFoundException("Input NetCDF file not found.", config.InputPath);
            if (config.OutputNc != null)
            {
                if (string.Equals(config.InputPath, config.OutputNc,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException("Output path must NOT be the original source path.");
                if (File.Exists(config.OutputNc))
                    throw new IOException("Refusing to overwrite existing output file: " + config.OutputNc);
            }

            var report = new StringBuilder();
            report.AppendLine("KODC COMPLETE 37-SLOT CYCLE AUDIT");
            report.AppendLine("Input: " + config.InputPath);
            report.AppendLine("Variable: " + config.Variable);
            report.AppendLine("Rule: compare ALL positions, depths, slots, and later cycles with cycle 0.");
            report.AppendLine("Comparison: bit-exact decoded double values (including NoData/NaN representations).");
            report.AppendLine();

            Native.Check(Native.nc_open(config.InputPath, NoWrite, out var inputId), "Open original file");
            try
            {
                var source = Inspect(inputId, config.Variable);
                var validTimes = source.TimeValues.Count(IsUsefulTime);
                report.AppendLine("Dimensions: lon=" + source.Lon.Length + ", lat=" + source.Lat.Length +
                    ", depth=" + source.Depth.Length + ", time=" + source.TimeValues.Length);
                report.AppendLine("37-slot blocks: " + source.BlockCount);
                report.AppendLine("Time coordinate valid entries: " + validTimes + "/" + source.TimeValues.Length);
                if (validTimes > 0)
                    report.AppendLine("CAUTION: useful time coordinates exist. Do not infer that collapsing dates is semantically harmless.");
                else
                    report.AppendLine("Time coordinate supplies no usable calendar values.");
                report.AppendLine();

                var audit = AuditWholeFile(inputId, source, report);
                report.AppendLine();
                report.AppendLine("COMPLETE AUDIT RESULT");
                report.AppendLine("Compared cell/slot values: " + audit.ComparisonCount.ToString("N0", CultureInfo.InvariantCulture));
                report.AppendLine("Different values (bitwise): " + audit.DifferenceCount.ToString("N0", CultureInfo.InvariantCulture));
                report.AppendLine("Result: " + (audit.DifferenceCount == 0 ? "PASS - all 37-slot blocks are bitwise identical." :
                    "FAIL - at least one 37-slot block differs. Do not compact."));
                report.AppendLine();

                if (audit.DifferenceCount != 0 || validTimes > 0)
                {
                    if (validTimes > 0)
                        report.AppendLine("Compaction blocked because usable source time coordinates exist.");
                    report.AppendLine("Original file unchanged; no compact NetCDF created.");
                    SaveReport(config.ReportPath, config.Variable, report.ToString());
                    return 4;
                }

                if (config.OutputNc != null)
                {
                    CompactAndVerify(inputId, source, config.InputPath, config.OutputNc, report);
                }
                else
                {
                    report.AppendLine("Full audit only; pass --compact <new-file.nc> after reviewing results.");
                }

                SaveReport(config.ReportPath, config.Variable, report.ToString());
                return 0;
            }
            finally
            {
                Native.nc_close(inputId);
            }
        }

        private static Options ReadOptions(string[] args)
        {
            string? input = null, variable = null, report = null, compact = null;
            var fullCheck = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--full-check":
                        fullCheck = true;
                        break;
                    case "--var":
                        variable = RequiredValue(args, ref i);
                        break;
                    case "--compact":
                        compact = RequiredValue(args, ref i);
                        fullCheck = true;
                        break;
                    case "--out":
                        report = RequiredValue(args, ref i);
                        break;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal))
                            throw new ArgumentException("Unknown option: " + args[i]);
                        if (input != null)
                            throw new ArgumentException("Only one source file is supported.");
                        input = args[i];
                        break;
                }
            }

            if (!fullCheck || string.IsNullOrWhiteSpace(input))
                throw new ArgumentException("Usage: <source.nc> [--var CT|SA] --full-check [--compact <new-file.nc>] [--out <report.txt>]");
            variable ??= Path.GetFileName(input).StartsWith("SA_", StringComparison.OrdinalIgnoreCase) ? "SA" : "CT";
            if (variable != "CT" && variable != "SA")
                throw new ArgumentException("Variable must be CT or SA.");
            return new Options(Path.GetFullPath(input), variable, compact == null ? null : Path.GetFullPath(compact),
                report == null ? null : Path.GetFullPath(report));
        }

        private static string RequiredValue(string[] args, ref int index)
        {
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Missing value for " + args[index - 1]);
            return args[index];
        }

        private static Layout Inspect(int inputId, string variable)
        {
            Native.Check(Native.nc_inq_ndims(inputId, out var ndims), "Count source dimensions");
            Native.Check(Native.nc_inq_nvars(inputId, out var nvars), "Count source variables");
            if (ndims != 4 || nvars != 5)
                throw new InvalidDataException("Expected precisely 4 dimensions and 5 variables: lon/lat/depth/time/" + variable +
                    ". Refusing to omit unexpected source content.");

            var lon = ReadAxis(inputId, "lon", out var lonDim);
            var lat = ReadAxis(inputId, "lat", out var latDim);
            var depth = ReadAxis(inputId, "depth", out var depthDim);
            var time = ReadAxis(inputId, "time", out var timeDim);
            if (lon.Length == 0 || lat.Length == 0 || depth.Length == 0 ||
                time.Length < SlotCount || time.Length % SlotCount != 0)
                throw new InvalidDataException("Input dimensions invalid or time dimension not divisible by 37.");

            Native.Check(Native.nc_inq_varid(inputId, variable, out var valueId), "Find " + variable);
            Native.Check(Native.nc_inq_varndims(inputId, valueId, out var dataNdims), "Read data dimensionality");
            if (dataNdims != 4)
                throw new InvalidDataException("Expected " + variable + "(lon,lat,depth,time).");
            var dimIds = new int[4];
            Native.Check(Native.nc_inq_vardimid(inputId, valueId, dimIds), "Read data dimension IDs");
            if (dimIds[0] != lonDim || dimIds[1] != latDim || dimIds[2] != depthDim || dimIds[3] != timeDim)
                throw new InvalidDataException("Unexpected data dimension ordering; expected (lon,lat,depth,time).");
            Native.Check(Native.nc_inq_vartype(inputId, valueId, out var type), "Read data type");
            if (type != DoubleType)
                throw new InvalidDataException("This strict diagnostic requires the source data variable to be NC_DOUBLE.");

            return new Layout(lon, lat, depth, time, valueId, variable);
        }

        private static double[] ReadAxis(int id, string name, out int dimensionId)
        {
            Native.Check(Native.nc_inq_dimid(id, name, out dimensionId), "Find dimension " + name);
            Native.Check(Native.nc_inq_dimlen(id, dimensionId, out var length), "Read dimension " + name);
            Native.Check(Native.nc_inq_varid(id, name, out var varId), "Find axis " + name);
            Native.Check(Native.nc_inq_varndims(id, varId, out var ndims), "Read axis dimensionality");
            if (ndims != 1)
                throw new InvalidDataException("Coordinate " + name + " must be one-dimensional.");
            var axisDims = new int[1];
            Native.Check(Native.nc_inq_vardimid(id, varId, axisDims), "Read axis dimension ID");
            Native.Check(Native.nc_inq_vartype(id, varId, out var type), "Read axis type");
            if (axisDims[0] != dimensionId || type != DoubleType)
                throw new InvalidDataException("Coordinate " + name + " must be NC_DOUBLE over its same-named dimension.");
            var values = new double[checked((int)length.ToUInt64())];
            Native.Check(Native.nc_get_var_double(id, varId, values), "Read coordinate " + name);
            return values;
        }

        private static AuditResult AuditWholeFile(int inputId, Layout source, StringBuilder report)
        {
            long total = 0, differences = 0;
            var examples = new List<string>();
            var times = source.TimeValues.Length;
            var depths = source.Depth.Length;

            // NetCDF C returns row-major values in source dimension order:
            // (one lon, up to eight lat, all depths, all 1110 time indices).
            for (var lon = 0; lon < source.Lon.Length; lon++)
            {
                for (var latStart = 0; latStart < source.Lat.Length; latStart += LatitudeChunk)
                {
                    var latCount = Math.Min(LatitudeChunk, source.Lat.Length - latStart);
                    var slab = ReadSlab(inputId, source.ValueId, lon, latStart, latCount, depths, times);
                    for (var localLat = 0; localLat < latCount; localLat++)
                    {
                        for (var depth = 0; depth < depths; depth++)
                        {
                            var cellStart = (localLat * depths + depth) * times;
                            for (var block = 1; block < source.BlockCount; block++)
                            {
                                var blockStart = cellStart + block * SlotCount;
                                for (var slot = 0; slot < SlotCount; slot++)
                                {
                                    total++;
                                    var baseline = slab[cellStart + slot];
                                    var candidate = slab[blockStart + slot];
                                    if (SameBits(baseline, candidate)) continue;
                                    differences++;
                                    if (examples.Count < MaxExampleCount)
                                    {
                                        examples.Add("First differences: lonIndex=" + lon + " (" +
                                            Format(source.Lon[lon]) + "), latIndex=" + (latStart + localLat) + " (" +
                                            Format(source.Lat[latStart + localLat]) + "), depthIndex=" + depth +
                                            " (" + Format(source.Depth[depth]) + "), block=" + block +
                                            ", slot=" + slot + "; first=" + Format(baseline) +
                                            ", other=" + Format(candidate) + ".");
                                    }
                                }
                            }
                        }
                    }
                }

                if ((lon + 1) % Math.Max(1, source.Lon.Length / 10) == 0 || lon + 1 == source.Lon.Length)
                    Console.Error.WriteLine("Audit progress: longitude " + (lon + 1) + "/" + source.Lon.Length);
            }

            foreach (var example in examples)
                report.AppendLine(example);
            return new AuditResult(total, differences);
        }

        private static void CompactAndVerify(
            int inputId, Layout source, string sourcePath, string desiredOutputPath, StringBuilder report)
        {
            var destDir = Path.GetDirectoryName(desiredOutputPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            var temporaryFile = desiredOutputPath + "." + Guid.NewGuid().ToString("N") + ".partial.nc";
            var created = false;
            try
            {
                Native.Check(Native.nc_create(temporaryFile, NetCdf4 | NoClobber, out var outputId),
                    "Create new temporary compact NetCDF");
                created = true;
                try
                {
                    var dims = new Dictionary<string, int>();
                    foreach (var item in new[]
                    {
                        (Name: "lon", Length: source.Lon.Length),
                        (Name: "lat", Length: source.Lat.Length),
                        (Name: "depth", Length: source.Depth.Length),
                        (Name: "time", Length: SlotCount)
                    })
                    {
                        Native.Check(Native.nc_def_dim(outputId, item.Name, (UIntPtr)(uint)item.Length,
                            out var newDim), "Define " + item.Name + " dimension");
                        dims.Add(item.Name, newDim);
                    }

                    var coordinateNames = new[] { "lon", "lat", "depth", "time" };
                    var coordinates = new Dictionary<string, int>();
                    foreach (var name in coordinateNames)
                    {
                        Native.Check(Native.nc_def_var(outputId, name, DoubleType, 1,
                            new[] { dims[name] }, out var newVar), "Define coordinate " + name);
                        coordinates.Add(name, newVar);
                        Native.Check(Native.nc_inq_varid(inputId, name, out var originalVar), "Source coordinate " + name);
                        CopyAttributes(inputId, originalVar, outputId, newVar);
                    }

                    Native.Check(Native.nc_inq_varid(inputId, source.ValueName, out var originalDataId),
                        "Source data variable");
                    Native.Check(Native.nc_def_var(outputId, source.ValueName, DoubleType, 4,
                        coordinateNames.Select(x => dims[x]).ToArray(), out var compactDataId),
                        "Define compact " + source.ValueName);
                    CopyAttributes(inputId, originalDataId, outputId, compactDataId);
                    Native.Check(Native.nc_def_var_deflate(outputId, compactDataId, 1, 1, 2),
                        "Enable compression for compact data");

                    CopyAttributes(inputId, GlobalVarId, outputId, GlobalVarId);
                    WriteGlobalText(outputId, "marineenv_compaction",
                        "37-slot cycle retained after full bitwise validation of decoded source DOUBLE values.");
                    WriteGlobalText(outputId, "marineenv_source_basename", Path.GetFileName(sourcePath));
                    WriteGlobalText(outputId, "marineenv_original_time_length",
                        source.TimeValues.Length.ToString(CultureInfo.InvariantCulture));
                    WriteGlobalText(outputId, "marineenv_time_semantics",
                        "37 climatological slots. Original time coordinates were unusable; no calendar years assigned.");

                    Native.Check(Native.nc_enddef(outputId), "Finish output definition");
                    WriteAxis(outputId, coordinates["lon"], source.Lon, "lon");
                    WriteAxis(outputId, coordinates["lat"], source.Lat, "lat");
                    WriteAxis(outputId, coordinates["depth"], source.Depth, "depth");
                    WriteAxis(outputId, coordinates["time"], source.TimeValues.Take(SlotCount).ToArray(), "time");

                    for (var lon = 0; lon < source.Lon.Length; lon++)
                    {
                        for (var latStart = 0; latStart < source.Lat.Length; latStart += LatitudeChunk)
                        {
                            var latCount = Math.Min(LatitudeChunk, source.Lat.Length - latStart);
                            var raw = ReadSlab(inputId, source.ValueId, lon, latStart, latCount,
                                source.Depth.Length, source.TimeValues.Length);
                            var compact = new double[latCount * source.Depth.Length * SlotCount];
                            for (var localLat = 0; localLat < latCount; localLat++)
                                for (var depth = 0; depth < source.Depth.Length; depth++)
                                    Array.Copy(raw,
                                        (localLat * source.Depth.Length + depth) * source.TimeValues.Length,
                                        compact,
                                        (localLat * source.Depth.Length + depth) * SlotCount,
                                        SlotCount);

                            Native.Check(Native.nc_put_vara_double(outputId, compactDataId,
                                Indices(lon, latStart, 0, 0),
                                Indices(1, latCount, source.Depth.Length, SlotCount),
                                compact), "Write compact data slab");
                        }

                        if ((lon + 1) % Math.Max(1, source.Lon.Length / 10) == 0 ||
                            lon + 1 == source.Lon.Length)
                            Console.Error.WriteLine("Write progress: longitude " +
                                (lon + 1) + "/" + source.Lon.Length);
                    }
                }
                finally
                {
                    Native.Check(Native.nc_close(outputId), "Close compact temporary file");
                }

                VerifyCompact(inputId, source, temporaryFile, report);
                if (File.Exists(desiredOutputPath))
                    throw new IOException("Output file appeared during processing; refusing overwrite: " + desiredOutputPath);
                File.Move(temporaryFile, desiredOutputPath);
                report.AppendLine("Compact file created: " + desiredOutputPath);
                report.AppendLine("Original size (bytes): " + new FileInfo(sourcePath).Length);
                report.AppendLine("Compact size (bytes): " + new FileInfo(desiredOutputPath).Length);
                report.AppendLine("Original unchanged.");
            }
            finally
            {
                if (created && File.Exists(temporaryFile))
                    File.Delete(temporaryFile);
            }
        }

        private static void VerifyCompact(int inputId, Layout source, string temporaryPath, StringBuilder report)
        {
            Native.Check(Native.nc_open(temporaryPath, NoWrite, out var compactId), "Reopen compact file");
            try
            {
                var compact = Inspect(compactId, source.ValueName);
                if (compact.BlockCount != 1 ||
                    compact.Lon.Length != source.Lon.Length ||
                    compact.Lat.Length != source.Lat.Length ||
                    compact.Depth.Length != source.Depth.Length)
                    throw new InvalidDataException("Compact dimensions differ from expected 37-slot layout.");
                CompareAxis(source.Lon, compact.Lon, "lon");
                CompareAxis(source.Lat, compact.Lat, "lat");
                CompareAxis(source.Depth, compact.Depth, "depth");
                CompareAxis(source.TimeValues.Take(SlotCount).ToArray(), compact.TimeValues, "time");

                long checkedValues = 0;
                for (var lon = 0; lon < source.Lon.Length; lon++)
                {
                    for (var latStart = 0; latStart < source.Lat.Length; latStart += LatitudeChunk)
                    {
                        var latCount = Math.Min(LatitudeChunk, source.Lat.Length - latStart);
                        var original = ReadSlab(inputId, source.ValueId, lon, latStart, latCount,
                            source.Depth.Length, SlotCount);
                        var emitted = ReadSlab(compactId, compact.ValueId, lon, latStart, latCount,
                            source.Depth.Length, SlotCount);
                        if (original.Length != emitted.Length)
                            throw new InvalidDataException("Compact read-back slab length mismatch.");
                        for (var i = 0; i < original.Length; i++)
                        {
                            checkedValues++;
                            if (!SameBits(original[i], emitted[i]))
                                throw new InvalidDataException("Compact read-back differs from source at lon " +
                                    lon + ", lat slab " + latStart + ", slab element " + i + ".");
                        }
                    }
                }

                report.AppendLine("COMPACT READ-BACK VALIDATION: PASS");
                report.AppendLine("All lon/lat/depth/time coordinates match bit-for-bit.");
                report.AppendLine("Compared ALL source first-cycle values to compact read-back: " +
                    checkedValues.ToString("N0", CultureInfo.InvariantCulture) +
                    " exact matches (including decoded NaN bit patterns).");
            }
            finally
            {
                Native.Check(Native.nc_close(compactId), "Close compact verification file");
            }
        }

        private static void CopyAttributes(int sourceId, int oldVarId, int destId, int newVarId)
        {
            int count;
            if (oldVarId == GlobalVarId)
                Native.Check(Native.nc_inq_natts(sourceId, out count), "Count global attributes");
            else
                Native.Check(Native.nc_inq_varnatts(sourceId, oldVarId, out count), "Count variable attributes");

            for (var i = 0; i < count; i++)
            {
                var name = new StringBuilder(256);
                Native.Check(Native.nc_inq_attname(sourceId, oldVarId, i, name), "Read attribute name");
                Native.Check(Native.nc_copy_att(sourceId, oldVarId, name.ToString(), destId, newVarId),
                    "Copy attribute " + name);
            }
        }

        private static void WriteGlobalText(int outputId, string name, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Native.Check(Native.nc_put_att_text(outputId, GlobalVarId, name,
                (UIntPtr)(uint)bytes.Length, bytes), "Set global attribute " + name);
        }

        private static void WriteAxis(int id, int varId, double[] source, string name)
        {
            Native.Check(Native.nc_put_var_double(id, varId, source), "Write " + name + " coordinate");
        }

        private static double[] ReadSlab(int fileId, int valueId, int lon, int latStart,
            int latCount, int depthCount, int timeCount)
        {
            var values = new double[checked(latCount * depthCount * timeCount)];
            Native.Check(Native.nc_get_vara_double(fileId, valueId,
                Indices(lon, latStart, 0, 0), Indices(1, latCount, depthCount, timeCount), values),
                "Read full data slab");
            return values;
        }

        private static UIntPtr[] Indices(int a, int b, int c, int d)
        {
            return new[] { (UIntPtr)(uint)a, (UIntPtr)(uint)b, (UIntPtr)(uint)c, (UIntPtr)(uint)d };
        }

        private static bool SameBits(double left, double right) =>
            BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

        private static bool IsUsefulTime(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value) < 1e30;

        private static void CompareAxis(double[] original, double[] compact, string name)
        {
            if (original.Length != compact.Length)
                throw new InvalidDataException("Compact coordinate size mismatch: " + name);
            for (var i = 0; i < original.Length; i++)
                if (!SameBits(original[i], compact[i]))
                    throw new InvalidDataException("Compact coordinate mismatch: " + name + " at " + i);
        }

        private static void SaveReport(string? configuredPath, string variable, string content)
        {
            var path = configuredPath ??
                Path.Combine(Directory.GetCurrentDirectory(), "kodc-full-validation-" + variable + "-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, content, new UTF8Encoding(true));
            Console.WriteLine(content);
            Console.WriteLine("Report saved: " + Path.GetFullPath(path));
        }

        private static string Format(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

        private sealed class Options
        {
            public Options(string inputPath, string variable, string? outputNc, string? reportPath)
            {
                InputPath = inputPath; Variable = variable; OutputNc = outputNc; ReportPath = reportPath;
            }
            public string InputPath { get; }
            public string Variable { get; }
            public string? OutputNc { get; }
            public string? ReportPath { get; }
        }

        private sealed class Layout
        {
            public Layout(double[] lon, double[] lat, double[] depth, double[] timeValues, int valueId,
                string valueName)
            {
                Lon = lon; Lat = lat; Depth = depth; TimeValues = timeValues; ValueId = valueId;
                ValueName = valueName;
            }
            public double[] Lon { get; }
            public double[] Lat { get; }
            public double[] Depth { get; }
            public double[] TimeValues { get; }
            public int ValueId { get; }
            public string ValueName { get; }
            public int BlockCount => TimeValues.Length / SlotCount;
        }

        private readonly struct AuditResult
        {
            public AuditResult(long comparisonCount, long differenceCount)
            {
                ComparisonCount = comparisonCount; DifferenceCount = differenceCount;
            }
            public long ComparisonCount { get; }
            public long DifferenceCount { get; }
        }

        private static class Native
        {
            private const string Library = "netcdf";
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_open(string path, int mode, out int id);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_close(int id);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_create(string path, int mode, out int id);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_ndims(int id, out int count);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_nvars(int id, out int count);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_natts(int id, out int count);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_dimid(int id, string name, out int dimId);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_dimlen(int id, int dimId, out UIntPtr len);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varid(int id, string name, out int varId);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varndims(int id, int varId, out int count);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_vardimid(int id, int varId, [Out] int[] dimids);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_vartype(int id, int varId, out int type);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_var_double(int id, int varId, [Out] double[] values);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_vara_double(int id, int varId, UIntPtr[] start,
                UIntPtr[] count, [Out] double[] values);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_put_var_double(int id, int varId, double[] values);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_put_vara_double(int id, int varId, UIntPtr[] start,
                UIntPtr[] count, double[] values);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_def_dim(int id, string name, UIntPtr length, out int dimId);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_def_var(int id, string name, int type, int ndims, int[] dimIds,
                out int varId);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_def_var_deflate(int id, int varId, int shuffle,
                int deflate, int level);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_enddef(int id);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varnatts(int id, int varId, out int count);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_attname(int id, int varId, int index, StringBuilder name);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_copy_att(int inputId, int inputVar, string name,
                int outputId, int outputVar);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_put_att_text(int id, int varId, string name, UIntPtr len,
                byte[] value);
            [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr nc_strerror(int error);

            internal static void Check(int code, string operation)
            {
                if (code == 0) return;
                throw new InvalidDataException(operation + ": NetCDF " + code + " (" +
                    (Marshal.PtrToStringAnsi(nc_strerror(code)) ?? "unknown") + ").");
            }
        }
    }
}
