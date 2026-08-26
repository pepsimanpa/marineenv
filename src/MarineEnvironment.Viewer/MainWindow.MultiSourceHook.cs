using System;
using System.Windows;
using System.Windows.Input;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow
    {
        private bool _multiSourceClickHookInstalled;

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (_multiSourceClickHookInstalled)
                return;

            // The original handler performs the render-source-only point query.
            // Replace it with the public API validation path: one click queries every READY source.
            MapViewport.RemoveHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(MapViewport_MouseLeftButtonUp));
            MapViewport.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler(MapViewport_MultiSourceMouseUp), true);
            _multiSourceClickHookInstalled = true;
        }

        private async void MapViewport_MultiSourceMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !_isPanning)
                return;

            var position = e.GetPosition(MapViewport);
            _isPanning = false;
            MapViewport.ReleaseMouseCapture();
            MapViewport.Cursor = Cursors.Arrow;

            if (!_dragMoved)
                await QueryAllSourcesAtViewportPosition(position);

            e.Handled = true;
        }
    }
}
