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
        private bool _pointQueryInProgress;

        private async Task QueryAllSourcesAtViewportPosition(Point position)
        {
            if (_currentGrid == null)
                return;

            if (_pointQueryInProgress)
            {
                PointQueryText.Text = "Point query already running...";
                return;
            }

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

            _pointQueryInProgress = true;
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

                var derivedRows = result.DerivedValues.SelectMany(CreateDerivedRows).ToArray();

                PointQueryText.Text =
                    $"Point API: {result.SourceCount} source / {result.DerivedCount} derived";
                PointResultsHeaderText.Text = $"Requested: {result.RequestedLatitude:0.#####}, {result.RequestedLongitude:0.#####}"
                    + (result.RequestedDepth.HasValue ? $"  |  Depth {result.RequestedDepth:0.###} m" : string.Empty)
                    + $"  |  {result.RequestedDateTime:yyyy-MM-dd}";

                PointResultsGrid.ItemsSource = result.SourceValues.Select(x => new PointResultRow
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
                StatusText.Text =
                    $"Point query returned {result.SourceCount} source value(s) and {result.DerivedCount} derived/estimated value(s).";
            }
            catch (Exception ex)
            {
                PointQueryText.Text = $"Point query error: {ex.Message}";
                StatusText.Text = "Point query failed.";
            }
            finally
            {
                _pointQueryInProgress = false;
            }
        }

        private static IEnumerable<DerivedResultRow> CreateDerivedRows(EnvironmentValue value)
        {
            if (value.Type == EnvironmentType.Turbidity && value.Value is double turbidity)
            {
                var factor = TryMetadataDouble(value, "tssToTurbidityFactor") ?? 0.3671;
                var meanTss = TryMetadataDouble(value, "tssMeanMgL");
                var validCount = TryMetadataInt(value, "validObservationCount");
                var basis = meanTss.HasValue
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Mean TSS {0:0.###} mg/L × {1:0.####}{2}",
                        meanTss.Value,
                        factor,
                        validCount.HasValue ? $" ({validCount.Value} valid obs.)" : string.Empty)
                    : "Derived from GOCI-II TSS";

                yield return new DerivedResultRow
                {
                    Model = TryMetadataString(value, "model") ?? "TSS → Turbidity",
                    Source = value.SourceId,
                    Basis = basis,
                    Classification = "Turbidity",
                    Seabed = turbidity.ToString("0.###", CultureInfo.InvariantCulture),
                    BurialRate = value.Unit ?? "NTU"
                };
                yield break;
            }

            if (value.Value is SeabedDerivedValue seabed)
            {
                yield return new DerivedResultRow
                {
                    Model = seabed.MappingTableId,
                    Source = value.SourceId,
                    Basis = $"{seabed.ShomOriginalClassification} → {seabed.PrimaryClassification}",
                    Classification = seabed.PrimaryClassification,
                    Seabed = seabed.SeabedDisplay,
                    BurialRate = seabed.BurialRatePercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
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

        private static double? TryMetadataDouble(EnvironmentValue value, string key)
        {
            if (value.Metadata == null || !value.Metadata.TryGetValue(key, out var raw) || raw == null)
                return null;
            try { return Convert.ToDouble(raw, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static int? TryMetadataInt(EnvironmentValue value, string key)
        {
            if (value.Metadata == null || !value.Metadata.TryGetValue(key, out var raw) || raw == null)
                return null;
            try { return Convert.ToInt32(raw, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static string? TryMetadataString(EnvironmentValue value, string key)
        {
            if (value.Metadata == null || !value.Metadata.TryGetValue(key, out var raw) || raw == null)
                return null;
            return Convert.ToString(raw, CultureInfo.InvariantCulture);
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
