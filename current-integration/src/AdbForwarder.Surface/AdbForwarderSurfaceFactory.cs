using Avalonia.Controls;
using AdbForwarder.Surface.Services;
using AdbForwarder.Surface.ViewModels;
using AdbForwarder.Surface.Views;
using MyPowerTools.AvaloniaSdk;

namespace AdbForwarder.Surface;

/// <summary>
/// Dotnet-surface factory for the ADB Forwarder tool. Loaded by the Shell's DotnetSurfaceLoader
/// from this assembly via the route's <c>assembly</c>+<c>type</c> manifest fields. Builds the
/// AdbForwarderViewModel with callbacks wired through <see cref="MptAvaloniaSurfaceContext"/> so the
/// tool operates independently of the Shell controller.
/// </summary>
public sealed class AdbForwarderSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var host = new ContentControl
        {
            Content = CreateLoadingView()
        };

        _ = PopulateAsync(host, context);
        return host;
    }

    private static async Task PopulateAsync(ContentControl host, MptAvaloniaSurfaceContext context)
    {
        try
        {
            host.Content = await CreateLoadedSurfaceAsync(context);
        }
        catch (Exception ex)
        {
            context.Log(new MptSurfaceLogEntry("error", $"ADB Forwarder failed to load: {ex.Message}", DateTimeOffset.Now));
            host.Content = CreateFailureView(host, context, ex.Message);
        }
    }

    private static async Task<UserControl> CreateLoadedSurfaceAsync(MptAvaloniaSurfaceContext context)
    {
        var tools = new AdbForwarderToolService(
            service: new AdbForwarderServiceClient(context.ServiceUnits));
        var elevation = new AdbForwarderElevationService();

        var snapshot = await tools.LoadAsync();
        var brokerAvailability = elevation.GetAvailability();
        snapshot = snapshot with
        {
            BrokerAvailable = brokerAvailability.IsAvailable,
            BrokerAvailabilityMessage = brokerAvailability.Message
        };

        var settingsRevision = snapshot.SettingsRevision;
        AdbForwarderViewModel viewModel = null!;
        viewModel = new AdbForwarderViewModel(
            snapshot,
            context.RouteId,
            navigateRoute: targetRouteId => context.NavigateAsync(context.ToolId, targetRouteId, null),
            browseAllTools: () => context.NavigateAsync("", "", null),
            refresh: async () =>
            {
                var fresh = await tools.LoadAsync(refreshService: true);
                var ba = elevation.GetAvailability();
                fresh = fresh with { BrokerAvailable = ba.IsAvailable, BrokerAvailabilityMessage = ba.Message };
                viewModel.UpdateSnapshot(fresh);
            },
            saveMappings: async mappings =>
            {
                settingsRevision = await tools.SaveMappingsAsync(settingsRevision, mappings);
            },
            preview: mappings => tools.PreviewAsync(mappings),
            executeBrokered: async (commandId, mappings) =>
            {
                var action = commandId.EndsWith(".revert", StringComparison.OrdinalIgnoreCase)
                    ? AdbForwarderBrokerAction.Remove
                    : AdbForwarderBrokerAction.Ensure;
                var result = await elevation.ExecuteImmediatelyAsync(action, mappings);
                if (result.Disposition != AdbForwarderBrokerDisposition.Applied)
                {
                    throw new InvalidOperationException(result.Message);
                }
            },
            forwarding: tools.Forwarding,
            forwardBroker: async (action, mapping, cancellationToken) =>
            {
                return await elevation.RequestOrApproveAsync(action, mapping, cancellationToken);
            },
            saveEnvironment: async configuration =>
            {
                settingsRevision = await tools.SaveEnvironmentAsync(settingsRevision, configuration);
            });

        return new AdbForwarderView { DataContext = viewModel };
    }

    private static Control CreateLoadingView() =>
        new Border
        {
            Padding = new Avalonia.Thickness(32),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "ADB Forwarder", FontSize = 30, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = "正在读取有线设备、无线设备与端口转发状态…" },
                    new ProgressBar { IsIndeterminate = true, Width = 240, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left }
                }
            }
        };

    private static Control CreateFailureView(
        ContentControl host,
        MptAvaloniaSurfaceContext context,
        string message)
    {
        var retry = new Button { Content = "Retry", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        retry.Click += (_, _) =>
        {
            host.Content = CreateLoadingView();
            _ = PopulateAsync(host, context);
        };

        return new Border
        {
            Padding = new Avalonia.Thickness(32),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "ADB Forwarder could not load", FontSize = 26, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    retry
                }
            }
        };
    }
}
