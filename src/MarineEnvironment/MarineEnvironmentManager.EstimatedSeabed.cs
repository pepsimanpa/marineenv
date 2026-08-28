using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MarineEnvironment.Configuration;
using MarineEnvironment.Models;
using MarineEnvironment.Sources;

namespace MarineEnvironment
{
    public sealed partial class MarineEnvironmentManager
    {
        private readonly object _estimatedSeabedSync = new object();
        private EstimatedSeabedModelOption? _estimatedSeabedModel;
        private TerrainCalibration? _estimatedTerrainCalibration;

        private void ConfigureEstimatedSeabed(EstimatedSeabedModelOption? option)
        {
            if (option != null && option.Enabled)
            {
                if (string.IsNullOrWhiteSpace(option.Id))
                    throw new ArgumentException("Estimated seabed model id is required.");
                if (string.IsNullOrWhiteSpace(option.TerrainSourceId) || string.IsNullOrWhiteSpace(option.PorositySourceId))
                    throw new ArgumentException("Estimated seabed model requires terrainSourceId and porositySourceId.");
                if (option.NeighborhoodSize < 3 || option.NeighborhoodSize % 2 == 0)
                    throw new ArgumentException("Estimated seabed neighborhoodSize must be an odd integer >= 3.");
                if (option.CalibrationStride < 1)
                    throw new ArgumentException("Estimated seabed calibrationStride must be >= 1.");
                if (option.CalibrationMinLatitude >= option.CalibrationMaxLatitude || option.CalibrationMinLongitude >= option.CalibrationMaxLongitude)
                    throw new ArgumentException("Estimated seabed calibration bounds are invalid.");
                if (option.PorosityLowPercent >= option.PorosityHighPercent)
                    throw new ArgumentException("Estimated seabed porosityLowPercent must be less than porosityHighPercent.");
            }

            lock (_estimatedSeabedSync)
            {
                _estimatedSeabedModel = option;
                _estimatedTerrainCalibration = null;
            }
        }

        private void ResetEstimatedSeabedCalibration()
        {
            lock (_estimatedSeabedSync)
                _estimatedTerrainCalibration = null;
        }

        private void AppendEstimatedSeabed(
            List<EnvironmentValue> values,
            IReadOnlyList<IEnvironmentDataSource> readySources,
            EnvironmentQuery query)
        {
            EstimatedSeabedModelOption? model;
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

            var porosityT = Normalize(porosityPercent, model.PorosityLowPercent, model.PorosityHighPercent);
            var mudIndex = SmoothStep(porosityT);

            var rockPercent = 100.0 * rockIndex;
            var mudPercent = 100.0 * (1.0 - rockIndex) * mudIndex;
            var sandPercent = 100.0 * (1.0 - rockIndex) * (1.0 - mudIndex);
            NormalizePercentSum(ref rockPercent, ref sandPercent, ref mudPercent);

            var burialRate = CalculateOperationalBurialRate(rockIndex, mudIndex);
            var classification = ClassifyEstimatedSeabed(rockPercent, mudIndex);

            var estimated = new EstimatedSeabedValue
            {
                ModelId = model.Id,
                TerrainSourceId = terrain.Id,
                PorositySourceId = porosity.Id,
                Classification = classification,
                RockPercent = rockPercent,
                SandPercent = sandPercent,
                MudPercent = mudPercent,
                BurialRatePercent = burialRate,
                PorosityPercent = porosityPercent,
                MudIndex = mudIndex,
                SlopeDegrees = slopeDegrees,
                RoughnessMeters = roughnessMeters,
                RockIndex = rockIndex,
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
                ["rockFormula"] = "sqrt(SlopeIndex * RoughnessIndex)",
                ["mudFormula"] = "(1-RockIndex) * smoothstep(PorosityIndex)",
                ["sandFormula"] = "(1-RockIndex) * (1-smoothstep(PorosityIndex))",
                ["burialPolicy"] = "SHOM_OPERATIONAL_COMPAT_V1 / user-derived, not observed",
                ["rockPercent"] = rockPercent,
                ["sandPercent"] = sandPercent,
                ["mudPercent"] = mudPercent,
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
                "Derived estimated seabed composition",
                metadata));
        }

