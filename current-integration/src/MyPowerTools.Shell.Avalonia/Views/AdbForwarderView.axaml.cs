using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class AdbForwarderView : UserControl
{
    public AdbForwarderView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, eventArgs) => UpdateResponsiveLayout(eventArgs.NewSize.Width);
        Loaded += (_, _) => UpdateResponsiveLayout(Bounds.Width);
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void UpdateResponsiveLayout(double width)
    {
        var overview = this.FindControl<Grid>("RulesOverviewGrid");
        var current = this.FindControl<Border>("CurrentRulesCard");
        var preview = this.FindControl<Border>("ChangePreviewCard");
        var forward = this.FindControl<Grid>("ForwardWorkspaceGrid");
        var forwardSetup = this.FindControl<StackPanel>("ForwardSetupColumn");
        var forwardProgress = this.FindControl<StackPanel>("ForwardProgressColumn");
        if (overview is null || current is null || preview is null ||
            forward is null || forwardSetup is null || forwardProgress is null)
        {
            return;
        }

        forward.ColumnDefinitions.Clear();
        forward.RowDefinitions.Clear();
        if (width < 1180)
        {
            forward.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            forward.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            forward.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            forward.ColumnSpacing = 0;
            forward.RowSpacing = 14;
            Grid.SetColumn(forwardSetup, 0);
            Grid.SetRow(forwardSetup, 0);
            Grid.SetColumn(forwardProgress, 0);
            Grid.SetRow(forwardProgress, 1);
        }
        else
        {
            forward.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0.88, GridUnitType.Star)));
            forward.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.12, GridUnitType.Star)));
            forward.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            forward.ColumnSpacing = 14;
            forward.RowSpacing = 0;
            Grid.SetColumn(forwardSetup, 0);
            Grid.SetRow(forwardSetup, 0);
            Grid.SetColumn(forwardProgress, 1);
            Grid.SetRow(forwardProgress, 0);
        }

        overview.ColumnDefinitions.Clear();
        overview.RowDefinitions.Clear();
        if (width < 1120)
        {
            overview.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            overview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            overview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            overview.RowSpacing = 14;
            overview.ColumnSpacing = 0;
            Grid.SetColumn(current, 0);
            Grid.SetRow(current, 0);
            Grid.SetColumn(preview, 0);
            Grid.SetRow(preview, 1);
            return;
        }

        overview.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1.15, GridUnitType.Star)));
        overview.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0.85, GridUnitType.Star)));
        overview.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        overview.ColumnSpacing = 14;
        overview.RowSpacing = 0;
        Grid.SetColumn(current, 0);
        Grid.SetRow(current, 0);
        Grid.SetColumn(preview, 1);
        Grid.SetRow(preview, 0);
    }
}
