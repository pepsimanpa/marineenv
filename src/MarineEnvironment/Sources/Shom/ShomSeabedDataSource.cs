using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.IO.Esri;

namespace MarineEnvironment.Sources.Shom
{
    internal sealed class ShomSeabedDataSource : IEnvironmentDataSource
    {
        private readonly DataSourceOption _option;
        private readonly string _shapefilePath;
        private readonly GeometryFactory _geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        private readonly STRtree<SeabedFeature> _index = new STRtree<SeabedFeature>();
        private readonly List<SeabedFeature> _features = new List<SeabedFeature>();

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
                _index.Build();

                if (_features.Count == 0)
                    throw new InvalidDataException("The SHOM shapefile contains no polygon features.");

                Status = SourceStatus.Ready;
                StatusMessage = $"{_features.Count:N0} polygon(s) / field '{_option.AttributeField}'";
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
                _features.Min(x => x.Geometry.EnvelopeInternal.MinY),
                _features.Max(x => x.Geometry.EnvelopeInternal.MaxY),
                _features.Min(x => x.Geometry.EnvelopeInternal.MinX),
                _features.Max(x => x.Geometry.EnvelopeInternal.MaxX)
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
            foreach (var feature in Shapefile.ReadAllFeatures(_shapefilePath))
            {
                if (feature.Geometry == null || feature.Geometry.IsEmpty)
                    continue;
                if (!(feature.Geometry is Polygon) && !(feature.Geometry is MultiPolygon))
                    continue;

                var code = ReadAttribute(feature.Attributes, _option.AttributeField)?.ToString()?.Trim();
                if (!ShomSedimentCatalog.TryGet(code, out var definition))
                    continue;

                int? sourceMapNumber = null;
                var numero = ReadAttribute(feature.Attributes, "Numero");
                if (numero != null && int.TryParse(numero.ToString(), out var parsedNumber))
                    sourceMapNumber = parsedNumber;

                var item = new SeabedFeature(feature.Geometry, definition, sourceMapNumber);
                _features.Add(item);
                _index.Insert(item.Geometry.EnvelopeInternal, item);
            }
        }

        private SeabedFeature? FindFeature(double longitude, double latitude)
        {
            var point = _geometryFactory.CreatePoint(new Coordinate(longitude, latitude));
            var candidates = _index.Query(point.EnvelopeInternal);
            foreach (var candidate in candidates)
            {
                if (candidate.Geometry.Covers(point))
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
            metadata["spatialReference"] = "EPSG:4326 / WGS84";
            metadata["classificationField"] = _option.AttributeField;
            metadata["file"] = _shapefilePath;
            return metadata;
        }

        private static object? ReadAttribute(IAttributesTable attributes, string name)
        {
            foreach (var candidate in attributes.GetNames())
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
                    return attributes[candidate];
            }
            return null;
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
            public SeabedFeature(Geometry geometry, ShomSedimentDefinition definition, int? sourceMapNumber)
            {
                Geometry = geometry;
                Definition = definition;
                SourceMapNumber = sourceMapNumber;
            }

            public Geometry Geometry { get; }
            public ShomSedimentDefinition Definition { get; }
            public int? SourceMapNumber { get; }
        }
    }
}
