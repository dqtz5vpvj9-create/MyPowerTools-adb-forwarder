using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using AdbForwarder.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace AdbForwarder.Surface.ViewModels;

public sealed class AdbForwarderMappingEditorViewModel : MyPowerTools.AvaloniaSdk.MptObservableViewModel
{
    private readonly Action<AdbForwarderMappingEditorViewModel> _remove;
    private string _name;
    private bool _enabled;
    private string _listenAddress;
    private string _listenPort;
    private string _connectAddress;
    private string _connectPort;
    private string _validationMessage = "";

    public AdbForwarderMappingEditorViewModel(
        AdbForwarderMapping mapping,
        Action<AdbForwarderMappingEditorViewModel> remove)
    {
        Id = mapping.Id;
        _name = mapping.Name;
        _enabled = mapping.Enabled;
        _listenAddress = mapping.ListenAddress;
        _listenPort = mapping.ListenPort > 0 ? mapping.ListenPort.ToString(CultureInfo.InvariantCulture) : "";
        _connectAddress = mapping.ConnectAddress;
        _connectPort = mapping.ConnectPort > 0 ? mapping.ConnectPort.ToString(CultureInfo.InvariantCulture) : "";
        _remove = remove;
        RemoveCommand = new MptAsyncRelayCommand(() =>
        {
            _remove(this);
            return Task.CompletedTask;
        });
    }

    public string Id { get; }
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

    public string ListenAddress
    {
        get => _listenAddress;
        set => SetProperty(ref _listenAddress, value);
    }

    public string ListenPort
    {
        get => _listenPort;
        set => SetProperty(ref _listenPort, value);
    }

    public string ConnectAddress
    {
        get => _connectAddress;
        set => SetProperty(ref _connectAddress, value);
    }

    public string ConnectPort
    {
        get => _connectPort;
        set => SetProperty(ref _connectPort, value);
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

    public bool TryBuild(out AdbForwarderMapping mapping)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ListenAddress))
        {
            errors.Add("请输入监听地址。");
        }
        if (string.IsNullOrWhiteSpace(ConnectAddress))
        {
            errors.Add("请输入目标地址。");
        }
        if (!TryPort(ListenPort, out var listenPort))
        {
            errors.Add("监听端口需要在 1 到 65535 之间。");
        }
        if (!TryPort(ConnectPort, out var connectPort))
        {
            errors.Add("目标端口需要在 1 到 65535 之间。");
        }

        ValidationMessage = string.Join(" ", errors);
        mapping = new AdbForwarderMapping(
            Id,
            string.IsNullOrWhiteSpace(Name) ? $"{ListenAddress}:{ListenPort}" : Name.Trim(),
            Enabled,
            ListenAddress.Trim(),
            listenPort,
            ConnectAddress.Trim(),
            connectPort);
        return errors.Count == 0;
    }

    private static bool TryPort(string value, out int port)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }
}
