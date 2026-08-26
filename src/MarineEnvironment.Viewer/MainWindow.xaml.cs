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

        private double _zoom = 1.0;
        private double _panX;
        private double _panY;
        private bool _isPanning;
        private bool _dragMoved;
        private Point _panStart;
        private Point _panOrigin;

        private const double MinZoom = 1.0;
        private const double MaxZoom = 32.0;
        private const double ZoomStep = 1.25;

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

                ResetMapTransform();
                ScaleBarPanel.Visibility = Visibility.Visible;
                UpdateScaleBar();
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

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomAt(new Point(MapViewport.ActualWidth / 2.0, MapViewport.ActualHeight / 2.0), ZoomStep);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomAt(new Point(MapViewport.ActualWidth / 2.0, MapViewport.ActualHeight / 2.0), 1.0 / ZoomStep);
        }

        private void ResetZoom_Click(object sender, RoutedEventArgs e)
        {
            ResetMapTransform();
        }

        private void MapViewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_currentGrid == null)
                return;

            var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
            ZoomAt(e.GetPosition(MapViewport), factor);
            e.Handled = true;
        }

        private void MapViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentGrid == null)
                return;

            _isPanning = true;
            _dragMoved = false;
            _panStart = e.GetPosition(MapViewport);
            _panOrigin = new Point(_panX, _panY);
            MapViewport.CaptureMouse();
            MapViewport.Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private async void MapViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isPanning)
                return;

            var position = e.GetPosition(MapViewport);
            _isPanning = false;
            MapViewport.ReleaseMouseCapture();
            MapViewport.Cursor = Cursors.Arrow;

            if (!_dragMoved)
                await QueryPointAtViewportPosition(position);

            e.Handled = true;
        }

        private void MapViewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_currentGrid == null)
                return;

            var position = e.GetPosition(MapViewport);

            if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var delta = position - _panStart;
                if (Math.Abs(delta.X) > 2 || Math.Abs(delta.Y) > 2)
                    _dragMoved = true;

                _panX = _panOrigin.X + delta.X;
                _panY = _panOrigin.Y + delta.Y;
                ClampPan();
                ApplyMapTransform();
                return;
            }

            if (TryGetRasterCellFromViewport(position, out var row, out var column))
            {
                var value = _currentGrid.GetValue(row, column);
                CursorInfoText.Text = $"Lat: {_currentGrid.Latitudes[row]:0.#####}   Lon: {_currentGrid.Longitudes[column]:0.#####}   Value: {FormatValue(value)} {_currentGrid.Unit}";
            }
            else
            {
                CursorInfoText.Text = "Lat: -   Lon: -   Value: -";
            }
        }

        private void MapViewport_MouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isPanning)
                CursorInfoText.Text = "Lat: -   Lon: -   Value: -";
        }

        private void MapViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_currentGrid == null)
                return;

            ClampPan();
            ApplyMapTransform();
            UpdateScaleBar();
        }

        private async Task QueryPointAtViewportPosition(Point position)
        {
            if (_currentGrid == null || _selectedSource == null)
                return;

            if (!TryGetRasterCellFromViewport(position, out var row, out var column))
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

        private void ZoomAt(Point viewportPoint, double factor)
        {
            if (_currentGrid == null || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0)
                return;

            var oldZoom = _zoom;
            var newZoom = Math.Max(MinZoom, Math.Min(MaxZoom, oldZoom * factor));
            if (Math.Abs(newZoom - oldZoom) < 1e-12)
                return;

            var imageX = (viewportPoint.X - _panX) / oldZoom;
            var imageY = (viewportPoint.Y - _panY) / oldZoom;

            _zoom = newZoom;
            _panX = viewportPoint.X - (imageX * newZoom);
            _panY = viewportPoint.Y - (imageY * newZoom);

            ClampPan();
            ApplyMapTransform();
            UpdateScaleBar();
        }

        private void ResetMapTransform()
        {
            _zoom = 1.0;
            _panX = 0;
            _panY = 0;
            ApplyMapTransform();
            UpdateScaleBar();
        }

        private void ClampPan()
        {
            var width = MapViewport.ActualWidth;
            var height = MapViewport.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            if (_zoom <= 1.0)
            {
                _panX = 0;
                _panY = 0;
                return;
            }

            var minX = width - (width * _zoom);
            var minY = height - (height * _zoom);
            _panX = Math.Max(minX, Math.Min(0, _panX));
            _panY = Math.Max(minY, Math.Min(0, _panY));
        }

        private void ApplyMapTransform()
        {
            var matrix = Matrix.Identity;
            matrix.Scale(_zoom, _zoom);
            matrix.Translate(_panX / _zoom, _panY / _zoom);
            MapTransform.Matrix = matrix;
            ZoomText.Text = $"{_zoom * 100:0}%";
        }

        private bool TryGetRasterCellFromViewport(Point viewportPoint, out int row, out int column)
        {
            row = 0;
            column = 0;
            if (_currentGrid == null || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0)
                return false;

            var imageX = (viewportPoint.X - _panX) / _zoom;
            var imageY = (viewportPoint.Y - _panY) / _zoom;

            if (imageX < 0 || imageY < 0 || imageX >= MapViewport.ActualWidth || imageY >= MapViewport.ActualHeight)
                return false;

            var x = Math.Max(0, Math.Min(0.999999, imageX / MapViewport.ActualWidth));
            var y = Math.Max(0, Math.Min(0.999999, imageY / MapViewport.ActualHeight));
            column = Math.Max(0, Math.Min(_currentGrid.Width - 1, (int)(x * _currentGrid.Width)));
            row = Math.Max(0, Math.Min(_currentGrid.Height - 1, (int)(y * _currentGrid.Height)));
            return true;
        }

        private void UpdateScaleBar()
        {
            if (_currentGrid == null || MapViewport.ActualWidth <= 0 || _zoom <= 0)
            {
                ScaleBarPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var centerY = MapViewport.ActualHeight / 2.0;
            var centerImageY = (centerY - _panY) / _zoom;
            centerImageY = Math.Max(0, Math.Min(MapViewport.ActualHeight - 1, centerImageY));
            var normalizedY = centerImageY / Math.Max(1.0, MapViewport.ActualHeight - 1.0);
            var centerLat = _currentGrid.Latitudes.Length > 1
                ? Interpolate(_currentGrid.Latitudes[0], _currentGrid.Latitudes[_currentGrid.Latitudes.Length - 1], normalizedY)
                : _currentGrid.Latitudes[0];

            var minLon = _currentGrid.Longitudes.Min();
            var maxLon = _currentGrid.Longitudes.Max();
            var lonSpan = Math.Abs(maxLon - minLon);
            if (lonSpan <= 0)
            {
                ScaleBarPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var kilometersAcrossViewport = HaversineKilometers(centerLat, minLon, centerLat, maxLon) / _zoom;
            if (kilometersAcrossViewport <= 0 || double.IsNaN(kilometersAcrossViewport) || double.IsInfinity(kilometersAcrossViewport))
            {
                ScaleBarPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var targetPixels = Math.Max(70.0, Math.Min(150.0, MapViewport.ActualWidth * 0.18));
            var targetKm = kilometersAcrossViewport * (targetPixels / MapViewport.ActualWidth);
            var niceKm = NiceDistance(targetKm);
            var pixels = MapViewport.ActualWidth * (niceKm / kilometersAcrossViewport);
            pixels = Math.Max(35.0, Math.Min(MapViewport.ActualWidth * 0.35, pixels));

            ScaleBarLine.Width = pixels;
            ScaleBarText.Text = niceKm >= 1.0
                ? $"{niceKm:0.##} km"
                : $"{niceKm * 1000.0:0} m";
            ScaleBarPanel.Visibility = Visibility.Visible;
        }

        private static double NiceDistance(double targetKm)
        {
            if (targetKm <= 0)
                return 1;

            var exponent = Math.Floor(Math.Log10(targetKm));
            var magnitude = Math.Pow(10, exponent);
            var normalized = targetKm / magnitude;
            double nice;

            if (normalized < 1.5)
                nice = 1;
            else if (normalized < 3.5)
                nice = 2;
            else if (normalized < 7.5)
                nice = 5;
            else
                nice = 10;

            return nice * magnitude;
        }

        private static double HaversineKilometers(double lat1, double lon1, double lat2, double lon2)
        {
            const double EarthRadiusKm = 6371.0088;
            var phi1 = DegreesToRadians(lat1);
            var phi2 = DegreesToRadians(lat2);
            var dPhi = DegreesToRadians(lat2 - lat1);
            var dLambda = DegreesToRadians(lon2 - lon1);

            var a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                    + Math.Cos(phi1) * Math.Cos(phi2)
                    * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
            return EarthRadiusKm * c;
        }

        private static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private static double Interpolate(double a, double b, double t)
        {
            return a + ((b - a) * t);
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

                var t = Math.Max(0, Math.Min(1, (value.Value - min) / span));
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
