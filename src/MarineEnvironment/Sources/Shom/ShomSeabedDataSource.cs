using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;

namespace MarineEnvironment.Sources.Shom
{
    internal sealed class ShomSeabedDataSource : IEnvironmentDataSource
    {
        private const double SpatialCellSizeDegrees = 2.0;
        private const int SpatialColumns = 180;
        private const int SpatialRows = 90;

        private readonly DataSourceOption _option;
        private readonly string _shapefilePath;
        private readonly List<SeabedFeature> _features = new List<SeabedFeature>();
        private readonly Dictionary<int, List<SeabedFeature>> _spatialIndex = new Dictionary<int, List<SeabedFeature>>();

        public ShomSeabedDataSource(DataSourceOption option, string resolvedPath)
        {
            _option = option;
            _shapefilePath = ResolveShapefilePath(resolvedPath);

            if (!option.Enabled)
            {
                Status = SourceStatus.Disabled;
                return;
            }

            try
            {
                ValidateShapefileComponents(_shapefilePath);
                LoadFeatures();

                if (_features.Count == 0)
                    throw new InvalidDataException("The SHOM shapefile contains no supported sediment polygon features.");

                BuildSpatialIndex();
                Status = SourceStatus.Ready;
                StatusMessage = $"{_features.Count:N0} polygon(s) / field '{_option.AttributeField}' / built-in SHP reader";
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
        public EnvironmentType Type => EnvironmentType.Seabed;
        public SourceStatus Status { get; private set; } = SourceStatus.NotInitialized;
        public string? StatusMessage { get; private set; }

        public EnvironmentValue? Query(EnvironmentQuery query)
        {
            if (Status != SourceStatus.Ready)
                return null;

            var feature = FindFeature(query.Longitude, query.Latitude);
            if (feature == null)
                return null;

            var value = new SeabedValue
            {
                Code = feature.Definition.Code,
                Name = feature.Definition.Name,
                SedimentClass = feature.Definition.SedimentClass,
                SourceMapNumber = feature.SourceMapNumber
            };

            var metadata = CreateMetadata();
            metadata["shomCode"] = feature.Definition.Code;
            metadata["shomName"] = feature.Definition.Name;
            metadata["sedimentClass"] = feature.Definition.SedimentClass.ToString();
            if (feature.SourceMapNumber.HasValue)
                metadata["sourceMapNumber"] = feature.SourceMapNumber.Value;

            return new EnvironmentValue(
                Id,
                EnvironmentType.Seabed,
                value,
                null,
                query.Latitude,
                query.Longitude,
                null,
                null,
                _option.AttributeField,
                metadata);
        }

        public GridResult QueryGrid(GridQuery query)
        {
            if (Status != SourceStatus.Ready)
                throw new InvalidOperationException($"Source '{Id}' is not ready: {Status} - {StatusMessage}");
            if (query.Width < 2 || query.Height < 2)
                throw new ArgumentOutOfRangeException(nameof(query), "SHOM grid width and height must both be at least 2.");

            var latitudes = new double[query.Height];
            var longitudes = new double[query.Width];
            var values = new double?[query.Width * query.Height];
            var labels = new string?[values.Length];

            for (var row = 0; row < query.Height; row++)
            {
                var t = row / (double)(query.Height - 1);
                latitudes[row] = query.MaxLatitude + ((query.MinLatitude - query.MaxLatitude) * t);
            }

            for (var column = 0; column < query.Width; column++)
            {
                var t = column / (double)(query.Width - 1);
                longitudes[column] = query.MinLongitude + ((query.MaxLongitude - query.MinLongitude) * t);
            }

            double? minimum = null;
            double? maximum = null;
            for (var row = 0; row < query.Height; row++)
            {
                var latitude = latitudes[row];
                for (var column = 0; column < query.Width; column++)
                {
                    var longitude = longitudes[column];
                    var feature = FindFeature(longitude, latitude);
                    if (feature == null)
                        continue;

                    var outputIndex = (row * query.Width) + column;
                    var category = feature.Definition.Index;
                    values[outputIndex] = category;
                    labels[outputIndex] = $"{feature.Definition.Code} / {feature.Definition.Name}";
                    minimum = !minimum.HasValue ? category : Math.Min(minimum.Value, category);
                    maximum = !maximum.HasValue ? category : Math.Max(maximum.Value, category);
                }
            }

            var metadata = CreateMetadata();
            metadata["rasterValue"] = "SHOM categorical sediment code";
            metadata["categoryCount"] = ShomSedimentCatalog.All.Count;
            metadata["requestedBounds"] = new[] { query.MinLatitude, query.MaxLatitude, query.MinLongitude, query.MaxLongitude };
            metadata["sourceBounds"] = new[]
            {
                _features.Min(x => x.MinY),
                _features.Max(x => x.MaxY),
                _features.Min(x => x.MinX),
                _features.Max(x => x.MaxX)
            };

            return new GridResult
            {
                SourceId = Id,
                Type = EnvironmentType.Seabed,
                Width = query.Width,
                Height = query.Height,
                Latitudes = latitudes,
                Longitudes = longitudes,
                Values = values,
                Labels = labels,
                Unit = null,
                DateTime = null,
                Variable = _option.AttributeField,
                Minimum = minimum,
                Maximum = maximum,
                Metadata = metadata
            };
        }

        private void LoadFeatures()
        {
            var dbfRecords = ReadDbf(Path.ChangeExtension(_shapefilePath, ".dbf"), _option.AttributeField);
            using var stream = File.OpenRead(_shapefilePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 100)
                throw new InvalidDataException("Invalid SHOM .shp file: header is shorter than 100 bytes.");

            var fileCode = ReadInt32BigEndian(reader);
            if (fileCode != 9994)
                throw new InvalidDataException($"Invalid SHOM .shp file code: {fileCode}.");

            stream.Position = 24;
            var fileLengthWords = ReadInt32BigEndian(reader);
            var declaredFileLength = fileLengthWords * 2L;
            stream.Position = 28;
            var version = reader.ReadInt32();
            var headerShapeType = reader.ReadInt32();
            if (version != 1000)
                throw new InvalidDataException($"Unsupported SHP version: {version}.");
            if (!IsSupportedPolygonShapeType(headerShapeType))
                throw new InvalidDataException($"SHOM shapefile shape type {headerShapeType} is not a Polygon/PolygonZ/PolygonM dataset.");

            stream.Position = 100;
            var recordIndex = 0;
            var logicalEnd = Math.Min(stream.Length, declaredFileLength > 0 ? declaredFileLength : stream.Length);

            while (stream.Position + 8 <= logicalEnd)
            {
                ReadInt32BigEndian(reader); // record number
                var contentLengthWords = ReadInt32BigEndian(reader);
                if (contentLengthWords < 2)
                    throw new InvalidDataException("Invalid SHP record length.");

                var contentBytes = contentLengthWords * 2L;
                var contentStart = stream.Position;
                var contentEnd = contentStart + contentBytes;
                if (contentEnd > stream.Length)
                    throw new InvalidDataException("SHP record extends past end of file.");

                var shapeType = reader.ReadInt32();
                if (shapeType == 0)
                {
                    stream.Position = contentEnd;
                    recordIndex++;
                    continue;
                }

                if (!IsSupportedPolygonShapeType(shapeType))
                {
                    stream.Position = contentEnd;
                    recordIndex++;
                    continue;
                }

                var minX = reader.ReadDouble();
                var minY = reader.ReadDouble();
                var maxX = reader.ReadDouble();
                var maxY = reader.ReadDouble();
                var partCount = reader.ReadInt32();
                var pointCount = reader.ReadInt32();

                if (partCount <= 0 || pointCount <= 0 || partCount > pointCount)
                    throw new InvalidDataException($"Invalid SHP polygon record {recordIndex + 1}: parts={partCount}, points={pointCount}.");

                var parts = new int[partCount];
                for (var i = 0; i < partCount; i++)
                    parts[i] = reader.ReadInt32();

                var x = new double[pointCount];
                var y = new double[pointCount];
                for (var i = 0; i < pointCount; i++)
                {
                    x[i] = reader.ReadDouble();
                    y[i] = reader.ReadDouble();
                }

                stream.Position = contentEnd; // PolygonZ/M extra arrays are not needed for 2D point-in-polygon.

                if (recordIndex < dbfRecords.Count)
                {
                    var attributes = dbfRecords[recordIndex];
                    if (!attributes.Deleted && ShomSedimentCatalog.TryGet(attributes.Code, out var definition))
                    {
                        _features.Add(new SeabedFeature(minX, minY, maxX, maxY, parts, x, y, definition, attributes.SourceMapNumber));
                    }
                }

                recordIndex++;
            }
        }

        private void BuildSpatialIndex()
        {
            foreach (var feature in _features)
            {
                var minColumn = LongitudeCell(feature.MinX);
                var maxColumn = LongitudeCell(feature.MaxX);
                var minRow = LatitudeCell(feature.MinY);
                var maxRow = LatitudeCell(feature.MaxY);

                for (var row = minRow; row <= maxRow; row++)
                {
                    for (var column = minColumn; column <= maxColumn; column++)
                    {
                        var key = (row * SpatialColumns) + column;
                        if (!_spatialIndex.TryGetValue(key, out var bucket))
                        {
                            bucket = new List<SeabedFeature>();
                            _spatialIndex[key] = bucket;
                        }
                        bucket.Add(feature);
                    }
                }
            }
        }

        private SeabedFeature? FindFeature(double longitude, double latitude)
        {
            if (longitude < -180 || longitude > 180 || latitude < -90 || latitude > 90)
                return null;

            var key = (LatitudeCell(latitude) * SpatialColumns) + LongitudeCell(longitude);
            if (!_spatialIndex.TryGetValue(key, out var candidates))
                return null;

            foreach (var candidate in candidates)
            {
                if (!candidate.EnvelopeContains(longitude, latitude))
                    continue;
                if (candidate.Contains(longitude, latitude))
                    return candidate;
            }

            return null;
        }

        private Dictionary<string, object?> CreateMetadata()
        {
            var metadata = _option.Metadata != null
                ? _option.Metadata.ToDictionary(x => x.Key, x => (object?)x.Value)
                : new Dictionary<string, object?>();
            metadata["dataset"] = "SHOM Worldwide Seabed Sediment Map";
            metadata["format"] = "ESRI Shapefile";
            metadata["reader"] = "MarineEnvironment built-in SHP/DBF reader";
            metadata["spatialReference"] = "EPSG:4326 / WGS84";
            metadata["classificationField"] = _option.AttributeField;
            metadata["file"] = _shapefilePath;
            return metadata;
        }

        private static List<DbfRecord> ReadDbf(string dbfPath, string codeFieldName)
        {
            using var stream = File.OpenRead(dbfPath);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 32)
                throw new InvalidDataException("Invalid SHOM .dbf file.");

            reader.ReadByte(); // version
            reader.ReadBytes(3); // YY MM DD
            var recordCount = reader.ReadInt32();
            var headerLength = reader.ReadUInt16();
            var recordLength = reader.ReadUInt16();
            stream.Position = 32;

            var fields = new List<DbfField>();
            while (stream.Position < headerLength)
            {
                var first = reader.ReadByte();
                if (first == 0x0D)
                    break;

                var descriptor = new byte[32];
                descriptor[0] = first;
                var remaining = reader.ReadBytes(31);
                if (remaining.Length != 31)
                    throw new EndOfStreamException("Unexpected end of DBF field descriptor.");
                Buffer.BlockCopy(remaining, 0, descriptor, 1, 31);

                var nameLength = 0;
                while (nameLength < 11 && descriptor[nameLength] != 0)
                    nameLength++;
                var name = Encoding.ASCII.GetString(descriptor, 0, nameLength).Trim();
                var type = (char)descriptor[11];
                var length = descriptor[16];
                fields.Add(new DbfField(name, type, length));
            }

            var codeField = fields.FirstOrDefault(x => string.Equals(x.Name, codeFieldName, StringComparison.OrdinalIgnoreCase));
            if (codeField == null)
                throw new InvalidDataException($"DBF field '{codeFieldName}' was not found. Available fields: {string.Join(", ", fields.Select(x => x.Name))}");

            var numeroField = fields.FirstOrDefault(x => string.Equals(x.Name, "Numero", StringComparison.OrdinalIgnoreCase));
            stream.Position = headerLength;
            var records = new List<DbfRecord>(Math.Max(0, recordCount));

            for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
            {
                if (stream.Position + recordLength > stream.Length)
                    break;

                var recordStart = stream.Position;
                var deleted = reader.ReadByte() == 0x2A;
                string? code = null;
                int? sourceMapNumber = null;

                foreach (var field in fields)
                {
                    var bytes = reader.ReadBytes(field.Length);
                    if (bytes.Length != field.Length)
                        throw new EndOfStreamException("Unexpected end of DBF record.");
                    var text = Encoding.ASCII.GetString(bytes).Trim().Trim('\0');

                    if (ReferenceEquals(field, codeField))
                        code = text;
                    else if (numeroField != null && ReferenceEquals(field, numeroField)
                             && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
                        sourceMapNumber = parsedNumber;
                }

                records.Add(new DbfRecord(deleted, code, sourceMapNumber));
                stream.Position = recordStart + recordLength;
            }

            return records;
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            if (bytes.Length != 4)
                throw new EndOfStreamException();
            return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        }

        private static bool IsSupportedPolygonShapeType(int shapeType)
            => shapeType == 5 || shapeType == 15 || shapeType == 25;

        private static int LongitudeCell(double longitude)
        {
            var cell = (int)Math.Floor((longitude + 180.0) / SpatialCellSizeDegrees);
            return Math.Max(0, Math.Min(SpatialColumns - 1, cell));
        }

        private static int LatitudeCell(double latitude)
        {
            var cell = (int)Math.Floor((latitude + 90.0) / SpatialCellSizeDegrees);
            return Math.Max(0, Math.Min(SpatialRows - 1, cell));
        }

        private static string ResolveShapefilePath(string path)
        {
            if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".shp", StringComparison.OrdinalIgnoreCase))
                return path;

            if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.shp", SearchOption.TopDirectoryOnly);
                if (files.Length == 1)
                    return files[0];
                if (files.Length == 0)
                    throw new FileNotFoundException("No .shp file was found in the SHOM source directory.", path);
                throw new InvalidDataException($"Multiple .shp files were found in '{path}'. Configure the exact SHOM .shp path.");
            }

