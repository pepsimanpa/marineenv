using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;

namespace MarineEnvironment.Sources.Goci2
{
    /// <summary>
    /// Offline GOCI-II turbidity source.
    ///
    /// The source keeps the downloaded GOCI-II L2 LA mosaic TSS files as-is and does not
    /// build a separate averaged database. For every point/grid query it reads up to five
    /// mosaic files, filters invalid pixels through Goci2TssDataSource, takes the pixel-wise
    /// median TSS, and derives turbidity with the project-selected KIOST/Gomso relation:
    ///
    ///     Turbidity [NTU] = 0.3671 * TSS [mg/L]
    ///
    /// GOCI-II TSS is stored as g/m^3 and 1 g/m^3 = 1 mg/L, so the numeric TSS value can be
    /// used directly in the relation. The result is derived, not a satellite-observed NTU.
    /// </summary>
    internal sealed class Goci2TurbidityDataSource : IEnvironmentDataSource
    {
        private const int MaximumAggregationFiles = 5;
        private const int MinimumValidObservations = 1;
        private const double TssToTurbidityFactor = 0.3671;
        private const string DerivedVariableName = "DerivedTurbidity";

        private static readonly Regex MosaicFileRegex = new Regex(
            @"^GK2B_GOCI2_L2_(?<date>\d{8})_(?<time>\d{6})_LA_TSS\.nc$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly DataSourceOption _option;
        private readonly string _resolvedPath;
        private readonly MosaicFile[] _availableFiles;
        private readonly Dictionary<string, Goci2TssDataSource> _readers =
            new Dictionary<string, Goci2TssDataSource>(StringComparer.OrdinalIgnoreCase);
        private readonly object _readerSync = new object();
        private bool _disposed;

        public Goci2TurbidityDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option ?? throw new ArgumentNullException(nameof(option));
            _resolvedPath = resolvedPath ?? throw new ArgumentNullException(nameof(resolvedPath));

            if (!option.Enabled)
            {
                _availableFiles = Array.Empty<MosaicFile>();
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                _availableFiles = ResolveAvailableFiles(resolvedPath).OrderBy(x => x.ObservationUtc).ToArray();
                if (_availableFiles.Length == 0)
                {
                    Status = SourceStatus.FileNotFound;
                    StatusMessage = $"No GOCI-II LA TSS mosaic files were found at '{resolvedPath}'.";
                    return;
                }

                // Validate one file immediately. Remaining files are opened lazily when selected.
                var validationReader = GetOrCreateReader(_availableFiles[_availableFiles.Length - 1]);
                if (validationReader.Status != SourceStatus.Ready)
                {
                    Status = validationReader.Status;
                    StatusMessage = validationReader.StatusMessage;
                    return;
                }

                Status = SourceStatus.Ready;
                StatusMessage =
                    $"GOCI-II LA TSS mosaics: {_availableFiles.Length} file(s); query-time median of up to {MaximumAggregationFiles}, " +
                    $"derived turbidity = {TssToTurbidityFactor.ToString(CultureInfo.InvariantCulture)} x TSS (NTU).";
            }
            catch (DllNotFoundException ex)
            {
                _availableFiles = Array.Empty<MosaicFile>();
                Status = SourceStatus.NativeLibraryUnavailable;
                StatusMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _availableFiles = Array.Empty<MosaicFile>();
                Status = SourceStatus.Error;
                StatusMessage = ex.Message;
            }
        }

        public string Id => _option.Id;
        public EnvironmentType Type => EnvironmentType.Turbidity;
        public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
        public string? StatusMessage { get; private set; }

