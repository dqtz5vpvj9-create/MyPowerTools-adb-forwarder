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
        return CreateAsync(context).GetAwaiter().GetResult();
    }

    private static async Task<UserControl> CreateAsync(MptAvaloniaSurfaceContext context)
    {
        var tools = new AdbForwarderToolService();
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
                var fresh = await tools.LoadAsync();
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
}
