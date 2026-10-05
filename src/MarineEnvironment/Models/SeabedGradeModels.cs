using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MarineEnvironment.Models
{
    public enum SeabedTerrain
    {
        Flat,
        Normal,
        Rough
    }

    public enum SeabedGradeGridMode
    {
        CellCount,
        CellSizeKilometers
    }

    public sealed class SeabedGradeGridQuery
    {
        public double MinLatitude { get; init; }
        public double MaxLatitude { get; init; }
        public double MinLongitude { get; init; }
        public double MaxLongitude { get; init; }

        public SeabedGradeGridMode GridMode { get; init; } = SeabedGradeGridMode.CellCount;
        public int Columns { get; init; } = 20;
        public int Rows { get; init; } = 10;
        public double CellSizeKilometers { get; init; } = 5.0;

        /// <summary>Operational contact-density level. Valid values are 1, 2, 3.</summary>
        public int ContactDensity { get; init; } = 1;

        /// <summary>User-supplied seabed terrain category. Default is Flat.</summary>
        public SeabedTerrain Terrain { get; init; } = SeabedTerrain.Flat;

        public DateTime? DateTime { get; init; }
        public SpatialSampling Sampling { get; init; } = SpatialSampling.Nearest;
    }

    public sealed class SeabedGradeCell
    {
        public int Row { get; init; }
        public int Column { get; init; }

        public double MinLatitude { get; init; }
        public double MaxLatitude { get; init; }
        public double MinLongitude { get; init; }
        public double MaxLongitude { get; init; }
        public double CenterLatitude { get; init; }
        public double CenterLongitude { get; init; }

        public string SourceId { get; init; } = string.Empty;
        public int? SourcePriority { get; init; }
        public string SourceCode { get; init; } = string.Empty;
        public string SourceClassification { get; init; } = string.Empty;
        public string MappingTableId { get; init; } = string.Empty;

        public string Seabed { get; init; } = string.Empty;
        public double? MudPercent { get; init; }
        public double? SandPercent { get; init; }
        public double? BurialRatePercent { get; init; }

        public int ContactDensity { get; init; }
        public SeabedTerrain Terrain { get; init; }

        public string? Grade { get; init; }
        public int? GradeIndex { get; init; }
        public string? GradeBucket { get; init; }
        public string? NoDataReason { get; init; }

        public bool HasGrade => !string.IsNullOrWhiteSpace(Grade);
    }

    public sealed class SeabedGradeGridResult
    {
        public const string ModelId = "SEABED_GRADE_V1";

        public double MinLatitude { get; init; }
        public double MaxLatitude { get; init; }
        public double MinLongitude { get; init; }
        public double MaxLongitude { get; init; }
        public int Columns { get; init; }
        public int Rows { get; init; }
        public SeabedGradeGridMode GridMode { get; init; }
        public double? RequestedCellSizeKilometers { get; init; }
        public int ContactDensity { get; init; }
        public SeabedTerrain Terrain { get; init; }
        public IReadOnlyList<SeabedGradeCell> Cells { get; init; } = Array.Empty<SeabedGradeCell>();

        public int GradeCount => Cells.Count(x => x.HasGrade);
        public int NoDataCount => Cells.Count - GradeCount;

        public SeabedGradeCell GetCell(int row, int column)
        {
            if ((uint)row >= (uint)Rows)
                throw new ArgumentOutOfRangeException(nameof(row));
            if ((uint)column >= (uint)Columns)
                throw new ArgumentOutOfRangeException(nameof(column));
            return Cells[(row * Columns) + column];
        }

        /// <summary>
        /// Compatibility display raster for the validation Viewer. GradeIndex is encoded
        /// A1..A3=1..3, B1..B3=4..6, C1..C3=7..9, D1..D3=10..12.
        /// Full provenance remains available in Cells.
        /// </summary>
        public GridResult ToGridResult()
        {
            var values = Cells.Select(x => x.GradeIndex.HasValue ? (double?)x.GradeIndex.Value : null).ToArray();
            var labels = Cells.Select(x =>
            {
                if (!x.HasGrade)
                    return x.NoDataReason == null ? "NoData" : "NoData | " + x.NoDataReason;
                var sediment = x.MudPercent.HasValue && x.SandPercent.HasValue
                    ? string.Format(CultureInfo.InvariantCulture, "{0:0.#}/{1:0.#}", x.MudPercent.Value, x.SandPercent.Value)
                    : x.Seabed;
                return string.Format(CultureInfo.InvariantCulture,
                    "{0} | {1} | burial {2:0.#}% | {3} | P{4}",
                    x.Grade, sediment, x.BurialRatePercent ?? 0, x.SourceId, x.SourcePriority ?? 100);
            }).ToArray();

            var latitudes = new double[Rows];
            var longitudes = new double[Columns];
            for (var r = 0; r < Rows; r++)
                latitudes[r] = GetCell(r, 0).CenterLatitude;
            for (var c = 0; c < Columns; c++)
                longitudes[c] = GetCell(0, c).CenterLongitude;

            var metadata = new Dictionary<string, object?>
            {
                ["classificationScheme"] = "SeabedGrade",
                ["model"] = ModelId,
                ["gridMode"] = GridMode.ToString(),
                ["requestedCellSizeKilometers"] = RequestedCellSizeKilometers,
                ["contactDensity"] = ContactDensity,
                ["terrain"] = Terrain.ToString(),
                ["gradeCount"] = GradeCount,
                ["noDataCount"] = NoDataCount,
                ["sourceSelection"] = "Lowest configured priority with available mapped seabed value"
            };

            return new GridResult
            {
                SourceId = ModelId,
                Type = EnvironmentType.SeabedGrade,
                Width = Columns,
                Height = Rows,
                Latitudes = latitudes,
                Longitudes = longitudes,
                Values = values,
                Labels = labels,
                Unit = null,
                Variable = "SeabedGrade",
                Minimum = GradeCount > 0 ? values.Where(x => x.HasValue).Min() : null,
                Maximum = GradeCount > 0 ? values.Where(x => x.HasValue).Max() : null,
                Metadata = metadata
            };
        }
    }

    public static class SeabedGradeCatalog
    {
        public static bool TryCalculate(
            string seabed,
            double? mudPercent,
            double? sandPercent,
            double burialRatePercent,
            SeabedTerrain terrain,
            int contactDensity,
            out string grade,
            out int gradeIndex,
            out string bucket)
        {
            grade = string.Empty;
            gradeIndex = 0;
            bucket = string.Empty;

            if (contactDensity < 1 || contactDensity > 3)
                return false;

            var isRock = string.Equals(seabed, "암반", StringComparison.Ordinal);
            var group = -1;

            if (isRock && burialRatePercent < 10)
            {
                group = 5;
                bucket = "암반 / 매몰률 10% 미만";
            }
            else if (mudPercent.HasValue && sandPercent.HasValue
                && Nearly(mudPercent.Value, 50) && Nearly(sandPercent.Value, 50)
                && burialRatePercent < 10)
            {
                group = 0;
                bucket = "뻘/모래 50/50 / 매몰률 10% 미만";
            }
            else if (mudPercent.HasValue && sandPercent.HasValue
                && Nearly(mudPercent.Value, 70) && Nearly(sandPercent.Value, 30)
                && burialRatePercent >= 10 && burialRatePercent < 20)
            {
                group = 1;
                bucket = "뻘/모래 70/30 / 매몰률 10~20%";
            }
            else if (mudPercent.HasValue && sandPercent.HasValue
                && Nearly(mudPercent.Value, 70) && Nearly(sandPercent.Value, 30)
                && burialRatePercent >= 20 && burialRatePercent < 50)
            {
                group = 2;
                bucket = "뻘/모래 70/30 / 매몰률 20~50%";
            }
            else if (mudPercent.HasValue && sandPercent.HasValue
                && Nearly(mudPercent.Value, 80) && Nearly(sandPercent.Value, 20)
                && burialRatePercent >= 50 && burialRatePercent < 75)
            {
                group = 3;
                bucket = "뻘/모래 80/20 / 매몰률 50~75%";
            }
            else if (mudPercent.HasValue && sandPercent.HasValue
                && Nearly(mudPercent.Value, 100) && Nearly(sandPercent.Value, 0)
                && burialRatePercent >= 75)
            {
                group = 4;
                bucket = "뻘/모래 100/0 / 매몰률 75% 이상";
            }

            if (group < 0)
                return false;

            var letters = terrain switch
            {
                SeabedTerrain.Flat => new[] { 'A', 'B', 'B', 'C', 'D', 'B' },
                SeabedTerrain.Normal => new[] { 'B', 'B', 'C', 'C', 'D', 'C' },
                SeabedTerrain.Rough => new[] { 'C', 'C', 'C', 'C', 'D', 'C' },
                _ => Array.Empty<char>()
            };
            if (letters.Length == 0)
                return false;

            var letter = letters[group];
            grade = letter.ToString() + contactDensity.ToString(CultureInfo.InvariantCulture);
            gradeIndex = ((letter - 'A') * 3) + contactDensity;
            return true;
        }

        private static bool Nearly(double a, double b) => Math.Abs(a - b) < 1e-8;
    }
}
