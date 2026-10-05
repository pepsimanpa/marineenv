using System;
using System.Collections.Generic;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Sources;
using MarineEnvironment.Sources.Shom;

namespace MarineEnvironment
{
    public sealed partial class MarineEnvironmentManager
    {
        /// <summary>
        /// Splits the requested rectangle into analysis cells and calculates one seabed grade
        /// per cell center. READY seabed sources are tried in ascending config priority;
        /// the first source with a mapped operational seabed value wins for that cell.
        /// Normal point-query APIs remain independent and return every available source.
        /// </summary>
        public SeabedGradeGridResult QuerySeabedGradeGrid(SeabedGradeGridQuery query)
        {
            lock (_querySync)
            {
                if (query == null)
                    throw new ArgumentNullException(nameof(query));
                ValidateSeabedGradeQuery(query);
                ThrowIfDisposed();

                SeabedSourceCandidate[] sources;
                Dictionary<string, SeabedMappingLookup> mappings;
                lock (_sync)
                {
                    sources = _sources.Values
                        .Where(x => x.Status == SourceStatus.Ready && x.Type == EnvironmentType.Seabed)
                        .Select(x => new SeabedSourceCandidate(
                            x,
                            _sourcePriorities.TryGetValue(x.Id, out var p) ? p : 100))
                        .OrderBy(x => x.Priority)
                        .ThenBy(x => x.Source.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    mappings = new Dictionary<string, SeabedMappingLookup>(
                        _seabedMappings, StringComparer.OrdinalIgnoreCase);
                }

                if (sources.Length == 0)
                    throw new InvalidOperationException("No READY seabed source is configured.");

                ResolveAnalysisGrid(query, out var columns, out var rows);
                var cells = new SeabedGradeCell[checked(columns * rows)];
                var dLat = (query.MaxLatitude - query.MinLatitude) / rows;
                var dLon = (query.MaxLongitude - query.MinLongitude) / columns;

                for (var row = 0; row < rows; row++)
                {
                    var cellMaxLat = query.MaxLatitude - (row * dLat);
                    var cellMinLat = cellMaxLat - dLat;
                    var centerLat = (cellMinLat + cellMaxLat) / 2.0;

                    for (var column = 0; column < columns; column++)
                    {
                        var cellMinLon = query.MinLongitude + (column * dLon);
                        var cellMaxLon = cellMinLon + dLon;
                        var centerLon = (cellMinLon + cellMaxLon) / 2.0;

                        cells[(row * columns) + column] = CalculateSeabedGradeCell(
                            row, column,
                            cellMinLat, cellMaxLat, cellMinLon, cellMaxLon,
                            centerLat, centerLon,
                            query, sources, mappings);
                    }
                }

                return new SeabedGradeGridResult
                {
                    MinLatitude = query.MinLatitude,
                    MaxLatitude = query.MaxLatitude,
                    MinLongitude = query.MinLongitude,
                    MaxLongitude = query.MaxLongitude,
                    Columns = columns,
                    Rows = rows,
                    GridMode = query.GridMode,
                    RequestedCellSizeKilometers = query.GridMode == SeabedGradeGridMode.CellSizeKilometers
                        ? query.CellSizeKilometers : null,
                    ContactDensity = query.ContactDensity,
                    Terrain = query.Terrain,
                    Cells = cells
                };
            }
        }

        private static SeabedGradeCell CalculateSeabedGradeCell(
            int row, int column,
            double minLat, double maxLat, double minLon, double maxLon,
            double centerLat, double centerLon,
            SeabedGradeGridQuery query,
            IReadOnlyList<SeabedSourceCandidate> sources,
            IReadOnlyDictionary<string, SeabedMappingLookup> mappings)
        {
            foreach (var candidate in sources)
            {
                var sourceValue = candidate.Source.Query(new EnvironmentQuery
                {
                    Latitude = centerLat,
                    Longitude = centerLon,
                    DateTime = query.DateTime,
                    Sampling = query.Sampling
                });

                if (sourceValue == null)
                    continue;

                var derived = new List<EnvironmentValue>();
                AppendDirectDerivedValues(sourceValue, mappings, derived);

                string seabed;
                double? mud;
                double? sand;
                double burial;
                string mappingId;
                string sourceCode;
                string sourceClass;

                var korea = derived.Select(x => x.Value).OfType<KoreaSedimentDerivedValue>().FirstOrDefault();
                var shom = derived.Select(x => x.Value).OfType<SeabedDerivedValue>().FirstOrDefault();

                if (korea != null)
                {
                    seabed = korea.Seabed;
                    mud = korea.MudPercent;
                    sand = korea.SandPercent;
                    burial = korea.BurialRatePercent;
                    mappingId = korea.MappingTableId;
                    sourceCode = korea.OriginalCode;
                    sourceClass = korea.OriginalClassification;
                }
                else if (shom != null)
                {
                    seabed = shom.Seabed;
                    mud = shom.MudPercent;
                    sand = shom.SandPercent;
                    burial = shom.BurialRatePercent;
                    mappingId = shom.MappingTableId;
                    sourceCode = sourceValue.Value is SeabedValue raw ? raw.Code : string.Empty;
                    sourceClass = shom.ShomOriginalClassification;
                }
                else
                {
                    // A raw polygon without an operational mapping cannot participate in
                    // the grade table; try the next configured seabed layer.
                    continue;
                }

                if (!SeabedGradeCatalog.TryCalculate(
                    seabed, mud, sand, burial, query.Terrain, query.ContactDensity,
                    out var grade, out var gradeIndex, out var bucket))
                {
                    return EmptyCell(row, column, minLat, maxLat, minLon, maxLon,
                        centerLat, centerLon, query,
                        $"Mapped seabed from {candidate.Source.Id} does not match the grade table.");
                }

                return new SeabedGradeCell
                {
                    Row = row,
                    Column = column,
                    MinLatitude = minLat,
                    MaxLatitude = maxLat,
                    MinLongitude = minLon,
                    MaxLongitude = maxLon,
                    CenterLatitude = centerLat,
                    CenterLongitude = centerLon,
                    SourceId = candidate.Source.Id,
                    SourcePriority = candidate.Priority,
                    SourceCode = sourceCode,
                    SourceClassification = sourceClass,
                    MappingTableId = mappingId,
                    Seabed = seabed,
                    MudPercent = mud,
                    SandPercent = sand,
                    BurialRatePercent = burial,
                    ContactDensity = query.ContactDensity,
                    Terrain = query.Terrain,
                    Grade = grade,
                    GradeIndex = gradeIndex,
                    GradeBucket = bucket
                };
            }

            return EmptyCell(row, column, minLat, maxLat, minLon, maxLon,
                centerLat, centerLon, query, "No configured seabed source has data at the cell center.");
        }

        private static SeabedGradeCell EmptyCell(
            int row, int column,
            double minLat, double maxLat, double minLon, double maxLon,
            double centerLat, double centerLon,
            SeabedGradeGridQuery query, string reason)
        {
            return new SeabedGradeCell
            {
                Row = row,
                Column = column,
                MinLatitude = minLat,
                MaxLatitude = maxLat,
                MinLongitude = minLon,
                MaxLongitude = maxLon,
                CenterLatitude = centerLat,
                CenterLongitude = centerLon,
                ContactDensity = query.ContactDensity,
                Terrain = query.Terrain,
                NoDataReason = reason
            };
        }

        private static void ResolveAnalysisGrid(
            SeabedGradeGridQuery query, out int columns, out int rows)
        {
            if (query.GridMode == SeabedGradeGridMode.CellCount)
            {
                columns = query.Columns;
                rows = query.Rows;
            }
            else
            {
                var midLat = (query.MinLatitude + query.MaxLatitude) / 2.0;
                var midLon = (query.MinLongitude + query.MaxLongitude) / 2.0;
                var widthKm = HaversineKilometers(midLat, query.MinLongitude, midLat, query.MaxLongitude);
                var heightKm = HaversineKilometers(query.MinLatitude, midLon, query.MaxLatitude, midLon);
                columns = Math.Max(1, (int)Math.Ceiling(widthKm / query.CellSizeKilometers));
                rows = Math.Max(1, (int)Math.Ceiling(heightKm / query.CellSizeKilometers));
            }

            const int maxCells = 100000;
            if ((long)columns * rows > maxCells)
                throw new ArgumentOutOfRangeException(nameof(query),
                    $"Analysis grid is too large ({columns} x {rows}). Maximum is {maxCells:N0} cells.");
        }

        private static void ValidateSeabedGradeQuery(SeabedGradeGridQuery query)
        {
            if (query.MinLatitude < -90 || query.MaxLatitude > 90 || query.MinLatitude >= query.MaxLatitude)
                throw new ArgumentException("Invalid latitude bounds.", nameof(query));
            if (query.MinLongitude < -360 || query.MaxLongitude > 360 || query.MinLongitude >= query.MaxLongitude)
                throw new ArgumentException("Invalid longitude bounds.", nameof(query));
            if (query.ContactDensity < 1 || query.ContactDensity > 3)
                throw new ArgumentOutOfRangeException(nameof(query.ContactDensity), "ContactDensity must be 1, 2, or 3.");
            if (!Enum.IsDefined(typeof(SeabedTerrain), query.Terrain))
                throw new ArgumentOutOfRangeException(nameof(query.Terrain));
            if (query.GridMode == SeabedGradeGridMode.CellCount
                && (query.Columns <= 0 || query.Rows <= 0))
                throw new ArgumentOutOfRangeException(nameof(query), "Columns and Rows must be positive.");
            if (query.GridMode == SeabedGradeGridMode.CellSizeKilometers
                && (query.CellSizeKilometers <= 0 || double.IsNaN(query.CellSizeKilometers)
                    || double.IsInfinity(query.CellSizeKilometers)))
                throw new ArgumentOutOfRangeException(nameof(query.CellSizeKilometers),
                    "CellSizeKilometers must be a positive finite value.");
        }

        private static double HaversineKilometers(double lat1, double lon1, double lat2, double lon2)
        {
            const double radiusKm = 6371.0088;
            var rad = Math.PI / 180.0;
            var dLat = (lat2 - lat1) * rad;
            var dLon = (lon2 - lon1) * rad;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad)
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 2 * radiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }

        private sealed class SeabedSourceCandidate
        {
            public SeabedSourceCandidate(IEnvironmentDataSource source, int priority)
            {
                Source = source;
                Priority = priority;
            }

            public IEnvironmentDataSource Source { get; }
            public int Priority { get; }
        }
    }
}
