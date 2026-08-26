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

                PointQueryText.Text = $"Point API: {result.Count} value(s)";
                PointResultsHeaderText.Text = $"Requested: {result.RequestedLatitude:0.#####}, {result.RequestedLongitude:0.#####}"
                    + (result.RequestedDepth.HasValue ? $"  |  Depth {result.RequestedDepth:0.###} m" : string.Empty)
                    + $"  |  {result.RequestedDateTime:yyyy-MM-dd}";
                PointResultsGrid.ItemsSource = result.Values.Select(x => new PointResultRow
                {
                    Type = x.Type.ToString(),
                    Source = x.SourceId,
                    Value = FormatObject(x.Value),
                    Unit = x.Unit ?? string.Empty,
                    Latitude = x.Latitude.ToString("0.#####", CultureInfo.InvariantCulture),
                    Longitude = x.Longitude.ToString("0.#####", CultureInfo.InvariantCulture),
                    Depth = x.Depth.HasValue ? x.Depth.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty,
                    Variable = x.Variable ?? string.Empty
                }).ToArray();
                PointResultsPanel.Visibility = Visibility.Visible;
                StatusText.Text = $"Point query returned {result.Count} value(s) from READY sources.";
            }
            catch (Exception ex)
            {
                PointQueryText.Text = $"Point query error: {ex.Message}";
                StatusText.Text = "Point query failed.";
            }
        }

        private sealed class PointResultRow
        {
            public string Type { get; set; } = string.Empty;
            public string Source { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
            public string Unit { get; set; } = string.Empty;
            public string Latitude { get; set; } = string.Empty;
            public string Longitude { get; set; } = string.Empty;
            public string Depth { get; set; } = string.Empty;
            public string Variable { get; set; } = string.Empty;
        }
    }
}
