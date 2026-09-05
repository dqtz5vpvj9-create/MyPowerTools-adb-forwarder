using MyPowerTools.AvaloniaSdk;
using AdbForwarder.Surface.ViewModels;

namespace AdbForwarder.Surface.Views;

public partial class AdbForwarderView : IMptShortcutCommandSource
{
    public string ShortcutToolId => "adb-forwarder";
    public string ShortcutContext => DataContext is AdbForwarderViewModel vm ? vm.SelectedRouteId : "";

    public IReadOnlyList<MptShortcutCommand> GetShortcutCommands()
    {
        if (DataContext is not AdbForwarderViewModel vm) return [];
        return
        [
            MptShortcutCommand.FromCommand("adb-forwarder.ui.refresh", vm.RefreshCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.show-forward", vm.ShowForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.show-rules", vm.ShowRulesCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.show-devices", vm.ShowDevicesCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.show-activity", vm.ShowActivityCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.show-diagnostics", vm.ShowDiagnosticsCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.show-settings", vm.ShowSettingsCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.select-wired-forward", vm.SelectWiredForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.select-wireless-forward", vm.SelectWirelessForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.add-mapping", vm.AddMappingCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.import-current-rules", vm.ImportCurrentRulesCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.save-mappings", vm.SaveMappingsCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.preview-changes", vm.PreviewChangesCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.apply", vm.ApplyCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.revert", vm.RevertCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.preflight-forward", vm.PreflightForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.start-forward", vm.StartForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.retry-forward", vm.RetryForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.cancel-forward", vm.CancelForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.cleanup-forward", vm.CleanupForwardCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.add-selected-device-to-configuration", vm.AddSelectedDeviceToConfigurationCommand),
            MptShortcutCommand.FromCommand("adb-forwarder.ui.save-environment", vm.SaveEnvironmentCommand),
        ];
    }
}
