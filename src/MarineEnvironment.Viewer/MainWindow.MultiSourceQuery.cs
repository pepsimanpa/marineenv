using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MarineEnvironment.Models;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow
    {
        private async Task QueryAllSourcesAtViewportPosition(Point position)
        {
            if (_currentGrid == null)
                return;

            if (!TryGetRasterCellFromViewport(position, out var row, out var column))
                return;

            var latitude = _currentGrid.Latitudes[row];
            var longitude = _currentGrid.Longitudes[column];
            var date = GetSelectedQueryDate();

            double? depth = null;
            if (!string.IsNullOrWhiteSpace(DepthTextBox.Text)
                && double.TryParse(DepthTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDepth))
            {
                depth = parsedDepth;
            }

            try
            {
                PointQueryText.Text = "Querying all READY sources...";
                var result = await Task.Run(() => _manager.Query(new EnvironmentQuery
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    Depth = depth,
                    DateTime = date
                }));

                var derivedRows = result.Values.SelectMany(CreateDerivedRows).ToArray();

                PointQueryText.Text = derivedRows.Length == 0
                    ? $"Point API: {result.Count} value(s)"
                    : $"Point API: {result.Count} value(s) / {derivedRows.Length} derived";
                PointResultsHeaderText.Text = $"Requested: {result.RequestedLatitude:0.#####}, {result.RequestedLongitude:0.#####}"
                    + (result.RequestedDepth.HasValue ? $"  |  Depth {result.RequestedDepth:0.###} m" : string.Empty)
                    + $"  |  {result.RequestedDateTime:yyyy-MM-dd}";

                PointResultsGrid.ItemsSource = result.Values.Select(x => new PointResultRow
                {
                    Type = x.Type.ToString(),
                    Source = x.SourceId,
                    Value = FormatPointValue(x.Value),
                    Unit = x.Unit ?? string.Empty,
                    Mode = FormatMode(x),
                    Latitude = x.Latitude.ToString("0.#####", CultureInfo.InvariantCulture),
                    Longitude = x.Longitude.ToString("0.#####", CultureInfo.InvariantCulture),
                    Depth = x.Depth.HasValue ? x.Depth.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
                    Variable = x.Variable ?? string.Empty
                }).ToArray();

                DerivedResultsGrid.ItemsSource = derivedRows;
                DerivedResultsPanel.Visibility = derivedRows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                PointResultsPanel.Visibility = Visibility.Visible;
                StatusText.Text = derivedRows.Length == 0
                    ? $"Point query returned {result.Count} value(s) from READY sources."
                    : $"Point query returned {result.Count} value(s) and {derivedRows.Length} derived/estimated result(s).";
            }
            catch (Exception ex)
            {
                PointQueryText.Text = $"Point query error: {ex.Message}";
                StatusText.Text = "Point query failed.";
            }
        }

        private static IEnumerable<DerivedResultRow> CreateDerivedRows(EnvironmentValue value)
        {
            if (value.Type == EnvironmentType.Tss
                && value.Metadata != null
                && value.Metadata.TryGetValue("derivedTurbidityNtu", out var derivedTurbidity)
                && derivedTurbidity != null)
            {
                var ntu = Convert.ToDouble(derivedTurbidity, CultureInfo.InvariantCulture);
                var factor = value.Metadata.TryGetValue("tssToTurbidityFactor", out var factorValue) && factorValue != null
                    ? Convert.ToDouble(factorValue, CultureInfo.InvariantCulture)
                    : 0.3671;
                var validCount = value.Metadata.TryGetValue("validObservationCount", out var countValue) && countValue != null
                    ? Convert.ToInt32(countValue, CultureInfo.InvariantCulture)
                    : 0;
                var meanTss = Convert.ToDouble(value.Value, CultureInfo.InvariantCulture);

                yield return new DerivedResultRow
                {
                    Model = "TSS → Turbidity",
                    Source = value.SourceId,
                    Basis = string.Format(
                        CultureInfo.InvariantCulture,
                        "Mean TSS {0:0.###} mg/L × {1:0.####} ({2} valid obs.)",
                        meanTss,
                        factor,
                        validCount),
                    Classification = "Turbidity",
                    Seabed = ntu.ToString("0.###", CultureInfo.InvariantCulture),
                    BurialRate = "NTU"
                };
                yield break;
            }

            if (value.Value is SeabedValue seabed && seabed.Derived != null)
            {
                var derived = seabed.Derived;
                yield return new DerivedResultRow
                {
                    Model = derived.MappingTableId,
                    Source = value.SourceId,
                    Basis = $"{seabed.Code} | {derived.ShomOriginalClassification} → {derived.PrimaryClassification}",
                    Classification = derived.PrimaryClassification,
                    Seabed = derived.SeabedDisplay,
                    BurialRate = derived.BurialRatePercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                };
                yield break;
            }

            if (value.Value is EstimatedSeabedValue estimated)
            {
                yield return new DerivedResultRow
                {
                    Model = estimated.ModelId,
                    Source = $"{estimated.TerrainSourceId} + {estimated.PorositySourceId}",
                    Basis = string.Format(
                        CultureInfo.InvariantCulture,
                        "rockIdx {0:0.##}/{1:0.##} | P {2:0.#}% | slope {3:0.##}° | rough {4:0.##} m",
                        estimated.RockIndex,
                        estimated.RockDecisionThreshold,
                        estimated.PorosityPercent,
                        estimated.SlopeDegrees,
                        estimated.RoughnessMeters),
                    Classification = estimated.Classification,
                    Seabed = estimated.SeabedDisplay,
                    BurialRate = estimated.BurialRatePercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                };
            }
        }

        private static string FormatPointValue(object? value)
        {
            if (value is CurrentValue current)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.###} @ {1:0.#}°",
                    current.Speed,
                    current.Direction);
            }

            if (value is EstimatedSeabedValue estimated)
                return estimated.SeabedDisplay;

            return FormatObject(value);
        }

        private static string FormatMode(EnvironmentValue value)
        {
            if (value.Value is CurrentValue current)
            {
                if (current.ConstituentCount > 0)
                    return $"{current.ConstituentMode} ({current.ConstituentCount})";
                if (value.Metadata != null
                    && value.Metadata.TryGetValue("sourceTemporalResolution", out var temporal)
                    && temporal != null)
                {
                    return $"DailyVector ({temporal})";
                }
                return "Vector";
            }
            if (value.Value is EstimatedSeabedValue)
                return "DerivedEstimate";

            if (value.Type == EnvironmentType.Tss
                && value.Metadata != null
                && value.Metadata.TryGetValue("validObservationCount", out var validCount)
                && validCount != null)
            {
                return $"Mean TSS ({Convert.ToInt32(validCount, CultureInfo.InvariantCulture)} obs.)";
            }

            return string.Empty;
        }

        private sealed class PointResultRow
        {
            public string Type { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string Unit { get; set; } = string.Empty;
            public string Mode { get; set; } = string.Empty;
            public string Latitude { get; set; } = string.Empty;
            public string Longitude { get; set; } = string.Empty;
            public string Depth { get; set; } = string.Empty;
            public string Variable { get; set; } = string.Empty;
        }

        private sealed class DerivedResultRow
        {
            public string Model { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Basis { get; set; } = string.Empty;
            public string Classification { get; set; } = string.Empty;
            public string Seabed { get; set; } = string.Empty;
            public string BurialRate { get; set; } = string.Empty;
        }
    }
}
