using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using AdbForwarder.Surface.ViewModels;

namespace AdbForwarder.Surface.Views;

public sealed partial class AdbForwarderView : UserControl
{
    public AdbForwarderView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += (_, eventArgs) => UpdateResponsiveLayout(eventArgs.NewSize.Width);
        Loaded += (_, _) => UpdateResponsiveLayout(Bounds.Width);
        KeyDown += OnKeyDown;
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Handled || DataContext is not AdbForwarderViewModel viewModel)
        {
            return;
        }

        var handled = AdbForwarderKeyboardShortcut.Resolve(eventArgs.Key, eventArgs.KeyModifiers) switch
        {
            AdbForwarderKeyboardAction.Refresh => TryExecute(viewModel.RefreshCommand),
            AdbForwarderKeyboardAction.RunPrimaryAction => TryRunPrimaryAction(viewModel),
            AdbForwarderKeyboardAction.CancelRunningAction when viewModel.IsForwardBusy =>
                TryExecute(viewModel.CancelForwardCommand),
            _ => false
        };

        eventArgs.Handled = handled;
    }

    private static bool TryRunPrimaryAction(AdbForwarderViewModel viewModel)
    {
        if (viewModel.IsSettings && viewModel.CanSaveEnvironment)
        {
            return TryExecute(viewModel.SaveEnvironmentCommand);
        }

        if (viewModel.IsRules)
        {
            if (viewModel.CanApply)
            {
                return TryExecute(viewModel.ApplyCommand);
            }

            if (viewModel.CanPreview)
            {
                return TryExecute(viewModel.PreviewChangesCommand);
            }
        }

        if (viewModel.IsForward)
        {
            if (viewModel.CanRetryForward)
            {
                return TryExecute(viewModel.RetryForwardCommand);
            }

            if (viewModel.CanStartForward)
            {
                return TryExecute(viewModel.StartForwardCommand);
            }
        }

        return false;
    }

    private static bool TryExecute(ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private void UpdateResponsiveLayout(double width)
    {
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
