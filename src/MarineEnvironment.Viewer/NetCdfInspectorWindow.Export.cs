using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MarineEnvironment.Viewer
{
    public partial class NetCdfInspectorWindow
    {
        private void ShowTextExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExportOutputTextBox.Text = BuildTextExport();
                InspectorStatusText.Text = "Generated copy/paste text export from the current inspector state.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export text", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowJsonExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ExportOutputTextBox.Text = BuildJsonExport();
                InspectorStatusText.Text = "Generated copy/paste JSON export from the current inspector state.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Export JSON", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CopyExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ExportOutputTextBox.Text))
                    ExportOutputTextBox.Text = BuildTextExport();

                Clipboard.SetText(ExportOutputTextBox.Text);
                InspectorStatusText.Text = $"Copied {ExportOutputTextBox.Text.Length:N0} characters to the clipboard.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Copy export", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private string BuildTextExport()
        {
            EnsureOpen();

            var selectedVariable = VariablesGrid.SelectedItem as VariableInfo;
            var seriesRows = GetCurrentSeriesRows();
            var builder = new StringBuilder();

            builder.AppendLine("NETCDF_INSPECTOR_EXPORT");
            builder.AppendLine("schemaVersion=1");
            builder.AppendLine($"fileName={GetExportFileName()}");
            builder.AppendLine("localPath=<omitted>");
            builder.AppendLine();

            builder.AppendLine("[dimensions]");
            foreach (var dimension in _dimensions)
                builder.AppendLine($"{dimension.Name}={dimension.Length}");
            builder.AppendLine();

            builder.AppendLine("[variables]");
            foreach (var variable in _variables)
            {
                builder.Append(variable.Name)
                    .Append(" | type=").Append(variable.TypeName)
                    .Append(" | dimensions=").Append(variable.DimensionText);

                if (!string.IsNullOrWhiteSpace(variable.FillValueText))
                    builder.Append(" | fill=").Append(variable.FillValueText);
                if (!string.IsNullOrWhiteSpace(variable.FirstValueText))
                    builder.Append(" | first=").Append(variable.FirstValueText);
                if (!string.IsNullOrWhiteSpace(variable.LastValueText))
                    builder.Append(" | last=").Append(variable.LastValueText);

                builder.AppendLine();
            }
            builder.AppendLine();

            builder.AppendLine("[selection]");
            builder.AppendLine($"variable={selectedVariable?.Name ?? string.Empty}");
            foreach (var row in _indexRows)
            {
                builder.Append(row.DimensionName)
                    .Append(" | index=").Append(row.Index)
                    .Append(" | length=").Append(row.Length);

                if (!string.IsNullOrWhiteSpace(row.CoordinateText))
                    builder.Append(" | coordinate=").Append(row.CoordinateText);

                builder.AppendLine();
            }

            var selectedValue = GetSelectedValueExportText();
            if (!string.IsNullOrWhiteSpace(selectedValue))
                builder.AppendLine($"value={selectedValue}");

            if (!string.IsNullOrWhiteSpace(PeerRecordTextBox.Text))
            {
                builder.AppendLine();
                builder.AppendLine("[sameIndexRecord]");
                builder.AppendLine(PeerRecordTextBox.Text.TrimEnd());
            }

            builder.AppendLine();
            builder.AppendLine("[series]");
            var seriesDimension = SeriesDimensionComboBox.SelectedItem as IndexRow;
            builder.AppendLine($"dimension={seriesDimension?.DimensionName ?? string.Empty}");
            builder.AppendLine($"requestedStart={SeriesStartTextBox.Text.Trim()}");
            builder.AppendLine($"requestedCount={SeriesCountTextBox.Text.Trim()}");
            builder.AppendLine($"actualCount={seriesRows.Count}");

            foreach (var row in seriesRows)
            {
                builder.Append("index=").Append(row.Index);
                if (!string.IsNullOrWhiteSpace(row.CoordinateText))
                    builder.Append(" | coordinate=").Append(row.CoordinateText);
                builder.Append(" | value=").Append(row.ValueText);
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private string BuildJsonExport()
        {
            EnsureOpen();

            var selectedVariable = VariablesGrid.SelectedItem as VariableInfo;
            var seriesDimension = SeriesDimensionComboBox.SelectedItem as IndexRow;
            var seriesRows = GetCurrentSeriesRows();

            var export = new
            {
                schemaVersion = 1,
                fileName = GetExportFileName(),
                localPath = (string?)null,
                dimensions = _dimensions.Select(x => new
                {
                    name = x.Name,
                    length = x.Length
                }).ToArray(),
                variables = _variables.Select(x => new
                {
                    name = x.Name,
                    type = x.TypeName,
                    dimensions = x.Dimensions.Select(d => new
                    {
                        name = d.Name,
                        length = d.Length
                    }).ToArray(),
                    fillValue = EmptyToNull(x.FillValueText),
                    firstValue = EmptyToNull(x.FirstValueText),
                    lastValue = EmptyToNull(x.LastValueText)
                }).ToArray(),
                selection = new
                {
                    variable = selectedVariable?.Name,
                    indices = _indexRows.Select(x => new
                    {
                        dimension = x.DimensionName,
                        length = x.Length,
                        index = x.Index,
                        coordinate = EmptyToNull(x.CoordinateText)
                    }).ToArray(),
                    value = EmptyToNull(GetSelectedValueExportText()),
                    sameIndexRecord = EmptyToNull(PeerRecordTextBox.Text)
                },
                series = new
                {
                    dimension = seriesDimension?.DimensionName,
                    requestedStart = SeriesStartTextBox.Text.Trim(),
                    requestedCount = SeriesCountTextBox.Text.Trim(),
                    actualCount = seriesRows.Count,
                    rows = seriesRows.Select(x => new
                    {
                        index = x.Index,
                        coordinate = EmptyToNull(x.CoordinateText),
                        value = x.ValueText
                    }).ToArray()
                }
            };

            return JsonSerializer.Serialize(export, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        private List<SeriesRow> GetCurrentSeriesRows()
        {
            if (SeriesGrid.ItemsSource is IEnumerable<SeriesRow> rows)
                return rows.ToList();

            return SeriesGrid.Items.OfType<SeriesRow>().ToList();
        }

        private string GetExportFileName()
        {
            var path = FilePathTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path);
        }

        private string? GetSelectedValueExportText()
        {
            var text = SelectedValueText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "Value: -", StringComparison.Ordinal))
                return null;
            return text;
        }

        private static string? EmptyToNull(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
