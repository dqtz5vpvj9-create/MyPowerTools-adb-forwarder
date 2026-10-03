using System.Globalization;
using System.Windows.Input;
using AdbForwarder.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace AdbForwarder.Surface.ViewModels;

public sealed class AdbForwardDeviceSettingEditorViewModel : MyPowerTools.AvaloniaSdk.MptObservableViewModel
{
    private readonly Action<AdbForwardDeviceSettingEditorViewModel> _remove;
    private string _deviceId;
    private string _port;
    private string _validationMessage = "";

    public AdbForwardDeviceSettingEditorViewModel(
        AdbForwarderForwardDeviceSetting setting,
        Action<AdbForwardDeviceSettingEditorViewModel> remove)
    {
        _deviceId = setting.DeviceId;
        _port = setting.Port > 0 ? setting.Port.ToString(CultureInfo.InvariantCulture) : "";
        _remove = remove;
        RemoveCommand = new MptAsyncRelayCommand(() =>
        {
            _remove(this);
            return Task.CompletedTask;
        });
    }

    public ICommand RemoveCommand { get; }

    public string DeviceId
    {
        get => _deviceId;
        set => SetProperty(ref _deviceId, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => ValidationMessage.Length > 0;

    public bool TryBuild(out AdbForwarderForwardDeviceSetting setting)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(DeviceId))
        {
            errors.Add("请输入设备 ID。");
        }
        if (!ConfigurationNumber.TryPort(Port, out var port) || port > AdbForwarderConfigurationService.MaximumForwardPort)
        {
            errors.Add($"共享端口需要在 1 到 {AdbForwarderConfigurationService.MaximumForwardPort} 之间。");
        }

        ValidationMessage = string.Join(" ", errors);
        setting = new AdbForwarderForwardDeviceSetting(DeviceId.Trim(), port);
        return errors.Count == 0;
    }
}

public sealed class AdbWifiDeviceSettingEditorViewModel : MyPowerTools.AvaloniaSdk.MptObservableViewModel
{
    private readonly Action<AdbWifiDeviceSettingEditorViewModel> _remove;
    private string _name;
    private bool _enabled;
    private string _usbSerial;
    private string _host;
    private string _port;
    private string _intervalSeconds;
    private string _validationMessage = "";

    public AdbWifiDeviceSettingEditorViewModel(
        AdbForwarderWifiDeviceSetting setting,
        Action<AdbWifiDeviceSettingEditorViewModel> remove)
    {
        _name = setting.Name;
        _enabled = setting.Enabled;
        _usbSerial = setting.UsbSerial;
        _host = setting.Host;
        _port = setting.Port > 0 ? setting.Port.ToString(CultureInfo.InvariantCulture) : "";
        _intervalSeconds = setting.IntervalSeconds > 0
            ? setting.IntervalSeconds.ToString(CultureInfo.InvariantCulture)
            : "60";
        _remove = remove;
        RemoveCommand = new MptAsyncRelayCommand(() =>
        {
            _remove(this);
            return Task.CompletedTask;
        });
    }

    public ICommand RemoveCommand { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string UsbSerial
    {
        get => _usbSerial;
        set => SetProperty(ref _usbSerial, value);
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string IntervalSeconds
    {
        get => _intervalSeconds;
        set => SetProperty(ref _intervalSeconds, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => ValidationMessage.Length > 0;

    public bool TryBuild(out AdbForwarderWifiDeviceSetting setting)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("请输入设备名称。");
        }
        if (string.IsNullOrWhiteSpace(UsbSerial))
        {
            errors.Add("请输入恢复用 USB 序列号。");
        }
        if (string.IsNullOrWhiteSpace(Host))
        {
            errors.Add("请输入无线设备主机地址。");
        }
        if (!ConfigurationNumber.TryPort(Port, out var port))
        {
            errors.Add("无线 ADB 端口需要在 1 到 65535 之间。");
        }
        if (!int.TryParse(IntervalSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval) ||
            interval is < 5 or > 86400)
        {
            errors.Add("检查间隔需要在 5 到 86400 秒之间。");
        }

        ValidationMessage = string.Join(" ", errors);
        setting = new AdbForwarderWifiDeviceSetting(
            Name.Trim(),
            Enabled,
            UsbSerial.Trim(),
            Host.Trim(),
            port,
            interval);
        return errors.Count == 0;
    }
}

file static class ConfigurationNumber
{
    public static bool TryPort(string value, out int port) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) &&
        port is >= 1 and <= 65535;
}
