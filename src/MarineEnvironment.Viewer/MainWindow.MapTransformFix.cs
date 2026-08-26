using System;
using System.Windows;
using System.Windows.Media;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow
    {
        private bool _normalizingMapTransform;

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // MainWindow's pan/zoom state (_panX/_panY) is stored in viewport pixels.
            // Keep the actual WPF transform in the same coordinate system.  This also
            // corrects transforms produced by the original ApplyMapTransform method,
            // whose translation became progressively smaller as zoom increased.
            MapTransform.Changed += MapTransform_Changed;
            NormalizeMapTransform();
        }

        private void MapTransform_Changed(object? sender, EventArgs e)
        {
            NormalizeMapTransform();
        }

        private void NormalizeMapTransform()
        {
            if (_normalizingMapTransform)
                return;

            var expected = new Matrix(_zoom, 0, 0, _zoom, _panX, _panY);
            var current = MapTransform.Matrix;

            if (NearlySame(current.M11, expected.M11)
                && NearlySame(current.M12, expected.M12)
                && NearlySame(current.M21, expected.M21)
                && NearlySame(current.M22, expected.M22)
                && NearlySame(current.OffsetX, expected.OffsetX)
                && NearlySame(current.OffsetY, expected.OffsetY))
            {
                return;
            }

            try
            {
                _normalizingMapTransform = true;
                MapTransform.Matrix = expected;
            }
            finally
            {
                _normalizingMapTransform = false;
            }

            // The map center latitude can change while panning, so refresh the
            // distance scale together with the corrected transform.
            UpdateScaleBar();
        }

        private static bool NearlySame(double a, double b)
        {
            return Math.Abs(a - b) <= 1e-9;
        }
    }
}
