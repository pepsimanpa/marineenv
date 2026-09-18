using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace MarineEnvironment.Viewer
{
    public partial class NetCdfInspectorWindow
    {
        private void FindNearestIndices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var variable = RequireSelectedVariable();
                CommitIndexEdits();

                var applied = new List<string>();

                ApplyNearestCoordinate(
                    variable,
                    TargetLongitudeTextBox.Text,
                    "longitude",
                    new[] { "lon", "longitude" },
                    applied);

                ApplyNearestCoordinate(
                    variable,
                    TargetLatitudeTextBox.Text,
                    "latitude",
                    new[] { "lat", "latitude" },
                    applied);

                ApplyNearestCoordinate(
                    variable,
                    TargetDepthTextBox.Text,
                    "depth",
                    new[] { "depth", "lev", "level", "z" },
                    applied);

                if (applied.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No coordinate index was changed. Enter Lon/Lat/Depth values and select a variable that contains matching coordinate dimensions.");
                }

                UpdateCoordinateTexts();
                IndicesGrid.Items.Refresh();

                // Existing sample/series results may refer to the old indices.
                SelectedValueText.Text = "Value: -";
                PeerRecordTextBox.Text = string.Empty;
                SeriesGrid.ItemsSource = null;
                ExportOutputTextBox.Text = string.Empty;

                var summary = string.Join(" | ", applied);
                NearestCoordinateResultText.Text = summary;
                InspectorStatusText.Text = "Nearest coordinate indices selected: " + summary;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Find nearest indices", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ApplyNearestCoordinate(
            VariableInfo variable,
            string rawTarget,
            string displayName,
            IReadOnlyCollection<string> aliases,
            ICollection<string> applied)
        {
            if (string.IsNullOrWhiteSpace(rawTarget))
                return;

            if (!TryParseCoordinate(rawTarget, out var target))
                throw new InvalidOperationException($"'{rawTarget}' is not a valid {displayName} value.");

            var row = _indexRows.FirstOrDefault(x => aliases.Contains(x.DimensionName, StringComparer.OrdinalIgnoreCase));
            if (row == null)
                return;

            if (!variable.DimensionIds.Contains(row.DimensionId))
                return;

            var coordinateVariable = FindCoordinateVariable(row.DimensionId, aliases);
            if (coordinateVariable == null)
                throw new InvalidOperationException(
                    $"Dimension '{row.DimensionName}' does not have a readable 1D coordinate variable.");

            var nearestIndex = FindNearestCoordinateIndex(coordinateVariable, row.Length, target, out var nearestValue);
            row.Index = nearestIndex;

            applied.Add(
                $"{displayName} {FormatRaw(target)} -> {row.DimensionName}[{nearestIndex}]={FormatRaw(nearestValue)}");
        }

        private VariableInfo? FindCoordinateVariable(int dimensionId, IReadOnlyCollection<string> aliases)
        {
            var dimension = GetDimension(dimensionId);

            var exact = _variables.FirstOrDefault(x =>
                x.DimensionIds.Length == 1 &&
                x.DimensionIds[0] == dimensionId &&
                string.Equals(x.Name, dimension.Name, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
                return exact;

            return _variables.FirstOrDefault(x =>
                x.DimensionIds.Length == 1 &&
                x.DimensionIds[0] == dimensionId &&
                aliases.Contains(x.Name, StringComparer.OrdinalIgnoreCase));
        }

        private long FindNearestCoordinateIndex(
            VariableInfo coordinateVariable,
            long length,
            double target,
            out double nearestValue)
        {
            if (length <= 0)
                throw new InvalidOperationException($"Coordinate variable '{coordinateVariable.Name}' is empty.");

            var bestIndex = -1L;
            nearestValue = double.NaN;
            var bestDistance = double.PositiveInfinity;

            for (var index = 0L; index < length; index++)
            {
                if (!TryReadRawValue(coordinateVariable, new[] { index }, out var value))
                    continue;
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;

                var distance = Math.Abs(value - target);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                bestIndex = index;
                nearestValue = value;

                if (distance == 0.0)
                    break;
            }

            if (bestIndex < 0)
                throw new InvalidOperationException(
                    $"Coordinate variable '{coordinateVariable.Name}' contains no readable numeric coordinates.");

            return bestIndex;
        }

        private static bool TryParseCoordinate(string text, out double value)
        {
            return double.TryParse(
                       text.Trim(),
                       NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.InvariantCulture,
                       out value)
                   || double.TryParse(
                       text.Trim(),
                       NumberStyles.Float | NumberStyles.AllowThousands,
                       CultureInfo.CurrentCulture,
                       out value);
        }
    }
}
