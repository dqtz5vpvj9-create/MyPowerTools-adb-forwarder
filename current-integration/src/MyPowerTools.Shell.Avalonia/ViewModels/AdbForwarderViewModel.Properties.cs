using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class AdbForwarderViewModel
{
    public bool IsForward => SelectedRouteId == "forward";
    public bool IsRules => SelectedRouteId == "rules";
    public bool IsDevices => SelectedRouteId == "devices";
    public bool IsActivity => SelectedRouteId == "activity";
    public bool IsDiagnostics => SelectedRouteId == "diagnostics";
    public bool IsSettings => SelectedRouteId == "settings";
    public bool HasMappings => Mappings.Count > 0;
    public bool HasCurrentRules => CurrentRules.Count > 0;
    public bool HasDevices => Devices.Count > 0;
    public bool HasActivity => Activity.Count > 0;
    public bool HasConfiguredForwardDevices => ConfiguredForwardDevices.Count > 0;
    public bool HasConfiguredWifiDevices => ConfiguredWifiDevices.Count > 0;
    public bool HasConfigurationError => Snapshot.ConfiguredState.Error.Length > 0;
    public string ConfigurationError => Snapshot.ConfiguredState.Error;
    public string ConfigurationPath => Snapshot.ConfiguredState.ConfigPath;
    public string WakeupPadSummary => string.IsNullOrWhiteSpace(Snapshot.ConfiguredState.WakeupPadDeviceId)
        ? "WakeupPad 未配置"
        : $"WakeupPad · {Snapshot.ConfiguredState.WakeupPadDeviceId}";
    public string ForwardFleetSummary => $"{ConfiguredForwardDevices.Count} 台已配置 · {ConfiguredForwardDevices.Count(device => device.IsOnline)} 台在线";
    public string WifiFleetSummary => $"{ConfiguredWifiDevices.Count} 台已配置 · {ConfiguredWifiDevices.Count(device => device.IsHealthy)} 台可连接";
    public IReadOnlyList<AdbForwarderDevice> ForwardDevices => DevicesForMode(ConnectionMode);
    public IReadOnlyList<AdbForwarderDevice> WiredForwardDevices => DevicesForMode(AdbForwardConnectionMode.Wired);
    public IReadOnlyList<AdbForwarderDevice> WirelessForwardDevices => DevicesForMode(AdbForwardConnectionMode.Wireless);
    public int WiredDeviceCount => WiredForwardDevices.Count;
    public int WirelessDeviceCount => WirelessForwardDevices.Count;
    public bool HasForwardDevices => ForwardDevices.Count > 0;
    public bool HasPreflightChecks => PreflightChecks.Count > 0;
    public bool HasForwardLogs => ForwardLogs.Count > 0;
    public bool BrokerAvailable => Snapshot.BrokerAvailable;
    public string BrokerAvailabilityMessage => Snapshot.BrokerAvailabilityMessage;
    public bool PortProxyAvailable => Snapshot.PortProxyAvailable;
    public bool HasSavedMappings => _savedMappings.Count > 0;
    public bool HasWarnings => UserFacingWarnings.Count > 0;
    public bool HasPlannedChanges => Plan.HasChanges;
    public bool IsPreviewCurrent
    {
        get => _isPreviewCurrent;
        private set
        {
            if (SetProperty(ref _isPreviewCurrent, value))
            {
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(CanApply));
                ((AsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public AdbForwarderDevice? SelectedForwardDevice
    {
        get => _selectedForwardDevice;
        set
        {
            if (SetProperty(ref _selectedForwardDevice, value))
            {
                _preflight = null;
                PreflightChecks.Clear();
                ForwardActionMessage = value is null
                    ? "请先连接并选择一台 ADB 设备。"
                    : $"已选择 {value.Model}，建议先运行只读预检。";
                NotifyForwardState();
            }
        }
    }

    public int LocalForwardPort
    {
        get => _localForwardPort;
        set
        {
            if (SetProperty(ref _localForwardPort, value))
            {
                OnPropertyChanged(nameof(ForwardPathSummary));
                InvalidateForwardPreflight();
            }
        }
    }

    public int SharedPort
    {
        get => _sharedPort;
        set
        {
            if (SetProperty(ref _sharedPort, value))
            {
                OnPropertyChanged(nameof(ForwardPathSummary));
                InvalidateForwardPreflight();
            }
        }
    }

    public int DevicePort
    {
        get => _devicePort;
        set
        {
            if (SetProperty(ref _devicePort, value))
            {
                OnPropertyChanged(nameof(ForwardPathSummary));
                InvalidateForwardPreflight();
            }
        }
    }

    public AdbForwardConnectionMode ConnectionMode
    {
        get => _connectionMode;
        private set
        {
            if (SetProperty(ref _connectionMode, value))
            {
                OnPropertyChanged(nameof(IsWiredForward));
                OnPropertyChanged(nameof(IsWirelessForward));
                OnPropertyChanged(nameof(ForwardDevices));
                OnPropertyChanged(nameof(HasForwardDevices));
                OnPropertyChanged(nameof(ForwardModeLabel));
                OnPropertyChanged(nameof(ForwardWorkflowTitle));
                OnPropertyChanged(nameof(ForwardWorkflowDescription));
                OnPropertyChanged(nameof(ForwardPathSummary));
            }
        }
    }

    public bool IsWiredForward => ConnectionMode == AdbForwardConnectionMode.Wired;
    public bool IsWirelessForward => ConnectionMode == AdbForwardConnectionMode.Wireless;

    public bool IncludeSsh
    {
        get => _includeSsh;
        set
        {
            if (SetProperty(ref _includeSsh, value))
            {
                OnPropertyChanged(nameof(ForwardModeLabel));
                OnPropertyChanged(nameof(IsLocalOnly));
                InvalidateForwardPreflight();
            }
        }
    }

    public bool IsLocalOnly => !IncludeSsh;

    public string RemoteHost
    {
        get => _remoteHost;
        set
        {
            if (SetProperty(ref _remoteHost, value))
            {
                OnPropertyChanged(nameof(ForwardModeLabel));
                InvalidateForwardPreflight();
            }
        }
    }

    public string RemoteAdbPath
    {
        get => _remoteAdbPath;
        set
        {
            if (SetProperty(ref _remoteAdbPath, value))
            {
                InvalidateForwardPreflight();
            }
        }
    }

    public bool IsForwardBusy
    {
        get => _isForwardBusy;
        private set
        {
            if (SetProperty(ref _isForwardBusy, value))
            {
                NotifyForwardState();
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanSaveMappings));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanRevert));
                ((AsyncRelayCommand)RefreshCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)SaveMappingsCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)PreviewChangesCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)RevertCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string ForwardActionMessage
    {
        get => _forwardActionMessage;
        private set => SetProperty(ref _forwardActionMessage, value);
    }
    public bool CanPreview => !IsBusy && !IsForwardBusy && PortProxyAvailable && HasMappings;
    public bool CanSaveMappings => !IsBusy && !IsForwardBusy && IsMappingsDirty;
    public bool CanApply => !IsBusy && !IsForwardBusy && BrokerAvailable && PortProxyAvailable && HasMappings && HasPlannedChanges && IsPreviewCurrent && !IsMappingsDirty;
    public bool CanRevert => !IsBusy && !IsForwardBusy && BrokerAvailable && PortProxyAvailable && HasSavedMappings && HasCurrentRules;
    public bool HasTechnicalDetails => TechnicalDetails.Length > 0;
    public IReadOnlyList<string> UserFacingWarnings => HasMappings
        ? Plan.Warnings.Select(LocalizeWarning).Distinct().ToArray()
        : ["先导入当前规则或添加映射，随后再预览更改。"];
    public string DeviceCountText => Devices.Count == 1 ? "1 台设备" : $"{Devices.Count} 台设备";
    public string RuleCountText => CurrentRules.Count == 1 ? "1 条生效规则" : $"{CurrentRules.Count} 条生效规则";
    public string AdbStateLabel => Snapshot.AdbAvailable ? "ADB 已就绪" : "ADB 当前不可用";
    public string PortProxyStateLabel => Snapshot.PortProxyAvailable ? "Windows 转发已就绪" : "Windows 转发不可用";
    public string ForwardModeLabel => $"{(IsWiredForward ? "有线设备" : "无线设备")} · {(IncludeSsh ? $"本机 + {RemoteHost} 远端" : "仅本机")}";
    public string ForwardWorkflowTitle => IsWiredForward ? "有线设备转发" : "无线设备转发";
    public string ForwardWorkflowDescription => IsWiredForward
        ? "选择 USB ADB 设备，配置设备 TCP 后通过主机端口向本机与可选远端提供连接。"
        : "选择已经连接的 host:port 网络 ADB 设备，保持设备网络配置并建立主机与可选远端转发。";
    public string ForwardPathSummary => IsWiredForward
        ? $"USB ADB → 设备 TCP {DevicePort} → 127.0.0.1:{LocalForwardPort} → 共享端口 {SharedPort}"
        : $"Wi-Fi ADB → 127.0.0.1:{LocalForwardPort} → 共享端口 {SharedPort}";
    public string ForwardEndpointSummary => $"127.0.0.1:{LocalForwardPort} · 共享端口 {SharedPort} · 设备端口 {DevicePort}";
    public string PreflightSummary => _preflight?.Summary ?? "尚未运行预检";
    public bool PreflightCanRun => _preflight?.CanRun ?? false;
    public bool CanPreflightForward => !IsForwardBusy && SelectedForwardDevice is not null;
    public bool CanStartForward => !IsForwardBusy &&
                                   !CanCleanupForward &&
                                   SelectedForwardDevice is not null &&
                                   PreflightCanRun;
    public bool CanRetryForward => !IsForwardBusy && _canRetryForward && SelectedForwardDevice is not null;
    public string RetryForwardLabel => _approvalRequired ? "批准并继续" : "继续重试";
    public bool CanCleanupForward => !IsForwardBusy && _cleanupState.HasWork;
    public string PlanSummary => !IsPreviewCurrent
        ? "配置已修改，请重新预览"
        : IsMappingsDirty && Plan.HasChanges
            ? $"将新增 {Plan.ToApply.Count} 条 · 移除 {Plan.ToRemove.Count} 条；保存映射后可应用"
        : Plan.HasChanges
            ? $"将新增 {Plan.ToApply.Count} 条 · 移除 {Plan.ToRemove.Count} 条"
            : "当前配置无需更改";

    private IReadOnlyList<AdbForwarderDevice> DevicesForMode(AdbForwardConnectionMode mode) =>
        Snapshot.ForwardDevices
            .Where(device => AdbForwardingWorkflowService.IsNetworkSerial(device.Id) ==
                             (mode == AdbForwardConnectionMode.Wireless))
            .ToArray();

    public bool IsMappingsDirty
    {
        get => _isMappingsDirty;
        private set
        {
            if (SetProperty(ref _isMappingsDirty, value))
            {
                OnPropertyChanged(nameof(CanSaveMappings));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(PlanSummary));
                ((AsyncRelayCommand)SaveMappingsCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedRouteId
    {
        get => _selectedRouteId;
        private set
        {
            if (SetProperty(ref _selectedRouteId, value))
            {
                OnPropertyChanged(nameof(IsForward));
                OnPropertyChanged(nameof(IsRules));
                OnPropertyChanged(nameof(IsDevices));
                OnPropertyChanged(nameof(IsActivity));
                OnPropertyChanged(nameof(IsDiagnostics));
                OnPropertyChanged(nameof(IsSettings));
            }
        }
    }

    public AdbForwarderPlan Plan
    {
        get => _plan;
        private set
        {
            if (SetProperty(ref _plan, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
                OnPropertyChanged(nameof(HasPlannedChanges));
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(UserFacingWarnings));
                OnPropertyChanged(nameof(CanApply));
                ((AsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ((AsyncRelayCommand)SaveMappingsCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)PreviewChangesCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)RevertCommand).NotifyCanExecuteChanged();
                ((AsyncRelayCommand)SaveEnvironmentCommand).NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanSaveMappings));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanRevert));
                OnPropertyChanged(nameof(CanSaveEnvironment));
            }
        }
    }

    public string ActionMessage
    {
        get => _actionMessage;
        private set => SetProperty(ref _actionMessage, value);
    }

    public string TechnicalDetails
    {
        get => _technicalDetails;
        private set
        {
            if (SetProperty(ref _technicalDetails, value))
            {
                OnPropertyChanged(nameof(HasTechnicalDetails));
            }
        }
    }
}
