using System;
using System.Collections.Generic;
using System.Linq;
using MarineEnvironment.Models;
using MarineEnvironment.Sources;

namespace MarineEnvironment
{
    public sealed partial class MarineEnvironmentManager
    {
        private void AppendEstimatedSeabedCategorical(
            List<EnvironmentValue> values,
            IReadOnlyList<IEnvironmentDataSource> readySources,
            EnvironmentQuery query)
        {
            Configuration.EstimatedSeabedModelOption? model;
            lock (_estimatedSeabedSync)
                model = _estimatedSeabedModel;

            if (model == null || !model.Enabled)
                return;

            var terrain = readySources.FirstOrDefault(x => string.Equals(x.Id, model.TerrainSourceId, StringComparison.OrdinalIgnoreCase));
            var porosity = readySources.FirstOrDefault(x => string.Equals(x.Id, model.PorositySourceId, StringComparison.OrdinalIgnoreCase));
            if (terrain == null || porosity == null || terrain.Type != EnvironmentType.Bathymetry || porosity.Type != EnvironmentType.Porosity)
                return;

            var calibration = EnsureTerrainCalibration(terrain, model, query.DateTime);
            if (calibration == null)
                return;

            if (!TryGetPointTerrainMetrics(terrain, model, query, out var slopeDegrees, out var roughnessMeters))
                return;

            if (!TryGetInterpolatedPorosity(porosity, query, out var porosityPercent, out var porosityMethod))
                return;

            var slopeIndex = Normalize(slopeDegrees, calibration.SlopeP75Degrees, calibration.SlopeP95Degrees);
            var roughnessIndex = Normalize(roughnessMeters, calibration.RoughnessP75Meters, calibration.RoughnessP95Meters);
            var rockIndex = Math.Sqrt(slopeIndex * roughnessIndex);
            var rockThreshold = Clamp01(model.RockDecisionThreshold);
            var isRock = rockIndex >= rockThreshold;

            var porosityT = Normalize(porosityPercent, model.PorosityLowPercent, model.PorosityHighPercent);
            var mudIndex = SmoothStep(porosityT);

            // RockIndex is a terrain-based decision score, not a physical rock fraction.
            // For sediment cells, the operational model needs only the 50:50 mud/sand end member
            // through pure mud. Therefore Martin's 0..1 mud tendency is mapped to:
            // Mud = 50..100%, Sand = 50..0%.
            var rockPercent = isRock ? 100.0 : 0.0;
            var mudPercent = isRock ? 0.0 : 50.0 + (50.0 * mudIndex);
            var sandPercent = isRock ? 0.0 : 100.0 - mudPercent;
            var sedimentMudShare = mudPercent / 100.0;

            // Existing operational burial anchors are defined against sediment mud share:
            // 50% -> 5%, 70% -> 35%, 80% -> 65%, 100% -> 85%.
            var burialRate = isRock ? 0.0 : CalculateOperationalBurialRate(0.0, sedimentMudShare);
            var classification = isRock ? "암반" : ClassifySediment(mudPercent);

            var estimated = new EstimatedSeabedValue
            {
                ModelId = model.Id,
                TerrainSourceId = terrain.Id,
                PorositySourceId = porosity.Id,
                Classification = classification,
                IsRock = isRock,
                RockPercent = rockPercent,
                MudPercent = mudPercent,
                SandPercent = sandPercent,
                BurialRatePercent = burialRate,
                PorosityPercent = porosityPercent,
                MudIndex = mudIndex,
                SlopeDegrees = slopeDegrees,
                RoughnessMeters = roughnessMeters,
                RockIndex = rockIndex,
                RockDecisionThreshold = rockThreshold,
                CalibrationSlopeP75Degrees = calibration.SlopeP75Degrees,
                CalibrationSlopeP95Degrees = calibration.SlopeP95Degrees,
                CalibrationRoughnessP75Meters = calibration.RoughnessP75Meters,
                CalibrationRoughnessP95Meters = calibration.RoughnessP95Meters
            };

            var metadata = new Dictionary<string, object?>
            {
                ["dataKind"] = "DerivedEstimate",
                ["observed"] = false,
                ["model"] = model.Id,
                ["terrainSource"] = terrain.Id,
                ["porositySource"] = porosity.Id,
                ["terrainNeighborhood"] = $"{model.NeighborhoodSize}x{model.NeighborhoodSize}",
                ["terrainMethod"] = "least-squares plane slope + detrended RMS roughness",
                ["terrainCalibration"] = "regional P75-P95 normalization",
                ["calibrationBounds"] = new[] { model.CalibrationMinLatitude, model.CalibrationMaxLatitude, model.CalibrationMinLongitude, model.CalibrationMaxLongitude },
                ["calibrationStride"] = model.CalibrationStride,
                ["slopeP75Degrees"] = calibration.SlopeP75Degrees,
                ["slopeP95Degrees"] = calibration.SlopeP95Degrees,
                ["roughnessP75Meters"] = calibration.RoughnessP75Meters,
                ["roughnessP95Meters"] = calibration.RoughnessP95Meters,
                ["porosityInterpolation"] = porosityMethod,
                ["porosityLowPercent"] = model.PorosityLowPercent,
                ["porosityHighPercent"] = model.PorosityHighPercent,
                ["rockIndex"] = rockIndex,
                ["rockDecisionThreshold"] = rockThreshold,
                ["isRock"] = isRock,
                ["rockDecisionFormula"] = "RockIndex = sqrt(SlopeIndex * RoughnessIndex); Rock if RockIndex >= threshold",
                ["sedimentMudFormula"] = "Mud% = 50 + 50 * smoothstep(PorosityIndex)",
                ["sedimentSandFormula"] = "Sand% = 100 - Mud%",
                ["sedimentMudRange"] = "50..100%",
                ["sedimentSandRange"] = "50..0%",
                ["burialPolicy"] = "Rock=0%; sediment uses SHOM_OPERATIONAL_COMPAT_V1 mud-share anchors / user-derived, not observed",
                ["mudPercent"] = mudPercent,
                ["sandPercent"] = sandPercent,
                ["burialRatePercent"] = burialRate
            };

            values.Add(new EnvironmentValue(
                model.Id,
                EnvironmentType.Seabed,
                estimated,
                null,
                query.Latitude,
                query.Longitude,
                null,
                query.DateTime,
                "Derived estimated seabed classification",
                metadata));
        }

        private static string ClassifySediment(double mudPercent)
        {
            if (mudPercent >= 99.5) return "뻘";
            if (mudPercent >= 70.0) return "뻘 우세";
            return "뻘·모래 혼합";
        }
    }
}
