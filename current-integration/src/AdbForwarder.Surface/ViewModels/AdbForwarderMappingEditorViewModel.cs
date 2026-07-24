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
        set
        {
            if (!SetProperty(ref _listenPort, value))
            {
                return;
            }

            _listenAddress = "0.0.0.0";
            _connectAddress = "127.0.0.1";
            if (TryPort(value, out var sharedPort) && sharedPort <= 50535)
            {
                _connectPort = (sharedPort + 15000).ToString(CultureInfo.InvariantCulture);
            }
            OnPropertyChanged(nameof(SharedPort));
        }
    }

    public string SharedPort
    {
        get => ListenPort;
        set => ListenPort = value;
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
        if (!TryPort(ListenPort, out var listenPort))
        {
            errors.Add("共享端口需要在 1 到 50535 之间。");
        }
        else if (listenPort > 50535)
        {
            errors.Add("共享端口需要在 1 到 50535 之间。");
        }

        var connectPort = listenPort is >= 1 and <= 50535 ? listenPort + 15000 : 0;

        ValidationMessage = string.Join(" ", errors);
        mapping = new AdbForwarderMapping(
            Id,
            string.IsNullOrWhiteSpace(Name) ? $"共享端口 {ListenPort}" : Name.Trim(),
            Enabled,
            "0.0.0.0",
            listenPort,
            "127.0.0.1",
            connectPort);
        return errors.Count == 0;
    }

    private static bool TryPort(string value, out int port)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
    }
}
