using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MarineEnvironment.Models;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow : Window
    {
        private readonly MarineEnvironmentManager _manager = new MarineEnvironmentManager();
        private GridResult? _currentGrid;
        private SourceState? _selectedSource;

        public MainWindow()
        {
            InitializeComponent();
            Closed += (_, _) => _manager.Dispose();
        }

        private void BrowseConfig_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "MarineEnvironment config (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
                ConfigPathTextBox.Text = dialog.FileName;
        }

        private void LoadConfig_Click(object sender, RoutedEventArgs e)
        {
            var path = ConfigPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "Select marineenvironment.json first.", "MarineEnvironment Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                StatusText.Text = "Loading configuration...";
                var result = _manager.Initialize(path);
                SourceListBox.ItemsSource = result.Sources;
                SourceListBox.SelectedItem = result.Sources.FirstOrDefault(x => x.Status == SourceStatus.Ready)
                                             ?? result.Sources.FirstOrDefault();
                StatusText.Text = result.Success
                    ? $"Configuration loaded. {result.Sources.Count} source(s)."
                    : "Configuration loaded with one or more unavailable sources.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Configuration load failed.";
                MessageBox.Show(this, ex.Message, "Configuration error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SourceListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedSource = SourceListBox.SelectedItem as SourceState;
            if (_selectedSource == null)
            {
                SelectedSourceStatusText.Text = string.Empty;
                return;
            }

            SelectedSourceStatusText.Text = $"{_selectedSource.Type} / {_selectedSource.Status}"
                + (string.IsNullOrWhiteSpace(_selectedSource.Message) ? string.Empty : $"\n{_selectedSource.Message}");
        }

        private async void Render_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSource == null)
            {
                MessageBox.Show(this, "Select a data source first.", "MarineEnvironment Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_selectedSource.Status != SourceStatus.Ready)
            {
                MessageBox.Show(this, $"Source '{_selectedSource.Id}' is not READY.", "MarineEnvironment Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryReadDouble(MinLatTextBox, out var minLat)
                || !TryReadDouble(MaxLatTextBox, out var maxLat)
                || !TryReadDouble(MinLonTextBox, out var minLon)
                || !TryReadDouble(MaxLonTextBox, out var maxLon)
                || !TryReadInt(GridWidthTextBox, out var width)
                || !TryReadInt(GridHeightTextBox, out var height))
            {
                MessageBox.Show(this, "Check the view bounds and sampling size.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double? depth = null;
            if (!string.IsNullOrWhiteSpace(DepthTextBox.Text))
            {
                if (!TryReadDouble(DepthTextBox, out var parsedDepth))
                {
                    MessageBox.Show(this, "Depth must be numeric or blank.", "Invalid input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                depth = parsedDepth;
            }

            var month = Math.Clamp(MonthComboBox.SelectedIndex + 1, 1, 12);
            var queryDate = new DateTime(DateTime.Now.Year, month, 15, 12, 0, 0, DateTimeKind.Local);
            var sourceId = _selectedSource.Id;
            var query = new GridQuery
            {
                MinLatitude = minLat,
                MaxLatitude = maxLat,
                MinLongitude = minLon,
                MaxLongitude = maxLon,
                Depth = depth,
                DateTime = queryDate,
                Width = width,
                Height = height
            };

            try
            {
                RenderButton.IsEnabled = false;
                StatusText.Text = $"Rendering {sourceId} ({width} x {height})...";
                Mouse.OverrideCursor = Cursors.Wait;

                var grid = await Task.Run(() => _manager.QueryGrid(sourceId, query));
                _currentGrid = grid;
                MapImage.Source = CreateBitmap(grid);
                EmptyMapText.Visibility = Visibility.Collapsed;
                MapTitleText.Text = $"{grid.SourceId} / {grid.Type}";
                RangeText.Text = !grid.Minimum.HasValue || !grid.Maximum.HasValue
                    ? "No valid values"
                    : $"Min {FormatValue(grid.Minimum)}  |  Max {FormatValue(grid.Maximum)}  {grid.Unit}";
                CursorInfoText.Text = "Move the pointer over the raster to inspect values.";
                PointQueryText.Text = !grid.Depth.HasValue ? string.Empty : $"Depth: {grid.Depth:0.###} m";
                StatusText.Text = $"Rendered {grid.Width} x {grid.Height} samples from {grid.SourceId}.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Render failed.";
                MessageBox.Show(this, ex.ToString(), "Render error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                RenderButton.IsEnabled = true;
            }
        }

        private void MapImage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_currentGrid == null || MapImage.ActualWidth <= 0 || MapImage.ActualHeight <= 0)
                return;

            if (!TryGetRasterCell(e.GetPosition(MapImage), out var row, out var column))
                return;

            var value = _currentGrid.GetValue(row, column);
            CursorInfoText.Text = $"Lat: {_currentGrid.Latitudes[row]:0.#####}   Lon: {_currentGrid.Longitudes[column]:0.#####}   Value: {FormatValue(value)} {_currentGrid.Unit}";
        }

        private async void MapImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentGrid == null || _selectedSource == null)
                return;

            if (!TryGetRasterCell(e.GetPosition(MapImage), out var row, out var column))
                return;

            var latitude = _currentGrid.Latitudes[row];
            var longitude = _currentGrid.Longitudes[column];
            var month = Math.Clamp(MonthComboBox.SelectedIndex + 1, 1, 12);
            var date = new DateTime(DateTime.Now.Year, month, 15, 12, 0, 0, DateTimeKind.Local);
            double? depth = _currentGrid.Depth;

            try
            {
                PointQueryText.Text = "Point query...";
                var sourceId = _selectedSource.Id;
                var result = await Task.Run(() => _manager.Query(sourceId, new EnvironmentQuery
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    Depth = depth,
                    DateTime = date
                }));

                PointQueryText.Text = result == null
                    ? "Point: NoData"
                    : $"Point API: {FormatObject(result.Value)} {result.Unit}";
            }
            catch (Exception ex)
            {
                PointQueryText.Text = $"Point query error: {ex.Message}";
            }
        }

        private bool TryGetRasterCell(Point point, out int row, out int column)
        {
            row = 0;
            column = 0;
            if (_currentGrid == null)
                return false;

            var x = Math.Clamp(point.X / MapImage.ActualWidth, 0, 0.999999);
            var y = Math.Clamp(point.Y / MapImage.ActualHeight, 0, 0.999999);
            column = Math.Clamp((int)(x * _currentGrid.Width), 0, _currentGrid.Width - 1);
            row = Math.Clamp((int)(y * _currentGrid.Height), 0, _currentGrid.Height - 1);
            return true;
        }

        private static WriteableBitmap CreateBitmap(GridResult grid)
        {
            var bitmap = new WriteableBitmap(grid.Width, grid.Height, 96, 96, PixelFormats.Bgra32, null);
            var stride = grid.Width * 4;
            var pixels = new byte[stride * grid.Height];

            var min = grid.Minimum ?? 0;
            var max = grid.Maximum ?? min;
            var span = Math.Max(max - min, 1e-12);

            for (var i = 0; i < grid.Values.Length; i++)
            {
                var p = i * 4;
                var value = grid.Values[i];
                if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
                {
                    pixels[p + 0] = 230;
                    pixels[p + 1] = 230;
                    pixels[p + 2] = 230;
                    pixels[p + 3] = 255;
                    continue;
                }

                var t = Math.Clamp((value.Value - min) / span, 0, 1);
                var color = TurboLike(t);
                pixels[p + 0] = color.B;
                pixels[p + 1] = color.G;
                pixels[p + 2] = color.R;
                pixels[p + 3] = 255;
            }

            bitmap.WritePixels(new Int32Rect(0, 0, grid.Width, grid.Height), pixels, stride, 0);
            return bitmap;
        }

        private static (byte R, byte G, byte B) TurboLike(double t)
        {
            var stops = new (double T, byte R, byte G, byte B)[]
            {
                (0.00, 48, 18, 59),
                (0.20, 50, 84, 179),
                (0.40, 31, 174, 174),
                (0.60, 150, 214, 75),
                (0.80, 249, 154, 28),
                (1.00, 180, 4, 38)
            };

            for (var i = 1; i < stops.Length; i++)
            {
                if (t > stops[i].T)
                    continue;
                var a = stops[i - 1];
                var b = stops[i];
                var u = (t - a.T) / (b.T - a.T);
                return (
                    (byte)Math.Round(a.R + ((b.R - a.R) * u)),
                    (byte)Math.Round(a.G + ((b.G - a.G) * u)),
                    (byte)Math.Round(a.B + ((b.B - a.B) * u)));
            }

            return (stops[stops.Length - 1].R, stops[stops.Length - 1].G, stops[stops.Length - 1].B);
        }

        private static bool TryReadDouble(TextBox box, out double value)
        {
            return double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                   || double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private static bool TryReadInt(TextBox box, out int value)
        {
            return int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                   || int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value);
        }

        private static string FormatValue(double? value)
        {
            return !value.HasValue ? "NoData" : value.Value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static string FormatObject(object? value)
        {
            if (value == null)
                return "NoData";
            if (value is double d)
                return d.ToString("0.#####", CultureInfo.InvariantCulture);
            if (value is float f)
                return f.ToString("0.#####", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "NoData";
        }
    }
}
