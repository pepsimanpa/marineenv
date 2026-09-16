using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MarineEnvironment.Viewer
{
    public partial class MainWindow
    {
        private bool _netCdfInspectorButtonAdded;

        private void AddNetCdfInspectorButton()
        {
            if (_netCdfInspectorButtonAdded)
                return;

            if (Content is not Grid root)
                return;

            var topBorder = root.Children
                .OfType<Border>()
                .FirstOrDefault(x => Grid.GetRow(x) == 0);
            if (topBorder?.Child is not Grid topGrid)
                return;

            var column = topGrid.ColumnDefinitions.Count;
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var button = new Button
            {
                Content = "Inspect NetCDF...",
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(14, 6, 14, 6),
                ToolTip = "Open a NetCDF file and inspect raw dimensions, variables, values, and one-dimensional series."
            };
            button.Click += OpenNetCdfInspector_Click;
            Grid.SetColumn(button, column);
            topGrid.Children.Add(button);
            _netCdfInspectorButtonAdded = true;
        }

        private void OpenNetCdfInspector_Click(object sender, RoutedEventArgs e)
        {
            var window = new NetCdfInspectorWindow
            {
                Owner = this
            };
            window.Show();
        }
    }
}
