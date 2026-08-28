using System;
using System.Globalization;

namespace MarineEnvironment.Models
{
    /// <summary>
    /// User-derived/estimated seabed composition. These values are not observed sediment fractions.
    /// Rock is derived from ETOPO terrain metrics, while sand/mud partitioning is derived from
    /// Martin et al. (2015) predicted porosity.
    /// </summary>
    public sealed class EstimatedSeabedValue
    {
        public string ModelId { get; init; } = string.Empty;
        public string TerrainSourceId { get; init; } = string.Empty;
        public string PorositySourceId { get; init; } = string.Empty;
        public string Classification { get; init; } = string.Empty;

        public double RockPercent { get; init; }
        public double SandPercent { get; init; }
        public double MudPercent { get; init; }
        public double BurialRatePercent { get; init; }

        public double PorosityPercent { get; init; }
        public double MudIndex { get; init; }
        public double SlopeDegrees { get; init; }
        public double RoughnessMeters { get; init; }
        public double RockIndex { get; init; }

        public double CalibrationSlopeP75Degrees { get; init; }
        public double CalibrationSlopeP95Degrees { get; init; }
        public double CalibrationRoughnessP75Meters { get; init; }
        public double CalibrationRoughnessP95Meters { get; init; }

        public string SeabedDisplay => string.Format(
            CultureInfo.InvariantCulture,
            "암반 {0:0.#}% / 모래 {1:0.#}% / 뻘 {2:0.#}%",
            RockPercent,
            SandPercent,
            MudPercent);

        public override string ToString() => SeabedDisplay;
    }
}