            throw new FileNotFoundException("SHOM shapefile was not found.", path);
        }

        private static void ValidateShapefileComponents(string shpPath)
        {
            foreach (var extension in new[] { ".shp", ".shx", ".dbf" })
            {
                var path = Path.ChangeExtension(shpPath, extension);
                if (!File.Exists(path))
                    throw new FileNotFoundException($"Required SHOM shapefile component '{extension}' was not found.", path);
            }
        }

        public void Dispose() { }

        private sealed class SeabedFeature
        {
            public SeabedFeature(
                double minX, double minY, double maxX, double maxY,
                int[] parts, double[] x, double[] y,
                ShomSedimentDefinition definition, int? sourceMapNumber)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
                Parts = parts;
                X = x;
                Y = y;
                Definition = definition;
                SourceMapNumber = sourceMapNumber;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public int[] Parts { get; }
            public double[] X { get; }
            public double[] Y { get; }
            public ShomSedimentDefinition Definition { get; }
            public int? SourceMapNumber { get; }

            public bool EnvelopeContains(double x, double y)
                => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

            public bool Contains(double px, double py)
            {
                var inside = false;
                for (var part = 0; part < Parts.Length; part++)
                {
                    var start = Parts[part];
                    var end = part + 1 < Parts.Length ? Parts[part + 1] : X.Length;
                    if (end - start < 3)
                        continue;

                    if (RingContains(px, py, start, end, out var onBoundary))
                        inside = !inside;
                    if (onBoundary)
                        return true;
                }
                return inside;
            }

