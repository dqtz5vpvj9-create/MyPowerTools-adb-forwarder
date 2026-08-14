using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using AdbForwarder.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace AdbForwarder.Surface.ViewModels;

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
    public IReadOnlyList<AdbConfiguredForwardDevice> ActiveSharedDevices =>
        ConfiguredForwardDevices.Where(device => device.PortProxyReady).ToArray();
    public bool HasActiveSharedDevices => ActiveSharedDevices.Count > 0;
    public bool HasConfigurationError => Snapshot.ConfiguredState.Error.Length > 0;
    public string ConfigurationError => Snapshot.ConfiguredState.Error;
    public string ConfigurationPath => Snapshot.ConfiguredState.ConfigPath;
    public bool HasServiceStatus => Snapshot.ServiceStatus is not null;
    public bool ServiceIsActive => Snapshot.ServiceStatus?.IsActive == true;
    public bool ServiceNeedsAttention => Snapshot.ServiceStatus is not null && !Snapshot.ServiceStatus.IsActive;
    public string ServiceStateLabel => Snapshot.ServiceStatus is null
        ? "后台服务未连接"
        : Snapshot.ServiceStatus.IsActive ? "后台服务运行中" : "后台服务需要处理";
    public string ServiceSummary => Snapshot.ServiceStatus?.Summary ?? "刷新后重新连接 ADB Forwarder Service。";
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
                ((MptAsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
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
                    : $"已选择 {value.DisplayName}，可以开始共享。";
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
                OnPropertyChanged(nameof(ForwardConnectCommand));
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
                OnPropertyChanged(nameof(IsLocalOnly));
                OnPropertyChanged(nameof(ForwardConnectionHint));
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
                OnPropertyChanged(nameof(ForwardConnectionHint));
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
                OnPropertyChanged(nameof(HasForwardRun));
                OnPropertyChanged(nameof(ForwardIsReady));
                OnPropertyChanged(nameof(ForwardNeedsAttention));
                OnPropertyChanged(nameof(ForwardStatusLabel));
                OnPropertyChanged(nameof(CanPreview));
                OnPropertyChanged(nameof(CanSaveMappings));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanRevert));
                ((MptAsyncRelayCommand)RefreshCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)SaveMappingsCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)PreviewChangesCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)RevertCommand).NotifyCanExecuteChanged();
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
    public string ForwardConnectCommand => $"adb connect <这台电脑的 IP>:{SharedPort}";
    public string ForwardConnectionHint => IncludeSsh
        ? $"本机和 {RemoteHost} 都可通过共享端口连接。"
        : "共享成功后，同一网络中的电脑可通过显示的地址连接。";
    public string PreflightSummary => _preflight?.Summary ?? "尚未运行预检";
    public bool PreflightCanRun => _preflight?.CanRun ?? false;
    public bool CanPreflightForward => !IsForwardBusy && SelectedForwardDevice is not null;
    public bool CanStartForward => !IsForwardBusy &&
                                   !CanCleanupForward &&
                                   SelectedForwardDevice is not null;
    public bool CanRetryForward => !IsForwardBusy && _canRetryForward && SelectedForwardDevice is not null;
    public string RetryForwardLabel => _approvalRequired ? "批准并继续" : "继续重试";
    public bool CanCleanupForward => !IsForwardBusy && _cleanupState.HasWork;
    public bool HasForwardRun => _forwardHasRun || HasForwardLogs || CanRetryForward || CanCleanupForward;
    public bool ForwardIsReady => _forwardSucceeded && !IsForwardBusy;
    public bool ForwardNeedsAttention => _forwardHasRun && !_forwardSucceeded && !_forwardCleaned && !IsForwardBusy;
    public string ForwardStatusLabel => IsForwardBusy
        ? "正在设置"
        : _approvalRequired
            ? "等待管理员确认"
            : _forwardSucceeded
                ? "共享已就绪"
                : _forwardCleaned
                    ? "共享已停止"
                    : _forwardHasRun
                        ? "需要处理"
                        : "准备就绪";
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
                ((MptAsyncRelayCommand)SaveMappingsCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
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
                ((MptAsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
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
                ((MptAsyncRelayCommand)SaveMappingsCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)PreviewChangesCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)ApplyCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)RevertCommand).NotifyCanExecuteChanged();
                ((MptAsyncRelayCommand)SaveEnvironmentCommand).NotifyCanExecuteChanged();
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
