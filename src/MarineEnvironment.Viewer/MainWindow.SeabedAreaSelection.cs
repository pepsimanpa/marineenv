using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MarineEnvironment.Models;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow
    {
        private bool _seabedGradeAreaSelectionMode;
        private bool _seabedGradeAreaDragActive;
        private Point _seabedGradeAreaStartMapPoint;
        private Point _seabedGradeAreaCurrentMapPoint;
        private GradeAreaBounds? _selectedSeabedGradeArea;

        private bool IsSeabedGradeAreaDragActive => _seabedGradeAreaDragActive;

        private void SelectSeabedGradeArea_Click(object sender, RoutedEventArgs e)
        {
            if (_currentGrid == null)
            {
                MessageBox.Show(this, "Render a source first. The analysis rectangle is selected on the rendered map.");
                return;
            }

            _seabedGradeAreaSelectionMode = !_seabedGradeAreaSelectionMode;
            SelectSeabedGradeAreaButton.Content = _seabedGradeAreaSelectionMode
                ? "Cancel Area Selection"
                : "Select Area on Map";
            MapViewport.Cursor = _seabedGradeAreaSelectionMode ? Cursors.Cross : Cursors.Arrow;
            SeabedGradeAreaStatusText.Text = _seabedGradeAreaSelectionMode
                ? "Drag a rectangle on the map to define the seabed-grade analysis area."
                : (_selectedSeabedGradeArea.HasValue
                    ? FormatGradeAreaStatus(_selectedSeabedGradeArea.Value)
                    : "No analysis area selected.");
        }

        private void ClearSeabedGradeArea_Click(object sender, RoutedEventArgs e)
        {
            _currentSeabedGradeResult = null;
            GradeGridCanvas.Children.Clear();
            ResetSeabedGradeAreaForNewMap();
            StatusText.Text = "Seabed-grade analysis area cleared.";
        }

        private void ResetSeabedGradeAreaForNewMap()
        {
            _seabedGradeAreaSelectionMode = false;
            _seabedGradeAreaDragActive = false;
            _selectedSeabedGradeArea = null;
            GradeSelectionCanvas.Children.Clear();
            GradeMinLatTextBox.Text = string.Empty;
            GradeMaxLatTextBox.Text = string.Empty;
            GradeMinLonTextBox.Text = string.Empty;
            GradeMaxLonTextBox.Text = string.Empty;
            SelectSeabedGradeAreaButton.Content = "Select Area on Map";
            SeabedGradeAreaStatusText.Text = "No analysis area selected.";
            MapViewport.Cursor = Cursors.Arrow;
        }

        private bool BeginSeabedGradeAreaSelection(Point viewportPoint)
        {
            if (!_seabedGradeAreaSelectionMode || _currentGrid == null)
                return false;
            if (!TryViewportToMapPoint(viewportPoint, false, out var mapPoint))
                return true;

            _seabedGradeAreaDragActive = true;
            _seabedGradeAreaStartMapPoint = mapPoint;
            _seabedGradeAreaCurrentMapPoint = mapPoint;
            GradeSelectionCanvas.Children.Clear();
            DrawDragRectangle();
            MapViewport.Cursor = Cursors.Cross;
            return true;
        }

        private bool UpdateSeabedGradeAreaSelection(Point viewportPoint, MouseButtonState leftButton)
        {
            if (!_seabedGradeAreaDragActive)
                return false;

            if (leftButton == MouseButtonState.Pressed
                && TryViewportToMapPoint(viewportPoint, true, out var mapPoint))
            {
                _seabedGradeAreaCurrentMapPoint = mapPoint;
                DrawDragRectangle();
            }
            return true;
        }

        private bool CompleteSeabedGradeAreaSelection(Point viewportPoint)
        {
            if (!_seabedGradeAreaDragActive)
                return false;

            if (TryViewportToMapPoint(viewportPoint, true, out var mapPoint))
                _seabedGradeAreaCurrentMapPoint = mapPoint;

            _seabedGradeAreaDragActive = false;
            _seabedGradeAreaSelectionMode = false;
            SelectSeabedGradeAreaButton.Content = "Select Area on Map";

            var width = Math.Abs(_seabedGradeAreaCurrentMapPoint.X - _seabedGradeAreaStartMapPoint.X);
            var height = Math.Abs(_seabedGradeAreaCurrentMapPoint.Y - _seabedGradeAreaStartMapPoint.Y);
            if (width < 3 || height < 3)
            {
                GradeSelectionCanvas.Children.Clear();
                SeabedGradeAreaStatusText.Text = "Selection was too small. Drag a rectangle to select an area.";
                return true;
            }

            if (!TryMapPointToGeo(_seabedGradeAreaStartMapPoint, out var lat1, out var lon1)
                || !TryMapPointToGeo(_seabedGradeAreaCurrentMapPoint, out var lat2, out var lon2))
            {
                GradeSelectionCanvas.Children.Clear();
                SeabedGradeAreaStatusText.Text = "Unable to convert the selected rectangle to geographic coordinates.";
                return true;
            }

            var bounds = new GradeAreaBounds(
                Math.Min(lat1, lat2),
                Math.Max(lat1, lat2),
                Math.Min(lon1, lon2),
                Math.Max(lon1, lon2));
            _selectedSeabedGradeArea = bounds;
            WriteGradeAreaFields(bounds);
            DrawSeabedGradeSelection();

            _currentSeabedGradeResult = null;
            GradeGridCanvas.Children.Clear();
            SeabedGradeAreaStatusText.Text = FormatGradeAreaStatus(bounds);
            StatusText.Text = "Seabed-grade analysis area selected. Configure grid/density/terrain and calculate.";
            return true;
        }

        private void DrawDragRectangle()
        {
            GradeSelectionCanvas.Children.Clear();
            var left = Math.Min(_seabedGradeAreaStartMapPoint.X, _seabedGradeAreaCurrentMapPoint.X);
            var top = Math.Min(_seabedGradeAreaStartMapPoint.Y, _seabedGradeAreaCurrentMapPoint.Y);
            var width = Math.Abs(_seabedGradeAreaCurrentMapPoint.X - _seabedGradeAreaStartMapPoint.X);
            var height = Math.Abs(_seabedGradeAreaCurrentMapPoint.Y - _seabedGradeAreaStartMapPoint.Y);

            var rectangle = new Rectangle
            {
                Width = width,
                Height = height,
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(38, 0, 191, 255))
            };
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, top);
            GradeSelectionCanvas.Children.Add(rectangle);
        }

        private void DrawSeabedGradeSelection()
        {
            if (!_selectedSeabedGradeArea.HasValue || _currentGrid == null)
                return;

            var b = _selectedSeabedGradeArea.Value;
            if (!TryGeoToMapPoint(b.MaxLatitude, b.MinLongitude, out var topLeft)
                || !TryGeoToMapPoint(b.MinLatitude, b.MaxLongitude, out var bottomRight))
                return;

            GradeSelectionCanvas.Children.Clear();
            var rectangle = new Rectangle
            {
                Width = Math.Abs(bottomRight.X - topLeft.X),
                Height = Math.Abs(bottomRight.Y - topLeft.Y),
                Stroke = Brushes.DeepSkyBlue,
                StrokeThickness = 2,
                Fill = Brushes.Transparent
            };
            Canvas.SetLeft(rectangle, Math.Min(topLeft.X, bottomRight.X));
            Canvas.SetTop(rectangle, Math.Min(topLeft.Y, bottomRight.Y));
            GradeSelectionCanvas.Children.Add(rectangle);
        }

        private bool TryReadSeabedGradeBounds(
            out double minLat, out double maxLat, out double minLon, out double maxLon)
        {
            minLat = maxLat = minLon = maxLon = 0;
            if (!TryReadDouble(GradeMinLatTextBox, out minLat)
                || !TryReadDouble(GradeMaxLatTextBox, out maxLat)
                || !TryReadDouble(GradeMinLonTextBox, out minLon)
                || !TryReadDouble(GradeMaxLonTextBox, out maxLon)
                || minLat >= maxLat
                || minLon >= maxLon)
                return false;

            var bounds = new GradeAreaBounds(minLat, maxLat, minLon, maxLon);
            _selectedSeabedGradeArea = bounds;
            DrawSeabedGradeSelection();
            SeabedGradeAreaStatusText.Text = FormatGradeAreaStatus(bounds);
            return true;
        }

        private void WriteGradeAreaFields(GradeAreaBounds bounds)
        {
            GradeMinLatTextBox.Text = bounds.MinLatitude.ToString("0.######", CultureInfo.InvariantCulture);
            GradeMaxLatTextBox.Text = bounds.MaxLatitude.ToString("0.######", CultureInfo.InvariantCulture);
            GradeMinLonTextBox.Text = bounds.MinLongitude.ToString("0.######", CultureInfo.InvariantCulture);
            GradeMaxLonTextBox.Text = bounds.MaxLongitude.ToString("0.######", CultureInfo.InvariantCulture);
        }

        private static string FormatGradeAreaStatus(GradeAreaBounds b)
            => $"Selected: Lat {b.MinLatitude:0.#####} ~ {b.MaxLatitude:0.#####}, Lon {b.MinLongitude:0.#####} ~ {b.MaxLongitude:0.#####}";

        private bool TryViewportToMapPoint(Point viewportPoint, bool clamp, out Point mapPoint)
        {
            mapPoint = default;
            if (_currentGrid == null || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0)
                return false;

            var x = (viewportPoint.X - _panX) / _zoom;
            var y = (viewportPoint.Y - _panY) / _zoom;
            if (clamp)
            {
                x = Math.Max(0, Math.Min(MapViewport.ActualWidth, x));
                y = Math.Max(0, Math.Min(MapViewport.ActualHeight, y));
            }
            else if (x < 0 || y < 0 || x > MapViewport.ActualWidth || y > MapViewport.ActualHeight)
            {
                return false;
            }

            mapPoint = new Point(x, y);
            return true;
        }

        private bool TryViewportToGeo(Point viewportPoint, out double latitude, out double longitude)
        {
            latitude = longitude = 0;
            return TryViewportToMapPoint(viewportPoint, false, out var mapPoint)
                && TryMapPointToGeo(mapPoint, out latitude, out longitude);
        }

        private bool TryMapPointToGeo(Point mapPoint, out double latitude, out double longitude)
        {
            latitude = longitude = 0;
            if (_currentGrid == null
                || _currentGrid.Latitudes.Length == 0
                || _currentGrid.Longitudes.Length == 0
                || MapViewport.ActualWidth <= 0
                || MapViewport.ActualHeight <= 0)
                return false;

            var tx = Math.Max(0, Math.Min(1, mapPoint.X / MapViewport.ActualWidth));
            var ty = Math.Max(0, Math.Min(1, mapPoint.Y / MapViewport.ActualHeight));
            longitude = Interpolate(
                _currentGrid.Longitudes[0],
                _currentGrid.Longitudes[_currentGrid.Longitudes.Length - 1],
                tx);
            latitude = Interpolate(
                _currentGrid.Latitudes[0],
                _currentGrid.Latitudes[_currentGrid.Latitudes.Length - 1],
                ty);
            return true;
        }

        private bool TryGeoToMapPoint(double latitude, double longitude, out Point mapPoint)
        {
            mapPoint = default;
            if (_currentGrid == null
                || _currentGrid.Latitudes.Length == 0
                || _currentGrid.Longitudes.Length == 0
                || MapViewport.ActualWidth <= 0
                || MapViewport.ActualHeight <= 0)
                return false;

            var lon0 = _currentGrid.Longitudes[0];
            var lon1 = _currentGrid.Longitudes[_currentGrid.Longitudes.Length - 1];
            var lat0 = _currentGrid.Latitudes[0];
            var lat1 = _currentGrid.Latitudes[_currentGrid.Latitudes.Length - 1];
            if (Math.Abs(lon1 - lon0) < 1e-12 || Math.Abs(lat1 - lat0) < 1e-12)
                return false;

            var tx = (longitude - lon0) / (lon1 - lon0);
            var ty = (latitude - lat0) / (lat1 - lat0);
            mapPoint = new Point(tx * MapViewport.ActualWidth, ty * MapViewport.ActualHeight);
            return true;
        }

        private void DrawSeabedGradeOverlay(SeabedGradeGridResult result)
        {
            GradeGridCanvas.Children.Clear();
            if (_currentGrid == null || MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0)
                return;

            if (!TryGeoToMapPoint(result.MaxLatitude, result.MinLongitude, out var topLeft)
                || !TryGeoToMapPoint(result.MinLatitude, result.MaxLongitude, out var bottomRight))
                return;

            var left = Math.Min(topLeft.X, bottomRight.X);
            var top = Math.Min(topLeft.Y, bottomRight.Y);
            var width = Math.Abs(bottomRight.X - topLeft.X);
            var height = Math.Abs(bottomRight.Y - topLeft.Y);
            if (width <= 0 || height <= 0)
                return;

            var overlay = new Image
            {
                Source = CreateSeabedGradeBitmap(result.ToGridResult()),
                Width = width,
                Height = height,
                Stretch = Stretch.Fill,
                Opacity = 0.70,
                IsHitTestVisible = false
            };
            RenderOptions.SetBitmapScalingMode(overlay, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(overlay, left);
            Canvas.SetTop(overlay, top);
            GradeGridCanvas.Children.Add(overlay);

            if (result.Columns + result.Rows <= 400)
            {
                var lineBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
                lineBrush.Freeze();
                for (var c = 1; c < result.Columns; c++)
                {
                    var x = left + (width * c / result.Columns);
                    GradeGridCanvas.Children.Add(new Line
                    {
                        X1 = x, X2 = x, Y1 = top, Y2 = top + height,
                        Stroke = lineBrush, StrokeThickness = 0.7
                    });
                }
                for (var r = 1; r < result.Rows; r++)
                {
                    var y = top + (height * r / result.Rows);
                    GradeGridCanvas.Children.Add(new Line
                    {
                        X1 = left, X2 = left + width, Y1 = y, Y2 = y,
                        Stroke = lineBrush, StrokeThickness = 0.7
                    });
                }
            }
        }

        private bool TryGetSeabedGradeCellAtGeo(
            double latitude, double longitude, out SeabedGradeCell cell)
        {
            cell = null!;
            var result = _currentSeabedGradeResult;
            if (result == null
                || latitude < result.MinLatitude || latitude > result.MaxLatitude
                || longitude < result.MinLongitude || longitude > result.MaxLongitude)
                return false;

            var lonT = (longitude - result.MinLongitude)
                / (result.MaxLongitude - result.MinLongitude);
            var latT = (result.MaxLatitude - latitude)
                / (result.MaxLatitude - result.MinLatitude);
            var column = Math.Max(0, Math.Min(result.Columns - 1,
                (int)Math.Floor(lonT * result.Columns)));
            var row = Math.Max(0, Math.Min(result.Rows - 1,
                (int)Math.Floor(latT * result.Rows)));
            cell = result.GetCell(row, column);
            return true;
        }

        private readonly struct GradeAreaBounds
        {
            public GradeAreaBounds(double minLatitude, double maxLatitude, double minLongitude, double maxLongitude)
            {
                MinLatitude = minLatitude;
                MaxLatitude = maxLatitude;
                MinLongitude = minLongitude;
                MaxLongitude = maxLongitude;
            }

            public double MinLatitude { get; }
            public double MaxLatitude { get; }
            public double MinLongitude { get; }
            public double MaxLongitude { get; }
        }
    }
}