        private TerrainCalibration? EnsureTerrainCalibration(
            IEnvironmentDataSource terrain,
            EstimatedSeabedModelOption model,
            DateTime? dateTime)
        {
            lock (_estimatedSeabedSync)
            {
                if (_estimatedTerrainCalibration != null &&
                    string.Equals(_estimatedTerrainCalibration.SourceId, terrain.Id, StringComparison.OrdinalIgnoreCase))
                    return _estimatedTerrainCalibration;

                var grid = terrain.QueryGrid(new GridQuery
                {
                    MinLatitude = model.CalibrationMinLatitude,
                    MaxLatitude = model.CalibrationMaxLatitude,
                    MinLongitude = model.CalibrationMinLongitude,
                    MaxLongitude = model.CalibrationMaxLongitude,
                    DateTime = dateTime,
                    Width = 2,
                    Height = 2,
                    ResolutionMode = GridResolutionMode.SourceNative
                });

                var half = model.NeighborhoodSize / 2;
                if (grid.Width <= half * 2 || grid.Height <= half * 2)
                    return null;

                var slopes = new List<double>();
                var roughness = new List<double>();
                var stride = model.CalibrationStride;

                for (var row = half; row < grid.Height - half; row += stride)
                {
                    for (var column = half; column < grid.Width - half; column += stride)
                    {
                        if (!TryCalculateTerrainMetrics(grid, row, column, half, out var slope, out var rough))
                            continue;
                        slopes.Add(slope);
                        roughness.Add(rough);
                    }
                }

                if (slopes.Count < 100 || roughness.Count < 100)
                    return null;

                slopes.Sort();
                roughness.Sort();
                var calibration = new TerrainCalibration(
                    terrain.Id,
                    Percentile(slopes, 0.75),
                    Percentile(slopes, 0.95),
                    Percentile(roughness, 0.75),
                    Percentile(roughness, 0.95));

                if (calibration.SlopeP95Degrees <= calibration.SlopeP75Degrees ||
                    calibration.RoughnessP95Meters <= calibration.RoughnessP75Meters)
                    return null;

                _estimatedTerrainCalibration = calibration;
                return calibration;
            }
        }

        private static bool TryGetPointTerrainMetrics(
            IEnvironmentDataSource terrain,
            EstimatedSeabedModelOption model,
            EnvironmentQuery query,
            out double slopeDegrees,
            out double roughnessMeters)
        {
            slopeDegrees = 0;
            roughnessMeters = 0;
            var center = terrain.Query(query);
            if (center == null || !TryConvertDouble(center.Value, out var centerElevation) || centerElevation > 0)
                return false;

            if (!TryMetadataDouble(center.Metadata, "sourceLatitudeSpacingDegrees", out var latSpacing) ||
                !TryMetadataDouble(center.Metadata, "sourceLongitudeSpacingDegrees", out var lonSpacing) ||
                latSpacing <= 0 || lonSpacing <= 0)
                return false;

            var half = model.NeighborhoodSize / 2;
            var grid = terrain.QueryGrid(new GridQuery
            {
                MinLatitude = center.Latitude - (latSpacing * half),
                MaxLatitude = center.Latitude + (latSpacing * half),
                MinLongitude = center.Longitude - (lonSpacing * half),
                MaxLongitude = center.Longitude + (lonSpacing * half),
                DateTime = query.DateTime,
                Width = model.NeighborhoodSize,
                Height = model.NeighborhoodSize,
                ResolutionMode = GridResolutionMode.Custom
            });

            if (grid.Width != model.NeighborhoodSize || grid.Height != model.NeighborhoodSize)
                return false;

            return TryCalculateTerrainMetrics(grid, half, half, half, out slopeDegrees, out roughnessMeters);
        }

