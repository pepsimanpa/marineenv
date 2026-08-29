using System;
using System.Globalization;

namespace MarineEnvironment.Models
{
    /// <summary>
    /// User-derived/estimated seabed result. ETOPO terrain metrics are used only to decide
    /// rock versus sediment. When sediment is selected, Martin et al. (2015) predicted porosity
    /// is regionally normalized and linearly mapped to the project's operational mud/sand range.
    /// These values are not observed sediment fractions.
    /// </summary>
    public sealed class EstimatedSeabedValue
    {
        public string ModelId { get; init; } = string.Empty;
        public string TerrainSourceId { get; init; } = string.Empty;
        public string PorositySourceId { get; init; } = string.Empty;
        public string Classification { get; init; } = string.Empty;

        /// <summary>True when the ETOPO-derived RockIndex exceeds the configured decision threshold.</summary>
        public bool IsRock { get; init; }

        /// <summary>
        /// Compatibility field. Categorical result only: 100 for rock and 0 for sediment.
        /// Do not interpret this as an estimated physical rock fraction.
        /// </summary>
        public double RockPercent { get; init; }

        /// <summary>Sediment-only sand share. Mud + Sand = 100 when IsRock is false.</summary>
        public double SandPercent { get; init; }

        /// <summary>Sediment-only mud share. Mud + Sand = 100 when IsRock is false.</summary>
        public double MudPercent { get; init; }
        public double BurialRatePercent { get; init; }

        public double PorosityPercent { get; init; }

        /// <summary>
        /// Linear 0..1 Martin porosity index T after clamped regional normalization between
        /// PorosityLowPercent and PorosityHighPercent. This is a tendency index, not observed mud fraction.
        /// </summary>
        public double MudIndex { get; init; }

        public double SlopeDegrees { get; init; }
        public double RoughnessMeters { get; init; }

        /// <summary>Continuous ETOPO terrain evidence used for rock/sediment classification, not a percentage.</summary>
        public double RockIndex { get; init; }
        public double RockDecisionThreshold { get; init; }

        public double CalibrationSlopeP75Degrees { get; init; }
        public double CalibrationSlopeP95Degrees { get; init; }
        public double CalibrationRoughnessP75Meters { get; init; }
        public double CalibrationRoughnessP95Meters { get; init; }

        public string SeabedDisplay => IsRock
            ? "암반"
            : string.Format(
                CultureInfo.InvariantCulture,
                "뻘 {0:0.#}% / 모래 {1:0.#}%",
                MudPercent,
                SandPercent);

        public override string ToString() => SeabedDisplay;
    }
}
