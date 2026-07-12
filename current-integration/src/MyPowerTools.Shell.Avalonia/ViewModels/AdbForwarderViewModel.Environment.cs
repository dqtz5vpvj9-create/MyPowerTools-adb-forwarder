using System.Collections.ObjectModel;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class AdbForwarderViewModel
{
    private readonly Func<AdbForwarderEnvironmentSettings, Task>? _saveEnvironment;
    private string _adbExecutablePath = "adb";
    private string _wakeupPadDeviceId = "";
    private bool _isEnvironmentDirty;
    private string _environmentMessage = "修改后保存；刷新页面可重新检测设备状态。";

    public string AdbExecutablePath
    {
        get => _adbExecutablePath;
        set
        {
            if (SetProperty(ref _adbExecutablePath, value))
            {
                MarkEnvironmentDirty();
            }
        }
    }

    public string WakeupPadDeviceId
    {
        get => _wakeupPadDeviceId;
        set
        {
            if (SetProperty(ref _wakeupPadDeviceId, value))
            {
                MarkEnvironmentDirty();
            }
        }
    }

    public bool IsEnvironmentDirty
    {
        get => _isEnvironmentDirty;
        private set
        {
            if (SetProperty(ref _isEnvironmentDirty, value))
            {
                OnPropertyChanged(nameof(EnvironmentStateLabel));
            }
        }
    }

    public string EnvironmentMessage
    {
        get => _environmentMessage;
        private set => SetProperty(ref _environmentMessage, value);
    }

    public string EnvironmentStateLabel => IsEnvironmentDirty ? "配置需要保存" : "配置已保存";
    public bool HasConfiguredForwardDeviceEditors => ConfiguredForwardDeviceEditors.Count > 0;
    public bool HasConfiguredWifiDeviceEditors => ConfiguredWifiDeviceEditors.Count > 0;
    public bool CanSaveEnvironment => _saveEnvironment is not null && !IsBusy;

    private AdbForwardDeviceSettingEditorViewModel CreateForwardDeviceEditor(
        AdbForwarderForwardDeviceSetting setting)
    {
        var editor = new AdbForwardDeviceSettingEditorViewModel(setting, removed =>
        {
            ConfiguredForwardDeviceEditors.Remove(removed);
            MarkEnvironmentDirty();
            NotifyConfiguredDeviceCounts();
        });
        editor.PropertyChanged += (_, _) => MarkEnvironmentDirty();
        return editor;
    }

    private AdbWifiDeviceSettingEditorViewModel CreateWifiDeviceEditor(
        AdbForwarderWifiDeviceSetting setting)
    {
        var editor = new AdbWifiDeviceSettingEditorViewModel(setting, removed =>
        {
            ConfiguredWifiDeviceEditors.Remove(removed);
            MarkEnvironmentDirty();
            NotifyConfiguredDeviceCounts();
        });
        editor.PropertyChanged += (_, _) => MarkEnvironmentDirty();
        return editor;
    }

    private Task AddConfiguredForwardDeviceAsync()
    {
        ConfiguredForwardDeviceEditors.Add(CreateForwardDeviceEditor(
            new AdbForwarderForwardDeviceSetting("", 0)));
        MarkEnvironmentDirty();
        NotifyConfiguredDeviceCounts();
        return Task.CompletedTask;
    }

    private Task AddConfiguredWifiDeviceAsync()
    {
        ConfiguredWifiDeviceEditors.Add(CreateWifiDeviceEditor(
            new AdbForwarderWifiDeviceSetting(
                $"设备 {ConfiguredWifiDeviceEditors.Count + 1}",
                true,
                "",
                "",
                5555,
                60)));
        MarkEnvironmentDirty();
        NotifyConfiguredDeviceCounts();
        return Task.CompletedTask;
    }

    private async Task SaveEnvironmentAsync()
    {
        if (_saveEnvironment is null || !TryBuildEnvironment(out var settings))
        {
            return;
        }

        IsBusy = true;
        EnvironmentMessage = "正在保存 ADB 环境与设备配置…";
        try
        {
            await _saveEnvironment(settings).ConfigureAwait(true);
            IsEnvironmentDirty = false;
            EnvironmentMessage = "ADB 路径与设备配置已保存。点击刷新后会按新配置重新检测。";
        }
        catch (Exception ex)
        {
            EnvironmentMessage = $"保存失败：{ex.Message}";
            TechnicalDetails = $"错误类型：{ex.GetType().Name}\n模块：adb-forwarder\n{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildEnvironment(out AdbForwarderEnvironmentSettings settings)
    {
        var forward = new List<AdbForwarderForwardDeviceSetting>();
        var wifi = new List<AdbForwarderWifiDeviceSetting>();
        var valid = !string.IsNullOrWhiteSpace(AdbExecutablePath);

        foreach (var editor in ConfiguredForwardDeviceEditors)
        {
            valid &= editor.TryBuild(out var device);
            forward.Add(device);
        }
        foreach (var editor in ConfiguredWifiDeviceEditors)
        {
            valid &= editor.TryBuild(out var device);
            wifi.Add(device);
        }

        var devices = new AdbForwarderDeviceConfiguration(forward, WakeupPadDeviceId.Trim(), wifi);
        if (valid)
        {
            try
            {
                AdbForwarderConfigurationService.Validate(devices);
            }
            catch (ArgumentException ex)
            {
                valid = false;
                EnvironmentMessage = ex.Message;
            }
        }
        else
        {
            EnvironmentMessage = string.IsNullOrWhiteSpace(AdbExecutablePath)
                ? "请输入 ADB 可执行文件路径，并修正设备字段。"
                : "请修正标出的设备字段。";
        }

        settings = new AdbForwarderEnvironmentSettings(AdbExecutablePath.Trim(), devices);
        return valid;
    }

    private void MarkEnvironmentDirty()
    {
        IsEnvironmentDirty = true;
        EnvironmentMessage = "配置已修改，保存后生效。";
    }

    private void NotifyConfiguredDeviceCounts()
    {
        OnPropertyChanged(nameof(HasConfiguredForwardDeviceEditors));
        OnPropertyChanged(nameof(HasConfiguredWifiDeviceEditors));
    }
}