        private static bool TryCalculateTerrainMetrics(
            GridResult grid,
            int centerRow,
            int centerColumn,
            int halfWindow,
            out double slopeDegrees,
            out double roughnessMeters)
        {
            slopeDegrees = 0;
            roughnessMeters = 0;
            var centerValue = grid.GetValue(centerRow, centerColumn);
            if (!centerValue.HasValue || centerValue.Value > 0 || double.IsNaN(centerValue.Value) || double.IsInfinity(centerValue.Value))
                return false;

            const double EarthRadiusMeters = 6371008.8;
            const double DegreesToRadians = Math.PI / 180.0;
            var centerLat = grid.Latitudes[centerRow];
            var centerLon = grid.Longitudes[centerColumn];
            var cosLat = Math.Cos(centerLat * DegreesToRadians);

            double sx = 0, sy = 0, sz = 0, sxx = 0, syy = 0, sxy = 0, sxz = 0, syz = 0;
            var count = 0;

            for (var row = centerRow - halfWindow; row <= centerRow + halfWindow; row++)
            {
                for (var column = centerColumn - halfWindow; column <= centerColumn + halfWindow; column++)
                {
                    var value = grid.GetValue(row, column);
                    if (!value.HasValue || value.Value > 0 || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                        continue;

                    var x = (grid.Longitudes[column] - centerLon) * DegreesToRadians * EarthRadiusMeters * cosLat;
                    var y = (grid.Latitudes[row] - centerLat) * DegreesToRadians * EarthRadiusMeters;
                    var z = value.Value;
                    count++;
                    sx += x; sy += y; sz += z;
                    sxx += x * x; syy += y * y; sxy += x * y;
                    sxz += x * z; syz += y * z;
                }
            }

            if (count < 5)
                return false;

            if (!SolvePlane(count, sx, sy, sz, sxx, syy, sxy, sxz, syz, out var c0, out var ax, out var by))
                return false;

            var residualSquareSum = 0.0;
            var residualCount = 0;
            for (var row = centerRow - halfWindow; row <= centerRow + halfWindow; row++)
            {
                for (var column = centerColumn - halfWindow; column <= centerColumn + halfWindow; column++)
                {
                    var value = grid.GetValue(row, column);
                    if (!value.HasValue || value.Value > 0 || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                        continue;
                    var x = (grid.Longitudes[column] - centerLon) * DegreesToRadians * EarthRadiusMeters * cosLat;
                    var y = (grid.Latitudes[row] - centerLat) * DegreesToRadians * EarthRadiusMeters;
                    var predicted = c0 + (ax * x) + (by * y);
                    var residual = value.Value - predicted;
                    residualSquareSum += residual * residual;
                    residualCount++;
                }
            }

            if (residualCount == 0)
                return false;

            slopeDegrees = Math.Atan(Math.Sqrt((ax * ax) + (by * by))) / DegreesToRadians;
            roughnessMeters = Math.Sqrt(residualSquareSum / residualCount);
            return !(double.IsNaN(slopeDegrees) || double.IsInfinity(slopeDegrees) ||
                     double.IsNaN(roughnessMeters) || double.IsInfinity(roughnessMeters));
        }

        private static bool SolvePlane(
            int count,
            double sx,
            double sy,
            double sz,
            double sxx,
            double syy,
            double sxy,
            double sxz,
            double syz,
            out double c0,
            out double ax,
            out double by)
        {
            var a00 = (double)count; var a01 = sx; var a02 = sy;
            var a10 = sx; var a11 = sxx; var a12 = sxy;
            var a20 = sy; var a21 = sxy; var a22 = syy;
            var det = Determinant(a00, a01, a02, a10, a11, a12, a20, a21, a22);
            if (Math.Abs(det) < 1e-12)
            {
                c0 = ax = by = 0;
                return false;
            }

            c0 = Determinant(sz, a01, a02, sxz, a11, a12, syz, a21, a22) / det;
            ax = Determinant(a00, sz, a02, a10, sxz, a12, a20, syz, a22) / det;
            by = Determinant(a00, a01, sz, a10, a11, sxz, a20, a21, syz) / det;
            return true;
        }

        private static double Determinant(
            double a00, double a01, double a02,
            double a10, double a11, double a12,
            double a20, double a21, double a22)
        {
            return a00 * ((a11 * a22) - (a12 * a21))
                 - a01 * ((a10 * a22) - (a12 * a20))
                 + a02 * ((a10 * a21) - (a11 * a20));
        }

        private static bool TryGetInterpolatedPorosity(
            IEnvironmentDataSource porosity,
            EnvironmentQuery query,
            out double porosityPercent,
            out string method)
        {
            porosityPercent = 0;
            method = "Nearest";
            var nearest = porosity.Query(query);
            if (nearest == null || !TryConvertDouble(nearest.Value, out var nearestValue))
                return false;

            if (!TryMetadataDouble(nearest.Metadata, "sourceLatitudeSpacingDegrees", out var latSpacing) ||
                !TryMetadataDouble(nearest.Metadata, "sourceLongitudeSpacingDegrees", out var lonSpacing) ||
                latSpacing <= 0 || lonSpacing <= 0)
            {
                porosityPercent = nearestValue;
                return true;
            }

            var otherLat = query.Latitude >= nearest.Latitude ? nearest.Latitude + latSpacing : nearest.Latitude - latSpacing;
            var otherLon = query.Longitude >= nearest.Longitude ? nearest.Longitude + lonSpacing : nearest.Longitude - lonSpacing;
            var lat0 = Math.Min(nearest.Latitude, otherLat);
            var lat1 = Math.Max(nearest.Latitude, otherLat);
            var lon0 = Math.Min(nearest.Longitude, otherLon);
            var lon1 = Math.Max(nearest.Longitude, otherLon);

            if (!TryQueryNumeric(porosity, lat0, lon0, query.DateTime, out var q00) ||
                !TryQueryNumeric(porosity, lat0, lon1, query.DateTime, out var q10) ||
                !TryQueryNumeric(porosity, lat1, lon0, query.DateTime, out var q01) ||
                !TryQueryNumeric(porosity, lat1, lon1, query.DateTime, out var q11))
            {
                porosityPercent = nearestValue;
                method = "Nearest fallback (Bilinear corner NoData)";
                return true;
            }

            var u = lon1 == lon0 ? 0 : Clamp01((query.Longitude - lon0) / (lon1 - lon0));
            var v = lat1 == lat0 ? 0 : Clamp01((query.Latitude - lat0) / (lat1 - lat0));
            var lower = q00 + ((q10 - q00) * u);
            var upper = q01 + ((q11 - q01) * u);
            porosityPercent = lower + ((upper - lower) * v);
            method = "Bilinear to query position";
            return true;
        }

        private static bool TryQueryNumeric(IEnvironmentDataSource source, double latitude, double longitude, DateTime? dateTime, out double value)
        {
            var result = source.Query(new EnvironmentQuery
            {
                Latitude = latitude,
                Longitude = longitude,
                DateTime = dateTime,
                Sampling = SpatialSampling.Nearest
            });
            if (result != null && TryConvertDouble(result.Value, out value))
                return true;
            value = 0;
            return false;
        }

        private static bool TryConvertDouble(object? value, out double result)
        {
            try
            {
                if (value == null) { result = 0; return false; }
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return !(double.IsNaN(result) || double.IsInfinity(result));
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static bool TryMetadataDouble(IReadOnlyDictionary<string, object?>? metadata, string key, out double result)
        {
            if (metadata != null && metadata.TryGetValue(key, out var value) && TryConvertDouble(value, out result))
                return true;
            result = 0;
            return false;
        }

        private static double Normalize(double value, double low, double high)
        {
            if (high <= low) return 0;
            return Clamp01((value - low) / (high - low));
        }

        private static double SmoothStep(double t)
        {
            t = Clamp01(t);
            return (3.0 * t * t) - (2.0 * t * t * t);
        }

        private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));

        private static void NormalizePercentSum(ref double rock, ref double sand, ref double mud)
        {
            var sum = rock + sand + mud;
            if (sum <= 0) { rock = 0; sand = 0; mud = 0; return; }
            var scale = 100.0 / sum;
            rock *= scale;
            sand *= scale;
            mud *= scale;
        }

        private static string ClassifyEstimatedSeabed(double rockPercent, double mudIndex)
        {
            if (rockPercent >= 50.0) return "암반 우세";
            var sediment = mudIndex >= 0.60 ? "뻘 우세" : mudIndex >= 0.25 ? "뻘·모래 혼합" : "모래 우세";
            return rockPercent >= 25.0 ? $"암반 혼재 / {sediment}" : sediment;
        }

        private static double CalculateOperationalBurialRate(double rockIndex, double mudIndex)
        {
            // User-derived compatibility policy based on the existing SHOM operational mapping anchors:
            // sediment mud share 0~50% -> 5%, 70% -> 35%, 80% -> 65%, 100% -> 85%.
            // Rock reduces the buryable sediment fraction continuously. This is an operational estimate,
            // not a measured or literature-derived burial probability.
            var mudSharePercent = Clamp01(mudIndex) * 100.0;
            double sedimentPotential;
            if (mudSharePercent <= 50.0) sedimentPotential = 5.0;
            else if (mudSharePercent <= 70.0) sedimentPotential = Lerp(5.0, 35.0, (mudSharePercent - 50.0) / 20.0);
            else if (mudSharePercent <= 80.0) sedimentPotential = Lerp(35.0, 65.0, (mudSharePercent - 70.0) / 10.0);
            else sedimentPotential = Lerp(65.0, 85.0, (mudSharePercent - 80.0) / 20.0);
            return Math.Max(0.0, Math.Min(85.0, (1.0 - Clamp01(rockIndex)) * sedimentPotential));
        }

        private static double Lerp(double a, double b, double t) => a + ((b - a) * Clamp01(t));

        private static double Percentile(IReadOnlyList<double> sorted, double p)
        {
            if (sorted.Count == 0) return double.NaN;
            if (sorted.Count == 1) return sorted[0];
            var position = Clamp01(p) * (sorted.Count - 1);
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper) return sorted[lower];
            return Lerp(sorted[lower], sorted[upper], position - lower);
        }

        private sealed class TerrainCalibration
        {
            public TerrainCalibration(string sourceId, double slopeP75Degrees, double slopeP95Degrees, double roughnessP75Meters, double roughnessP95Meters)
            {
                SourceId = sourceId;
                SlopeP75Degrees = slopeP75Degrees;
                SlopeP95Degrees = slopeP95Degrees;
                RoughnessP75Meters = roughnessP75Meters;
                RoughnessP95Meters = roughnessP95Meters;
            }

            public string SourceId { get; }
            public double SlopeP75Degrees { get; }
            public double SlopeP95Degrees { get; }
            public double RoughnessP75Meters { get; }
            public double RoughnessP95Meters { get; }
        }
    }
}