        public EnvironmentValue? Query(EnvironmentQuery query)
        {
            ThrowIfDisposed();
            if (Status != SourceStatus.Ready)
                return null;

            var selectedFiles = SelectFiles(query.DateTime);
            var samples = new List<TssSample>(selectedFiles.Length);

            foreach (var file in selectedFiles)
            {
                var reader = GetOrCreateReader(file);
                if (reader.Status != SourceStatus.Ready)
                    continue;

                var raw = reader.Query(query);
                if (raw?.Value is not double tss || double.IsNaN(tss) || double.IsInfinity(tss))
                    continue;

                samples.Add(new TssSample(file, tss));
            }

            if (samples.Count < MinimumValidObservations)
                return null;

            var medianTss = Median(samples.Select(x => x.TssGm3).ToArray());
            var turbidity = medianTss * TssToTurbidityFactor;
            var metadata = CreateDerivedMetadata(selectedFiles, samples.Select(x => x.File).ToArray());
            metadata["tssSamplesGm3"] = samples.Select(x => x.TssGm3).ToArray();
            metadata["tssMedianGm3"] = medianTss;
            metadata["tssMedianMgL"] = medianTss;
            metadata["validObservationCount"] = samples.Count;

            return new EnvironmentValue(
                Id,
                EnvironmentType.Turbidity,
                turbidity,
                "NTU",
                query.Latitude,
                query.Longitude,
                null,
                null,
                DerivedVariableName,
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            ThrowIfDisposed();
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"Source '{Id}' is not ready: {Status} - {StatusMessage}");

            var selectedFiles = SelectFiles(query.DateTime);
            var sourceGrids = new List<GridResult>(selectedFiles.Length);
            var usedFiles = new List<MosaicFile>(selectedFiles.Length);

            foreach (var file in selectedFiles)
            {
                var reader = GetOrCreateReader(file);
                if (reader.Status != SourceStatus.Ready)
                    continue;

                var grid = reader.QueryGrid(query);
                sourceGrids.Add(grid);
                usedFiles.Add(file);
            }

            var latitudes = BuildDescendingAxis(query.MaxLatitude, query.MinLatitude, query.Height);
            var longitudes = BuildAscendingAxis(query.MinLongitude, query.MaxLongitude, query.Width);
            var values = new double?[checked(query.Width * query.Height)];
            double? minimum = null;
            double? maximum = null;
            var cellsWithValue = 0;

            if (sourceGrids.Count > 0)
            {
                var buffer = new double[sourceGrids.Count];
                for (var i = 0; i < values.Length; i++)
                {
                    var count = 0;
                    for (var g = 0; g < sourceGrids.Count; g++)
                    {
                        var value = sourceGrids[g].Values[i];
                        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                            continue;
                        buffer[count++] = value.Value;
                    }

                    if (count < MinimumValidObservations)
                        continue;

                    Array.Sort(buffer, 0, count);
                    var medianTss = MedianFromSorted(buffer, count);
                    var turbidity = medianTss * TssToTurbidityFactor;
                    values[i] = turbidity;
                    cellsWithValue++;
                    minimum = !minimum.HasValue ? turbidity : Math.Min(minimum.Value, turbidity);
                    maximum = !maximum.HasValue ? turbidity : Math.Max(maximum.Value, turbidity);
                }

                // All child grids use the requested geographic display geometry, so use one
                // child's axes to preserve exactly the same sampling geometry as the raw reader.
                latitudes = sourceGrids[0].Latitudes;
                longitudes = sourceGrids[0].Longitudes;
            }

            var metadata = CreateDerivedMetadata(selectedFiles, usedFiles.ToArray());
            metadata["requestedBounds"] = new[]
            {
                query.MinLatitude, query.MaxLatitude, query.MinLongitude, query.MaxLongitude
            };
            metadata["renderGrid"] = new[] { query.Width, query.Height };
            metadata["cellsWithDerivedValue"] = cellsWithValue;
            metadata["sourceNativeRaster"] = false;
            metadata["curvilinearGeolocation"] = true;

            return new GridResult
            {
                SourceId = Id,
                Type = EnvironmentType.Turbidity,
                Width = query.Width,
                Height = query.Height,
                Latitudes = latitudes,
                Longitudes = longitudes,
                Values = values,
                Unit = "NTU",
                DateTime = null,
                Variable = DerivedVariableName,
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        private Dictionary<string, object?> CreateDerivedMetadata(
            IReadOnlyList<MosaicFile> selectedFiles,
            IReadOnlyList<MosaicFile> usedFiles)
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();

            metadata["derived"] = true;
            metadata["sensor"] = "GOCI-II";
            metadata["processingLevel"] = "L2";
            metadata["observationMode"] = "LA Mosaic";
            metadata["sourceParameter"] = "Total Suspended Solids concentration";
            metadata["sourceUnit"] = "g/m^3 (= mg/L)";
            metadata["outputParameter"] = "Turbidity";
            metadata["outputUnit"] = "NTU";
            metadata["aggregation"] = "Median";
            metadata["aggregationAtQueryTime"] = true;
            metadata["maximumAggregationFiles"] = MaximumAggregationFiles;
            metadata["minimumValidObservations"] = MinimumValidObservations;
            metadata["selectedFileCount"] = selectedFiles.Count;
            metadata["usedFileCount"] = usedFiles.Count;
            metadata["selectedFiles"] = selectedFiles.Select(x => Path.GetFileName(x.Path)).ToArray();
            metadata["usedFiles"] = usedFiles.Select(x => Path.GetFileName(x.Path)).ToArray();
            metadata["selectedObservationUtc"] = selectedFiles.Select(x => x.ObservationUtc.ToString("O", CultureInfo.InvariantCulture)).ToArray();
            metadata["usedObservationUtc"] = usedFiles.Select(x => x.ObservationUtc.ToString("O", CultureInfo.InvariantCulture)).ToArray();
            metadata["tssToTurbidityFactor"] = TssToTurbidityFactor;
            metadata["conversionFormula"] = "Turbidity_NTU = 0.3671 * TSS_mg/L";
            metadata["conversionModel"] = "KIOST_GOMSO_TSS_TURBIDITY_LINEAR";
            metadata["conversionScope"] = "Project-derived use of a site-specific Gomso Bay empirical TSS-turbidity relation";
            metadata["qualityFiltering"] = "Cloud_or_Ice | Land | AC_Fail | TSS_Fail excluded before aggregation";
            metadata["nominalSpatialResolution"] = "250 m";

            if (selectedFiles.Count > 0)
            {
                metadata["observationUtcStart"] = selectedFiles.Min(x => x.ObservationUtc);
                metadata["observationUtcEnd"] = selectedFiles.Max(x => x.ObservationUtc);
            }

            return metadata;
        }

        private MosaicFile[] SelectFiles(DateTime? requestedDateTime)
        {
            if (_availableFiles.Length <= MaximumAggregationFiles)
                return _availableFiles.ToArray();

            if (!requestedDateTime.HasValue)
                return _availableFiles
                    .OrderByDescending(x => x.ObservationUtc)
                    .Take(MaximumAggregationFiles)
                    .OrderBy(x => x.ObservationUtc)
                    .ToArray();

            var requestedUtc = ToUtc(requestedDateTime.Value);
            return _availableFiles
                .OrderBy(x => AbsoluteTicksDifference(x.ObservationUtc, requestedUtc))
                .Take(MaximumAggregationFiles)
                .OrderBy(x => x.ObservationUtc)
                .ToArray();
        }

        private Goci2TssDataSource GetOrCreateReader(MosaicFile file)
        {
            lock (_readerSync)
            {
                if (_readers.TryGetValue(file.Path, out var existing))
                    return existing;

                var rawOption = new DataSourceOption
                {
                    Id = _option.Id + "__RAW_TSS",
                    Type = EnvironmentType.Turbidity,
                    Format = DataSourceFormat.Goci2Tss,
                    Enabled = true,
                    Path = file.Path,
                    Variable = string.IsNullOrWhiteSpace(_option.Variable) ? "TSS" : _option.Variable,
                    LatitudeVariable = string.IsNullOrWhiteSpace(_option.LatitudeVariable) ? "latitude" : _option.LatitudeVariable,
                    LongitudeVariable = string.IsNullOrWhiteSpace(_option.LongitudeVariable) ? "longitude" : _option.LongitudeVariable,
                    Unit = "g/m^3"
                };

                var reader = new Goci2TssDataSource(rawOption, file.Path);
                _readers.Add(file.Path, reader);
                return reader;
            }
        }

        private static IEnumerable<MosaicFile> ResolveAvailableFiles(string path)
        {
            if (File.Exists(path))
            {
                var utc = ParseObservationUtc(path);
                if (utc.HasValue && IsMosaicFile(path))
                    yield return new MosaicFile(path, utc.Value);
                yield break;
            }

            if (!Directory.Exists(path))
                yield break;

            foreach (var filePath in Directory.EnumerateFiles(path, "*.nc", SearchOption.TopDirectoryOnly))
            {
                var utc = ParseObservationUtc(filePath);
                if (utc.HasValue && IsMosaicFile(filePath))
                    yield return new MosaicFile(filePath, utc.Value);
            }
        }

        private static bool IsMosaicFile(string path)
        {
            return MosaicFileRegex.IsMatch(Path.GetFileName(path));
        }

        private static DateTime? ParseObservationUtc(string path)
        {
            var match = MosaicFileRegex.Match(Path.GetFileName(path));
            if (!match.Success)
                return null;

            if (DateTime.TryParseExact(
                match.Groups["date"].Value + match.Groups["time"].Value,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
                return value;
            return null;
        }

        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static long AbsoluteTicksDifference(DateTime left, DateTime right)
        {
            var delta = left.Ticks - right.Ticks;
            if (delta == long.MinValue)
                return long.MaxValue;
            return Math.Abs(delta);
        }

        private static double Median(double[] values)
        {
            if (values == null || values.Length == 0)
                throw new ArgumentException("At least one value is required.", nameof(values));
            Array.Sort(values);
            return MedianFromSorted(values, values.Length);
        }

        private static double MedianFromSorted(double[] values, int count)
        {
            var middle = count / 2;
            return (count & 1) == 1
                ? values[middle]
                : (values[middle - 1] + values[middle]) * 0.5;
        }

        private static double[] BuildDescendingAxis(double max, double min, int count)
        {
            var result = new double[count];
            for (var i = 0; i < count; i++)
                result[i] = max + ((min - max) * (i / (double)(count - 1)));
            return result;
        }

        private static double[] BuildAscendingAxis(double min, double max, int count)
        {
            var result = new double[count];
            for (var i = 0; i < count; i++)
                result[i] = min + ((max - min) * (i / (double)(count - 1)));
            return result;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(Goci2TurbidityDataSource));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            lock (_readerSync)
            {
                if (_disposed)
                    return;
                foreach (var reader in _readers.Values)
                    reader.Dispose();
                _readers.Clear();
                _disposed = true;
            }
        }

        private readonly struct TssSample
        {
            public TssSample(MosaicFile file, double tssGm3)
            {
                File = file;
                TssGm3 = tssGm3;
            }

            public MosaicFile File { get; }
            public double TssGm3 { get; }
        }

        private readonly struct MosaicFile
        {
            public MosaicFile(string path, DateTime observationUtc)
            {
                Path = path;
                ObservationUtc = observationUtc;
            }

            public string Path { get; }
            public DateTime ObservationUtc { get; }
        }
    }
}
