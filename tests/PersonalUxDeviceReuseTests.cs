using AdbForwarder.Surface.Services;
using AdbForwarder.Surface.ViewModels;
using Avalonia.Headless.XUnit;
using Xunit;

namespace PersonalUx.Tests;

public sealed class PersonalUxDeviceReuseTests
{
    [AvaloniaFact]
    public void Reusing_connected_devices_fills_serials_avoids_duplicate_ports_and_preserves_existing_edits()
    {
        var first = new AdbForwarderDevice("usb-first", "device", "Pixel", "", "1");
        var second = new AdbForwarderDevice("usb-second", "device", "Tablet", "", "2");
        var snapshot = new AdbForwarderSnapshot(true, "adb", [first, second], true, [], [], new([], [], [], [], false), [], 1)
        { ForwardDevices = [first, second] };
        using var vm = new AdbForwarderViewModel(snapshot);
        vm.SharedPort = 15557;
        vm.AddSelectedDeviceToConfigurationCommand.Execute(null);
        Assert.Equal("settings", vm.SelectedRouteId);
        Assert.Equal("usb-first", Assert.Single(vm.ConfiguredForwardDeviceEditors).DeviceId);
        vm.ConfiguredForwardDeviceEditors[0].Port = "15558";
        vm.SelectedForwardDevice = second;
        vm.SharedPort = 15558;
        vm.AddSelectedDeviceToConfigurationCommand.Execute(null);
        Assert.Equal("15559", vm.ConfiguredForwardDeviceEditors[1].Port);
        vm.SelectedForwardDevice = first;
        vm.AddSelectedDeviceToConfigurationCommand.Execute(null);
        Assert.Equal(2, vm.ConfiguredForwardDeviceEditors.Count);
        Assert.Equal("15558", vm.ConfiguredForwardDeviceEditors[0].Port);
        Assert.True(vm.IsEnvironmentDirty);
    }
}
