using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AdbForwarder.Surface.Views;

public sealed partial class AdbForwarderView : UserControl
{
    public AdbForwarderView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(() => UpdateResponsiveLayout(Bounds.Width), DispatcherPriority.Loaded);
        Loaded += (_, _) => UpdateResponsiveLayout(Bounds.Width);
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private double _lastResponsiveWidth = double.NaN;

    private void UpdateResponsiveLayout(double width)
    {
        if (width <= 0 || width == _lastResponsiveWidth) return;
        _lastResponsiveWidth = width;
        var overview = this.FindControl<Grid>("DeviceOverviewGrid");
        var forward = this.FindControl<Border>("ForwardDevicesCard");
        var wifi = this.FindControl<Border>("WifiDevicesCard");
        if (overview is null || forward is null || wifi is null)
        {
            return;
        }

        overview.ColumnDefinitions.Clear();
        overview.RowDefinitions.Clear();
        if (width < 1180)
        {
            overview.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            overview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            overview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            overview.RowSpacing = 12;
            overview.ColumnSpacing = 0;
            Grid.SetColumn(forward, 0);
            Grid.SetRow(forward, 0);
            Grid.SetColumn(wifi, 0);
            Grid.SetRow(wifi, 1);
            return;
        }

        overview.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0.85, GridUnitType.Star)));
        overview.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.15, GridUnitType.Star)));
        overview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        overview.ColumnSpacing = 12;
        overview.RowSpacing = 0;
        Grid.SetColumn(forward, 0);
        Grid.SetRow(forward, 0);
        Grid.SetColumn(wifi, 1);
        Grid.SetRow(wifi, 0);
    }
}
