using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Shapes;
using MarineEnvironment.Models;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow
    {
        private void DrawCurrentVectors(GridResult grid)
        {
            CurrentVectorCanvas.Children.Clear();
            if (grid.Type != EnvironmentType.Current)
                return;
            if (MapViewport.ActualWidth <= 0 || MapViewport.ActualHeight <= 0)
                return;

            if (grid.CurrentVectors != null && grid.CurrentVectors.Count > 0)
            {
                DrawSourcePointCurrentVectors(grid);
                return;
            }

            if (grid.Directions == null)
                return;

            DrawRasterCurrentVectors(grid);
        }

        private void DrawSourcePointCurrentVectors(GridResult grid)
        {
            if (grid.Latitudes.Length == 0 || grid.Longitudes.Length == 0 || grid.CurrentVectors == null)
                return;

            var viewportWidth = MapViewport.ActualWidth;
            var viewportHeight = MapViewport.ActualHeight;
            var minLatitude = Math.Min(grid.Latitudes[0], grid.Latitudes[grid.Latitudes.Length - 1]);
            var maxLatitude = Math.Max(grid.Latitudes[0], grid.Latitudes[grid.Latitudes.Length - 1]);
            var minLongitude = Math.Min(grid.Longitudes[0], grid.Longitudes[grid.Longitudes.Length - 1]);
            var maxLongitude = Math.Max(grid.Longitudes[0], grid.Longitudes[grid.Longitudes.Length - 1]);

            if (maxLatitude <= minLatitude || maxLongitude <= minLongitude)
                return;

            // Preserve actual source coordinates, but keep the display readable by allowing at most
            // one source vector per ~44 px screen cell. This is display decimation only; the raster
            // and point-query data remain unchanged.
            const double screenCellSize = 44.0;
            var occupiedCells = new HashSet<(int X, int Y)>();

            foreach (var vector in grid.CurrentVectors)
            {
                if (vector.Latitude < minLatitude || vector.Latitude > maxLatitude
                    || vector.Longitude < minLongitude || vector.Longitude > maxLongitude)
                {
                    continue;
                }

                var x = (vector.Longitude - minLongitude) / (maxLongitude - minLongitude) * viewportWidth;
                var y = (maxLatitude - vector.Latitude) / (maxLatitude - minLatitude) * viewportHeight;
                var cell = ((int)(x / screenCellSize), (int)(y / screenCellSize));
                if (!occupiedCells.Add(cell))
                    continue;

                AddCurrentArrow(x, y, vector.Direction);
            }
        }

        private void DrawRasterCurrentVectors(GridResult grid)
        {
            var viewportWidth = MapViewport.ActualWidth;
            var viewportHeight = MapViewport.ActualHeight;
            var maxColumns = Math.Max(1, (int)(viewportWidth / 48.0));
            var maxRows = Math.Max(1, (int)(viewportHeight / 48.0));
            var columnStep = Math.Max(1, (int)Math.Ceiling(grid.Width / (double)maxColumns));
            var rowStep = Math.Max(1, (int)Math.Ceiling(grid.Height / (double)maxRows));

            var firstRow = Math.Min(grid.Height - 1, rowStep / 2);
            var firstColumn = Math.Min(grid.Width - 1, columnStep / 2);
            for (var row = firstRow; row < grid.Height; row += rowStep)
            {
                for (var column = firstColumn; column < grid.Width; column += columnStep)
                {
                    var direction = grid.GetDirection(row, column);
                    var speed = grid.GetValue(row, column);
                    if (!direction.HasValue || !speed.HasValue)
                        continue;

                    var x = (column + 0.5) / grid.Width * viewportWidth;
                    var y = (row + 0.5) / grid.Height * viewportHeight;
                    AddCurrentArrow(x, y, direction.Value);
                }
            }
        }

        private void AddCurrentArrow(double centerX, double centerY, double directionDegrees)
        {
            const double length = 17.0;
            const double headLength = 5.5;
            const double headAngleDegrees = 28.0;

            var radians = directionDegrees * Math.PI / 180.0;
            var dx = Math.Sin(radians);
            var dy = -Math.Cos(radians);
            var startX = centerX - (dx * length * 0.5);
            var startY = centerY - (dy * length * 0.5);
            var endX = centerX + (dx * length * 0.5);
            var endY = centerY + (dy * length * 0.5);

            AddVectorLine(startX, startY, endX, endY, Brushes.White, 3.2, 0.78);
            AddVectorLine(startX, startY, endX, endY, Brushes.Black, 1.4, 0.88);

            var backAngle = radians + Math.PI;
            var offset = headAngleDegrees * Math.PI / 180.0;
            AddArrowHeadLine(endX, endY, backAngle - offset, headLength);
            AddArrowHeadLine(endX, endY, backAngle + offset, headLength);
        }

        private void AddArrowHeadLine(double x, double y, double radians, double length)
        {
            var endX = x + (Math.Sin(radians) * length);
            var endY = y - (Math.Cos(radians) * length);
            AddVectorLine(x, y, endX, endY, Brushes.White, 3.0, 0.78);
            AddVectorLine(x, y, endX, endY, Brushes.Black, 1.3, 0.9);
        }

        private void AddVectorLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness, double opacity)
        {
            CurrentVectorCanvas.Children.Add(new Line
            {
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                Stroke = stroke,
                StrokeThickness = thickness,
                Opacity = opacity,
                SnapsToDevicePixels = true
            });
        }
    }
}
