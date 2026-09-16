using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace MarineEnvironment.Viewer
{
    public partial class NetCdfInspectorWindow : Window
    {
        private const int MaxNameLength = 256;
        private const int MaxSeriesCount = 10_000;
        private int _ncid = -1;
        private List<DimensionInfo> _dimensions = new List<DimensionInfo>();
        private List<VariableInfo> _variables = new List<VariableInfo>();
        private List<IndexRow> _indexRows = new List<IndexRow>();

        public NetCdfInspectorWindow()
        {
            InitializeComponent();
        }

        private void BrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "NetCDF files (*.nc;*.grd)|*.nc;*.grd|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true)
                return;

            FilePathTextBox.Text = dialog.FileName;
            OpenNetCdfFile(dialog.FileName);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var path = FilePathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "Select a NetCDF file first.", "NetCDF Inspector", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            OpenNetCdfFile(path);
        }

        private void OpenNetCdfFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("NetCDF file was not found.", path);

                CloseCurrentFile();
                ThrowIfError(Native.nc_open(path, Native.Nowrite, out _ncid), $"Open NetCDF '{path}'");
                LoadStructure();
                FilePathTextBox.Text = path;
                InspectorStatusText.Text = $"Opened {Path.GetFileName(path)} | {_dimensions.Count} dimension(s), {_variables.Count} variable(s).";
            }
            catch (Exception ex)
            {
                CloseCurrentFile();
                MessageBox.Show(this, ex.ToString(), "NetCDF Inspector error", MessageBoxButton.OK, MessageBoxImage.Error);
                InspectorStatusText.Text = "Failed to open NetCDF file.";
            }
        }

        private void LoadStructure()
        {
            EnsureOpen();
            ThrowIfError(Native.nc_inq_ndims(_ncid, out var dimensionCount), "Read dimension count");
            ThrowIfError(Native.nc_inq_nvars(_ncid, out var variableCount), "Read variable count");

            _dimensions = new List<DimensionInfo>(dimensionCount);
            for (var dimensionId = 0; dimensionId < dimensionCount; dimensionId++)
            {
                var name = new StringBuilder(MaxNameLength + 1);
                ThrowIfError(Native.nc_inq_dimname(_ncid, dimensionId, name), $"Read dimension name {dimensionId}");
                ThrowIfError(Native.nc_inq_dimlen(_ncid, dimensionId, out var length), $"Read dimension length '{name}'");
                var rawLength = length.ToUInt64();
                if (rawLength > long.MaxValue)
                    throw new InvalidDataException($"Dimension '{name}' is too large for this inspector.");
                _dimensions.Add(new DimensionInfo(dimensionId, name.ToString(), (long)rawLength));
            }

            _variables = new List<VariableInfo>(variableCount);
            for (var variableId = 0; variableId < variableCount; variableId++)
            {
                var name = new StringBuilder(MaxNameLength + 1);
                ThrowIfError(Native.nc_inq_varname(_ncid, variableId, name), $"Read variable name {variableId}");
                ThrowIfError(Native.nc_inq_vartype(_ncid, variableId, out var typeId), $"Read variable type '{name}'");
                ThrowIfError(Native.nc_inq_varndims(_ncid, variableId, out var dimensionNumber), $"Read variable dimensions '{name}'");
                var dimensionIds = new int[dimensionNumber];
                if (dimensionNumber > 0)
                    ThrowIfError(Native.nc_inq_vardimid(_ncid, variableId, dimensionIds), $"Read variable dimension ids '{name}'");

                var dimensions = dimensionIds.Select(GetDimension).ToArray();
                string? fillValueText = null;
                if (Native.nc_get_att_double(_ncid, variableId, "_FillValue", out var fillValue) == Native.NoError)
                    fillValueText = FormatRaw(fillValue);
                else if (Native.nc_get_att_double(_ncid, variableId, "missing_value", out var missingValue) == Native.NoError)
                    fillValueText = FormatRaw(missingValue);

                var variable = new VariableInfo(
                    variableId,
                    name.ToString(),
                    TypeName(typeId),
                    dimensionIds,
                    dimensions,
                    fillValueText);

                if (dimensions.Length == 1 && dimensions[0].Length > 0)
                {
                    if (TryReadRawValue(variable, new[] { 0L }, out var first))
                        variable.FirstValueText = FormatRaw(first);
                    if (TryReadRawValue(variable, new[] { dimensions[0].Length - 1 }, out var last))
                        variable.LastValueText = FormatRaw(last);
                }

                _variables.Add(variable);
            }

            DimensionsGrid.ItemsSource = _dimensions;
            VariablesGrid.ItemsSource = _variables;
            VariablesGrid.SelectedItem = _variables.FirstOrDefault(v => v.DimensionIds.Length > 0) ?? _variables.FirstOrDefault();
        }

        private void VariablesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VariablesGrid.SelectedItem is not VariableInfo variable)
            {
                _indexRows = new List<IndexRow>();
                IndicesGrid.ItemsSource = null;
                SeriesDimensionComboBox.ItemsSource = null;
                return;
            }

            _indexRows = variable.Dimensions
                .Select(dimension => new IndexRow(dimension.Id, dimension.Name, dimension.Length))
                .ToList();
            UpdateCoordinateTexts();
            IndicesGrid.ItemsSource = _indexRows;
            SeriesDimensionComboBox.ItemsSource = _indexRows;

            var preferred = _indexRows.FirstOrDefault(x => string.Equals(x.DimensionName, "time", StringComparison.OrdinalIgnoreCase))
                            ?? _indexRows.LastOrDefault();
            SeriesDimensionComboBox.SelectedItem = preferred;
            SeriesStartTextBox.Text = "0";
            if (preferred != null)
            {
                var defaultCount = string.Equals(preferred.DimensionName, "time", StringComparison.OrdinalIgnoreCase)
                    ? Math.Min(preferred.Length, 2000)
                    : Math.Min(preferred.Length, 100);
                SeriesCountTextBox.Text = defaultCount.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                SeriesCountTextBox.Text = "0";
            }

            SelectedValueText.Text = "Value: -";
            PeerRecordTextBox.Text = string.Empty;
            SeriesGrid.ItemsSource = null;
        }

        private void ReadValue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var variable = RequireSelectedVariable();
                CommitIndexEdits();
                ValidateIndices(variable);
                UpdateCoordinateTexts();
                IndicesGrid.Items.Refresh();

                var raw = ReadRawValue(variable, _indexRows.Select(x => x.Index).ToArray());
                SelectedValueText.Text = $"{variable.Name} = {FormatRaw(raw)}";
                InspectorStatusText.Text = $"Read {variable.Name} at [{string.Join(", ", _indexRows.Select(x => x.Index))}].";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Read value", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ReadPeerRecord_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var variable = RequireSelectedVariable();
                CommitIndexEdits();
                ValidateIndices(variable);
                if (variable.DimensionIds.Length != 1)
                    throw new InvalidOperationException("This helper is available for one-dimensional variables only. Select LAT/LON/MSL for BADA-style paired point data.");

                var dimensionId = variable.DimensionIds[0];
                var index = _indexRows[0].Index;
                var peers = _variables
                    .Where(x => x.DimensionIds.Length == 1 && x.DimensionIds[0] == dimensionId)
                    .ToArray();

                var parts = new List<string>();
                foreach (var peer in peers)
                {
                    if (TryReadRawValue(peer, new[] { index }, out var value))
                        parts.Add($"{peer.Name}[{index}] = {FormatRaw(value)}");
                    else
                        parts.Add($"{peer.Name}[{index}] = <non-numeric/unreadable>");
                }

                PeerRecordTextBox.Text = string.Join(Environment.NewLine, parts);
                InspectorStatusText.Text = $"Read {peers.Length} one-dimensional variable(s) sharing dimension '{variable.Dimensions[0].Name}' at index {index}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Read same-index record", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ReadSeries_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var variable = RequireSelectedVariable();
                CommitIndexEdits();
                ValidateIndices(variable);
                if (SeriesDimensionComboBox.SelectedItem is not IndexRow seriesDimension)
                    throw new InvalidOperationException("Select the dimension to read as a series.");
                if (!long.TryParse(SeriesStartTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) || start < 0)
                    throw new InvalidOperationException("Series start must be a non-negative integer.");
                if (!int.TryParse(SeriesCountTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 1)
                    throw new InvalidOperationException("Series count must be a positive integer.");
                if (count > MaxSeriesCount)
                    throw new InvalidOperationException($"Series count is limited to {MaxSeriesCount:N0} values per read.");
                if (start >= seriesDimension.Length || start + count > seriesDimension.Length)
                    throw new InvalidOperationException($"Requested range {start}..{start + count - 1} exceeds dimension '{seriesDimension.DimensionName}' length {seriesDimension.Length:N0}.");

                var varyingPosition = Array.IndexOf(variable.DimensionIds, seriesDimension.DimensionId);
                if (varyingPosition < 0)
                    throw new InvalidOperationException("Selected series dimension is not part of the selected variable.");

                var indices = _indexRows.Select(x => x.Index).ToArray();
                var dimension = GetDimension(seriesDimension.DimensionId);
                var rows = new List<SeriesRow>(count);
                for (var offset = 0; offset < count; offset++)
                {
                    var index = start + offset;
                    indices[varyingPosition] = index;
                    var value = ReadRawValue(variable, indices);
                    var coordinate = TryReadCoordinateValue(dimension, index, out var coordinateValue)
                        ? FormatRaw(coordinateValue)
                        : string.Empty;
                    rows.Add(new SeriesRow(index, coordinate, FormatRaw(value)));
                }

                SeriesGrid.ItemsSource = rows;
                InspectorStatusText.Text = $"Read {count:N0} {variable.Name} value(s) along '{seriesDimension.DimensionName}' from index {start}.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Read series", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CommitIndexEdits()
        {
            IndicesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            IndicesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void ValidateIndices(VariableInfo variable)
        {
            if (_indexRows.Count != variable.Dimensions.Length)
                throw new InvalidOperationException("Index editor does not match the selected variable.");
            for (var i = 0; i < _indexRows.Count; i++)
            {
                var row = _indexRows[i];
                if (row.Index < 0 || row.Index >= row.Length)
                    throw new InvalidOperationException($"Index for '{row.DimensionName}' must be between 0 and {row.Length - 1}.");
            }
        }

        private void UpdateCoordinateTexts()
        {
            foreach (var row in _indexRows)
            {
                if (row.Index < 0 || row.Index >= row.Length)
                {
                    row.CoordinateText = string.Empty;
                    continue;
                }

                var dimension = GetDimension(row.DimensionId);
                row.CoordinateText = TryReadCoordinateValue(dimension, row.Index, out var value)
                    ? FormatRaw(value)
                    : string.Empty;
            }
        }

        private bool TryReadCoordinateValue(DimensionInfo dimension, long index, out double value)
        {
            value = default;
            var coordinateVariable = _variables.FirstOrDefault(x =>
                string.Equals(x.Name, dimension.Name, StringComparison.OrdinalIgnoreCase) &&
                x.DimensionIds.Length == 1 &&
                x.DimensionIds[0] == dimension.Id);
            return coordinateVariable != null && TryReadRawValue(coordinateVariable, new[] { index }, out value);
        }

        private VariableInfo RequireSelectedVariable()
        {
            EnsureOpen();
            return VariablesGrid.SelectedItem as VariableInfo
                   ?? throw new InvalidOperationException("Select a variable first.");
        }

        private double ReadRawValue(VariableInfo variable, IReadOnlyList<long> indices)
        {
            EnsureOpen();
            if (variable.DimensionIds.Length == 0)
            {
                var scalar = new double[1];
                ThrowIfError(Native.nc_get_var_double(_ncid, variable.Id, scalar), $"Read scalar '{variable.Name}'");
                return scalar[0];
            }

            if (indices.Count != variable.DimensionIds.Length)
                throw new InvalidOperationException($"Variable '{variable.Name}' requires {variable.DimensionIds.Length} indices.");

            var nativeIndices = new UIntPtr[indices.Count];
            for (var i = 0; i < indices.Count; i++)
            {
                if (indices[i] < 0)
                    throw new InvalidOperationException("NetCDF indices cannot be negative.");
                nativeIndices[i] = new UIntPtr((ulong)indices[i]);
            }

            ThrowIfError(Native.nc_get_var1_double(_ncid, variable.Id, nativeIndices, out var value), $"Read '{variable.Name}'");
            return value;
        }

        private bool TryReadRawValue(VariableInfo variable, IReadOnlyList<long> indices, out double value)
        {
            try
            {
                value = ReadRawValue(variable, indices);
                return true;
            }
            catch
            {
                value = default;
                return false;
            }
        }

        private DimensionInfo GetDimension(int id)
        {
            return _dimensions.First(x => x.Id == id);
        }

        private void EnsureOpen()
        {
            if (_ncid < 0)
                throw new InvalidOperationException("Open a NetCDF file first.");
        }

        private void CloseCurrentFile()
        {
            if (_ncid >= 0)
            {
                Native.nc_close(_ncid);
                _ncid = -1;
            }
            _dimensions = new List<DimensionInfo>();
            _variables = new List<VariableInfo>();
            _indexRows = new List<IndexRow>();
            DimensionsGrid.ItemsSource = null;
            VariablesGrid.ItemsSource = null;
            IndicesGrid.ItemsSource = null;
            SeriesDimensionComboBox.ItemsSource = null;
            SeriesGrid.ItemsSource = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            CloseCurrentFile();
            base.OnClosed(e);
        }

        private static string TypeName(int typeId)
        {
            return typeId switch
            {
                1 => "byte",
                2 => "char",
                3 => "short",
                4 => "int",
                5 => "float",
                6 => "double",
                7 => "ubyte",
                8 => "ushort",
                9 => "uint",
                10 => "int64",
                11 => "uint64",
                12 => "string",
                _ => $"type {typeId}"
            };
        }

        private static string FormatRaw(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "+Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static void ThrowIfError(int code, string operation)
        {
            if (code == Native.NoError)
                return;
            throw new InvalidDataException($"{operation}: {Native.ErrorText(code)} ({code})");
        }

        private sealed class DimensionInfo
        {
            public DimensionInfo(int id, string name, long length)
            {
                Id = id;
                Name = name;
                Length = length;
            }
            public int Id { get; }
            public string Name { get; }
            public long Length { get; }
            public string LengthText => Length.ToString("N0", CultureInfo.InvariantCulture);
        }

        private sealed class VariableInfo
        {
            public VariableInfo(int id, string name, string typeName, int[] dimensionIds, DimensionInfo[] dimensions, string? fillValueText)
            {
                Id = id;
                Name = name;
                TypeName = typeName;
                DimensionIds = dimensionIds;
                Dimensions = dimensions;
                FillValueText = fillValueText ?? string.Empty;
            }
            public int Id { get; }
            public string Name { get; }
            public string TypeName { get; }
            public int[] DimensionIds { get; }
            public DimensionInfo[] Dimensions { get; }
            public string DimensionText => Dimensions.Length == 0 ? "scalar" : string.Join(" × ", Dimensions.Select(x => $"{x.Name}[{x.Length}]") );
            public string FillValueText { get; }
            public string FirstValueText { get; set; } = string.Empty;
            public string LastValueText { get; set; } = string.Empty;
        }

        private sealed class IndexRow
        {
            public IndexRow(int dimensionId, string dimensionName, long length)
            {
                DimensionId = dimensionId;
                DimensionName = dimensionName;
                Length = length;
            }
            public int DimensionId { get; }
            public string DimensionName { get; }
            public long Length { get; }
            public string LengthText => Length.ToString("N0", CultureInfo.InvariantCulture);
            public long Index { get; set; }
            public string CoordinateText { get; set; } = string.Empty;
        }

        private sealed class SeriesRow
        {
            public SeriesRow(long index, string coordinateText, string valueText)
            {
                Index = index;
                CoordinateText = coordinateText;
                ValueText = valueText;
            }
            public long Index { get; }
            public string CoordinateText { get; }
            public string ValueText { get; }
        }

        private static class Native
        {
            private const string LibraryName = "netcdf";
            internal const int NoError = 0;
            internal const int Nowrite = 0;

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_open(string path, int mode, out int ncidp);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_close(int ncid);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_ndims(int ncid, out int ndimsp);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_nvars(int ncid, out int nvarsp);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_dimname(int ncid, int dimid, StringBuilder name);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_dimlen(int ncid, int dimid, out UIntPtr lenp);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varname(int ncid, int varid, StringBuilder name);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_vartype(int ncid, int varid, out int xtypep);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_varndims(int ncid, int varid, out int ndimsp);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_inq_vardimid(int ncid, int varid, [Out] int[] dimidsp);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_var_double(int ncid, int varid, [Out] double[] value);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_var1_double(int ncid, int varid, UIntPtr[] indexp, out double value);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            internal static extern int nc_get_att_double(int ncid, int varid, string name, out double value);

            [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
            private static extern IntPtr nc_strerror(int ncerr);

            internal static string ErrorText(int code)
            {
                var ptr = nc_strerror(code);
                return ptr == IntPtr.Zero ? $"NetCDF error {code}" : Marshal.PtrToStringAnsi(ptr) ?? $"NetCDF error {code}";
            }
        }
    }
}
