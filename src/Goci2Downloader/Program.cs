using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Goci2Downloader
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                var options = DownloaderOptions.Parse(args);
                if (options.ShowHelp)
                {
                    DownloaderOptions.PrintHelp();
                    return 0;
                }

                var serviceKey = options.ServiceKey;
                if (string.IsNullOrWhiteSpace(serviceKey))
                    serviceKey = Environment.GetEnvironmentVariable("NOSC_SERVICE_KEY");

                if (string.IsNullOrWhiteSpace(serviceKey))
                {
                    Console.Error.WriteLine("NOSC API key is required. Use --service-key or set NOSC_SERVICE_KEY.");
                    return 2;
                }

                Directory.CreateDirectory(options.OutputDirectory);

                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                };
                using var http = new HttpClient(handler)
                {
                    Timeout = Timeout.InfiniteTimeSpan
                };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MarineEnvironment-Goci2Downloader/1.0");

                var client = new NoscClient(http, options.ApiEndpoint, serviceKey);
                var files = await client.FindLatestTssMosaicsAsync(
                    options.AsOfKstDate,
                    options.LookbackDays,
                    options.KeepCount,
                    CancellationToken.None).ConfigureAwait(false);

                if (files.Count == 0)
                {
                    Console.Error.WriteLine("No GOCI-II LA TSS mosaic was found in the requested date range.");
                    return 3;
                }

                Console.WriteLine($"Found {files.Count} TSS mosaic(s) selected for retention:");
                foreach (var file in files.OrderByDescending(x => x.ObservationUtc))
                    Console.WriteLine($"  {file.ObservationUtc:yyyy-MM-dd HH:mm:ss} UTC  {file.FileName}");

                if (options.DryRun)
                {
                    Console.WriteLine("Dry-run: no files were downloaded or deleted.");
                    return 0;
                }

                var downloader = new NetCdfDownloader(http, options.OutputDirectory);
                var downloaded = new List<DownloadRecord>();
                foreach (var file in files.OrderBy(x => x.ObservationUtc))
                {
                    var result = await downloader.EnsureDownloadedAsync(file, CancellationToken.None).ConfigureAwait(false);
                    downloaded.Add(result);
                }

                if (!options.NoPrune)
                    Retention.PruneOldMosaics(options.OutputDirectory, options.KeepCount);

                ManifestWriter.Write(options.OutputDirectory, downloaded);
                Console.WriteLine("Completed.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }
    }

    internal sealed class DownloaderOptions
    {
        public string? ServiceKey { get; private set; }
        public string OutputDirectory { get; private set; } = Path.GetFullPath("GOCI2_TSS");
        public int KeepCount { get; private set; } = 5;
        public int LookbackDays { get; private set; } = 7;
        public DateTime? AsOfKstDate { get; private set; }
        public bool DryRun { get; private set; }
        public bool NoPrune { get; private set; }
        public bool ShowHelp { get; private set; }
        public string ApiEndpoint { get; private set; } = "https://nosc.go.kr/openapi/GK2BNcMedia/search.do";

        public static DownloaderOptions Parse(string[] args)
        {
            var result = new DownloaderOptions();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        result.ShowHelp = true;
                        break;
                    case "--service-key":
                        result.ServiceKey = RequireValue(args, ref i, arg);
                        break;
                    case "--output":
                        result.OutputDirectory = Path.GetFullPath(RequireValue(args, ref i, arg));
                        break;
                    case "--keep":
                        result.KeepCount = ParseRange(RequireValue(args, ref i, arg), arg, 1, 20);
                        break;
                    case "--lookback-days":
                        result.LookbackDays = ParseRange(RequireValue(args, ref i, arg), arg, 1, 31);
                        break;
                    case "--as-of":
                        var text = RequireValue(args, ref i, arg);
                        if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                            throw new ArgumentException("--as-of must use yyyy-MM-dd.");
                        result.AsOfKstDate = date.Date;
                        break;
                    case "--dry-run":
                        result.DryRun = true;
                        break;
                    case "--no-prune":
                        result.NoPrune = true;
                        break;
                    case "--api-endpoint":
                        result.ApiEndpoint = RequireValue(args, ref i, arg);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {arg}");
                }
            }

            return result;
        }

        public static void PrintHelp()
        {
            Console.WriteLine("Goci2Downloader - download recent GOCI-II L2 LA TSS mosaic NetCDF files from NOSC");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  Goci2Downloader --output <dir> [--keep 5] [--lookback-days 7]");
            Console.WriteLine();
            Console.WriteLine("Authentication:");
            Console.WriteLine("  --service-key <key>        NOSC OPEN API key");
            Console.WriteLine("  or environment variable NOSC_SERVICE_KEY");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --output <dir>             Output directory (default: ./GOCI2_TSS)");
            Console.WriteLine("  --keep <1..20>             Keep newest mosaic files (default: 5)");
            Console.WriteLine("  --lookback-days <1..31>    Search backwards by KST date (default: 7)");
            Console.WriteLine("  --as-of yyyy-MM-dd         Historical test date instead of current KST date");
            Console.WriteLine("  --dry-run                  Query/list only; do not download or delete");
            Console.WriteLine("  --no-prune                 Do not remove older local mosaic files");
            Console.WriteLine("  --api-endpoint <url>       Override NOSC NetCDF information API endpoint");
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            return args[++index];
        }

        private static int ParseRange(string text, string option, int minimum, int maximum)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                || value < minimum || value > maximum)
                throw new ArgumentException($"{option} must be between {minimum} and {maximum}.");
            return value;
        }
    }

    internal sealed class NoscClient
    {
        private static readonly Regex MosaicTssRegex = new Regex(
            @"^GK2B_GOCI2_L2_(?<date>\d{8})_(?<time>\d{6})_LA_TSS\.nc$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly HttpClient _http;
        private readonly string _endpoint;
        private readonly string _serviceKey;

        public NoscClient(HttpClient http, string endpoint, string serviceKey)
        {
            _http = http;
            _endpoint = endpoint;
            _serviceKey = serviceKey;
        }

        public async Task<IReadOnlyList<NoscFile>> FindLatestTssMosaicsAsync(
            DateTime? asOfKstDate,
            int lookbackDays,
            int count,
            CancellationToken cancellationToken)
        {
            var kstToday = (asOfKstDate ?? DateTime.UtcNow.AddHours(9.0)).Date;
            var found = new Dictionary<string, NoscFile>(StringComparer.OrdinalIgnoreCase);

            for (var offset = 0; offset < lookbackDays && found.Count < count; offset++)
            {
                var date = kstToday.AddDays(-offset);
                Console.WriteLine($"Query NOSC: {date:yyyy-MM-dd} KST, slot=13 (mosaic)");
                var dayFiles = await QueryDayAsync(date, cancellationToken).ConfigureAwait(false);
                foreach (var file in dayFiles)
                {
                    if (!found.ContainsKey(file.FileName))
                        found.Add(file.FileName, file);
                }
            }

            return found.Values
                .OrderByDescending(x => x.ObservationUtc)
                .Take(count)
                .ToArray();
        }

        private async Task<IReadOnlyList<NoscFile>> QueryDayAsync(DateTime kstDate, CancellationToken cancellationToken)
        {
            var date = kstDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var url = _endpoint
                + "?ServiceKey=" + Uri.EscapeDataString(_serviceKey)
                + "&startDate=" + date
                + "&endDate=" + date
                + "&slot=13&ResultType=json";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"NOSC API returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var resultCode = GetScalar(root, "resultCode");
            if (!string.IsNullOrWhiteSpace(resultCode) && resultCode != "200")
            {
                var message = GetScalar(root, "resultMsg") ?? "Unknown NOSC API error";
                throw new InvalidDataException($"NOSC API error {resultCode}: {message}");
            }

            if (!TryGetProperty(root, "data", out var data) || data.ValueKind != JsonValueKind.Array)
                return Array.Empty<NoscFile>();

            var files = new List<NoscFile>();
            foreach (var item in data.EnumerateArray())
            {
                var fileName = GetScalar(item, "fileName");
                var filePath = GetScalar(item, "filePath");
                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(filePath))
                    continue;

                var match = MosaicTssRegex.Match(fileName);
                if (!match.Success)
                    continue;

                var product = GetScalar(item, "product");
                if (!string.IsNullOrWhiteSpace(product)
                    && !string.Equals(product, "TSS", StringComparison.OrdinalIgnoreCase))
                    continue;

                var observationUtc = ParseObservationUtc(item, match);
                files.Add(new NoscFile(fileName, filePath, observationUtc));
            }

            return files;
        }

        private static DateTime ParseObservationUtc(JsonElement item, Match fileNameMatch)
        {
            var text = GetScalar(item, "obsTimeUTC") ?? GetScalar(item, "ObsTime(UTC)");
            if (!string.IsNullOrWhiteSpace(text))
            {
                var formats = new[]
                {
                    "yyyy-MM-dd HH:mm:ss",
                    "yyyy-MM-dd HH:mm:ss 'UTC'",
                    "yyyyMMddHHmmss"
                };
                if (DateTime.TryParseExact(
                    text.Trim(),
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                    return parsed;
            }

            var stamp = fileNameMatch.Groups["date"].Value + fileNameMatch.Groups["time"].Value;
            if (DateTime.TryParseExact(
                stamp,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var fromName))
                return fromName;

            throw new InvalidDataException("Unable to parse GOCI-II observation time.");
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string? GetScalar(JsonElement element, string name)
        {
            if (!TryGetProperty(element, name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }
    }

    internal sealed class NetCdfDownloader
    {
        private readonly HttpClient _http;
        private readonly string _outputDirectory;

        public NetCdfDownloader(HttpClient http, string outputDirectory)
        {
            _http = http;
            _outputDirectory = outputDirectory;
        }

        public async Task<DownloadRecord> EnsureDownloadedAsync(NoscFile file, CancellationToken cancellationToken)
        {
            var destination = Path.Combine(_outputDirectory, file.FileName);
            if (File.Exists(destination) && NetCdfSignature.IsFileNetCdf(destination))
            {
                Console.WriteLine($"Already present: {file.FileName}");
                return new DownloadRecord(file.FileName, file.ObservationUtc, file.FilePath, file.FilePath, false, new FileInfo(destination).Length);
            }

            if (File.Exists(destination))
                File.Delete(destination);

            var candidates = BuildCandidateUrls(file.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var errors = new List<string>();
            foreach (var candidate in candidates)
            {
                try
                {
                    var length = await DownloadCandidateAsync(candidate, destination, file.FileName, cancellationToken).ConfigureAwait(false);
                    return new DownloadRecord(file.FileName, file.ObservationUtc, file.FilePath, candidate, true, length);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    errors.Add(candidate + " -> " + ex.Message);
                    DeletePart(destination);
                }
            }

            throw new IOException(
                $"Unable to download {file.FileName}. NOSC filePath was discovered successfully, but no tested download form returned a NetCDF payload. "
                + string.Join(" | ", errors));
        }

        private async Task<long> DownloadCandidateAsync(
            string url,
            string destination,
            string displayName,
            CancellationToken cancellationToken)
        {
            Console.WriteLine($"Download: {displayName}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var header = new byte[8];
            var headerCount = await ReadPrefixAsync(input, header, cancellationToken).ConfigureAwait(false);
            if (!NetCdfSignature.IsNetCdf(header, headerCount))
            {
                var previewBytes = new byte[512];
                var previewCount = await input.ReadAsync(previewBytes, 0, previewBytes.Length, cancellationToken).ConfigureAwait(false);
                var preview = Encoding.UTF8.GetString(previewBytes, 0, previewCount).Replace('\r', ' ').Replace('\n', ' ');
                throw new InvalidDataException($"Response is not NetCDF (Content-Type={response.Content.Headers.ContentType}); preview={preview}");
            }

            var temporary = destination + ".part";
            DeletePart(destination);
            const int bufferSize = 1024 * 1024;
            var buffer = new byte[bufferSize];
            long total = 0;
            long nextReport = 256L * 1024L * 1024L;

            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true))
            {
                await output.WriteAsync(header, 0, headerCount, cancellationToken).ConfigureAwait(false);
                total += headerCount;

                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    total += read;

                    if (total >= nextReport)
                    {
                        Console.WriteLine($"  {total / (1024.0 * 1024.0):0} MiB received");
                        nextReport += 256L * 1024L * 1024L;
                    }
                }
            }

            if (!NetCdfSignature.IsFileNetCdf(temporary))
                throw new InvalidDataException("Downloaded file failed NetCDF signature validation.");

            if (File.Exists(destination))
                File.Delete(destination);
            File.Move(temporary, destination);
            Console.WriteLine($"Saved: {destination} ({total / (1024.0 * 1024.0):0.0} MiB)");
            return total;
        }

        private static IEnumerable<string> BuildCandidateUrls(string apiFilePath)
        {
            var normalized = apiFilePath.Trim();
            if (normalized.StartsWith("http://nosc.go.kr/", StringComparison.OrdinalIgnoreCase))
                normalized = "https://nosc.go.kr/" + normalized.Substring("http://nosc.go.kr/".Length);
            else if (normalized.StartsWith("http://www.nosc.go.kr/", StringComparison.OrdinalIgnoreCase))
                normalized = "https://www.nosc.go.kr/" + normalized.Substring("http://www.nosc.go.kr/".Length);

            // NOSC documents filePath as an OPeNDAP dataset URL. On a Hyrax server with direct
            // source access enabled, the unadorned URL returns the original NetCDF file.
            yield return normalized;

            // If direct source access is disabled, Hyrax may still expose the NetCDF-4 file-out
            // service. This reconstructs the full DAP4 dataset as a NetCDF-4 response.
            if (!normalized.EndsWith(".nc4", StringComparison.OrdinalIgnoreCase))
                yield return normalized + ".nc4";
        }

        private static async Task<int> ReadPrefixAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;
                total += read;
            }
            return total;
        }

        private static void DeletePart(string destination)
        {
            var part = destination + ".part";
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    internal static class NetCdfSignature
    {
        private static readonly byte[] Hdf5 = { 0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool IsFileNetCdf(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var prefix = new byte[8];
                var count = stream.Read(prefix, 0, prefix.Length);
                return IsNetCdf(prefix, count);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsNetCdf(byte[] prefix, int count)
        {
            if (count >= 4 && prefix[0] == (byte)'C' && prefix[1] == (byte)'D' && prefix[2] == (byte)'F')
                return prefix[3] == 1 || prefix[3] == 2 || prefix[3] == 5;

            if (count >= Hdf5.Length)
            {
                for (var i = 0; i < Hdf5.Length; i++)
                {
                    if (prefix[i] != Hdf5[i])
                        return false;
                }
                return true;
            }

            return false;
        }
    }

    internal static class Retention
    {
        private static readonly Regex MosaicTssRegex = new Regex(
            @"^GK2B_GOCI2_L2_(?<date>\d{8})_(?<time>\d{6})_LA_TSS\.nc$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static void PruneOldMosaics(string directory, int keepCount)
        {
            var candidates = new List<(string Path, DateTime Utc)>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.nc", SearchOption.TopDirectoryOnly))
            {
                var match = MosaicTssRegex.Match(Path.GetFileName(path));
                if (!match.Success)
                    continue;

                var stamp = match.Groups["date"].Value + match.Groups["time"].Value;
                if (DateTime.TryParseExact(
                    stamp,
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utc))
                    candidates.Add((path, utc));
            }

            foreach (var old in candidates.OrderByDescending(x => x.Utc).Skip(keepCount))
            {
                File.Delete(old.Path);
                Console.WriteLine($"Pruned: {Path.GetFileName(old.Path)}");
            }
        }
    }

    internal static class ManifestWriter
    {
        public static void Write(string outputDirectory, IReadOnlyList<DownloadRecord> records)
        {
            var manifest = new
            {
                generatedUtc = DateTime.UtcNow,
                note = "GOCI-II TSS source files retained for offline MarineEnvironment ingestion. API keys are never stored.",
                files = records.OrderByDescending(x => x.ObservationUtc).Select(x => new
                {
                    x.FileName,
                    x.ObservationUtc,
                    sourceFilePath = x.SourceFilePath,
                    actualDownloadUrl = x.DownloadUrl,
                    x.Downloaded,
                    x.Bytes
                }).ToArray()
            };

            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(outputDirectory, "goci2-download-manifest.json"), json, new UTF8Encoding(false));
        }
    }

    internal sealed class NoscFile
    {
        public NoscFile(string fileName, string filePath, DateTime observationUtc)
        {
            FileName = fileName;
            FilePath = filePath;
            ObservationUtc = observationUtc;
        }

        public string FileName { get; }
        public string FilePath { get; }
        public DateTime ObservationUtc { get; }
    }

    internal sealed class DownloadRecord
    {
        public DownloadRecord(
            string fileName,
            DateTime observationUtc,
            string sourceFilePath,
            string downloadUrl,
            bool downloaded,
            long bytes)
        {
            FileName = fileName;
            ObservationUtc = observationUtc;
            SourceFilePath = sourceFilePath;
            DownloadUrl = downloadUrl;
            Downloaded = downloaded;
            Bytes = bytes;
        }

        public string FileName { get; }
        public DateTime ObservationUtc { get; }
        public string SourceFilePath { get; }
        public string DownloadUrl { get; }
        public bool Downloaded { get; }
        public long Bytes { get; }
    }
}