            private bool RingContains(double px, double py, int start, int end, out bool onBoundary)
            {
                onBoundary = false;
                var inside = false;
                var j = end - 1;
                for (var i = start; i < end; i++)
                {
                    var xi = X[i];
                    var yi = Y[i];
                    var xj = X[j];
                    var yj = Y[j];

                    if (PointOnSegment(px, py, xj, yj, xi, yi))
                    {
                        onBoundary = true;
                        return false;
                    }

                    var intersects = ((yi > py) != (yj > py))
                                     && (px < ((xj - xi) * (py - yi) / (yj - yi)) + xi);
                    if (intersects)
                        inside = !inside;
                    j = i;
                }
                return inside;
            }

            private static bool PointOnSegment(double px, double py, double ax, double ay, double bx, double by)
            {
                const double tolerance = 1e-10;
                var cross = ((px - ax) * (by - ay)) - ((py - ay) * (bx - ax));
                if (Math.Abs(cross) > tolerance)
                    return false;
                var minX = Math.Min(ax, bx) - tolerance;
                var maxX = Math.Max(ax, bx) + tolerance;
                var minY = Math.Min(ay, by) - tolerance;
                var maxY = Math.Max(ay, by) + tolerance;
                return px >= minX && px <= maxX && py >= minY && py <= maxY;
            }
        }

        private sealed class DbfField
        {
            public DbfField(string name, char type, int length)
            {
                Name = name;
                Type = type;
                Length = length;
            }
            public string Name { get; }
            public char Type { get; }
            public int Length { get; }
        }

        private sealed class DbfRecord
        {
            public DbfRecord(bool deleted, string? code, int? sourceMapNumber)
            {
                Deleted = deleted;
                Code = code;
                SourceMapNumber = sourceMapNumber;
            }
            public bool Deleted { get; }
            public string? Code { get; }
            public int? SourceMapNumber { get; }
        }
    }
}
