using System;
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
            var month = Math.Max(1, Math.Min(12, MonthComboBox.SelectedIndex + 1));
            var date = new DateTime(DateTime.Now.Year, month, 15, 12, 0, 0, DateTimeKind.Local);

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

                var derivedRows = result.Values
                    .Where(x => x.Value is SeabedValue seabed && seabed.Derived != null)
                    .Select(x =>
                    {
                        var seabed = (SeabedValue)x.Value!;
                        var derived = seabed.Derived!;
                        return new DerivedResultRow
                        {
                            Mapping = derived.MappingTableId,
                            Source = x.SourceId,
                            ShomCode = seabed.Code,
                            ShomOriginal = derived.ShomOriginalClassification,
                            Primary = derived.PrimaryClassification,
                            Seabed = derived.SeabedDisplay,
                            BurialRate = derived.BurialRatePercent.ToString("0.#", CultureInfo.InvariantCulture) + "%"
                        };
                    })
                    .ToArray();

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
                    Mode = FormatMode(x.Value),
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
                    : $"Point query returned {result.Count} source value(s) and {derivedRows.Length} user-derived result(s).";
            }
            catch (Exception ex)
            {
                PointQueryText.Text = $"Point query error: {ex.Message}";
                StatusText.Text = "Point query failed.";
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

            return FormatObject(value);
        }

        private static string FormatMode(object? value)
        {
            if (value is CurrentValue current)
                return $"{current.ConstituentMode} ({current.ConstituentCount})";

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
            public string Mapping { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string ShomCode { get; set; } = string.Empty;
            public string ShomOriginal { get; set; } = string.Empty;
            public string Primary { get; set; } = string.Empty;
            public string Seabed { get; set; } = string.Empty;
            public string BurialRate { get; set; } = string.Empty;
        }
    }
}
