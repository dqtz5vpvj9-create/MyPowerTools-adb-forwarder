using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using AdbForwarder.Surface.Services;

using MyPowerTools.AvaloniaSdk;
namespace AdbForwarder.Surface.ViewModels;

public sealed partial class AdbForwarderViewModel : MyPowerTools.AvaloniaSdk.ToolSurfacePageViewModel, IDisposable
{
    private readonly Func<string, Task>? _navigateRoute;
    private readonly Func<IReadOnlyList<AdbForwarderMapping>, Task>? _saveMappings;
    private readonly Func<IReadOnlyList<AdbForwarderMapping>, Task<AdbForwarderPlan>>? _preview;
    private readonly Func<string, IReadOnlyList<AdbForwarderMapping>, Task>? _executeBrokered;
    private readonly AdbForwardingWorkflowService _forwarding;
    private readonly Func<AdbForwarderBrokerAction, AdbForwarderMapping, CancellationToken, Task<AdbForwarderBrokerRequestResult>>? _forwardBroker;
    private string _selectedRouteId;
    private AdbForwarderPlan _plan;
    private bool _isBusy;
    private bool _isPreviewCurrent = true;
    private bool _isMappingsDirty;
    private IReadOnlyList<AdbForwarderMapping> _savedMappings;
    private string _actionMessage = "已就绪";
    private string _technicalDetails = "";
    private CancellationTokenSource? _forwardCancellation;
    private AdbForwardCleanupState _cleanupState = AdbForwardCleanupState.Empty;
    private AdbForwarderDevice? _selectedForwardDevice;
    private AdbForwardPreflightResult? _preflight;
    private bool _isForwardBusy;
    private bool _canRetryForward;
    private bool _approvalRequired;
    private bool _forwardHasRun;
    private bool _forwardSucceeded;
    private bool _forwardCleaned;
    private int _localForwardPort = 15556;
    private int _sharedPort = 15557;
    private int _devicePort = 5555;
    private AdbForwardConnectionMode _connectionMode = AdbForwardConnectionMode.Wired;
    private bool _includeSsh;
    private string _remoteHost = "r743";
    private string _remoteAdbPath = "/android/aosp/out/soong/host/linux-x86/bin/adb";
    private string _forwardActionMessage = "选择一台已授权设备，然后开始共享。";

    public AdbForwarderViewModel(
        AdbForwarderSnapshot snapshot,
        string initialRouteId = "forward",
        Func<string, Task>? navigateRoute = null,
        Func<Task>? browseAllTools = null,
        Func<Task>? refresh = null,
        Func<IReadOnlyList<AdbForwarderMapping>, Task>? saveMappings = null,
        Func<IReadOnlyList<AdbForwarderMapping>, Task<AdbForwarderPlan>>? preview = null,
        Func<string, IReadOnlyList<AdbForwarderMapping>, Task>? executeBrokered = null,
        AdbForwardingWorkflowService? forwarding = null,
        Func<AdbForwarderBrokerAction, AdbForwarderMapping, CancellationToken, Task<AdbForwarderBrokerRequestResult>>? forwardBroker = null,
        Func<AdbForwarderEnvironmentSettings, Task>? saveEnvironment = null)
        : base(
            "ADB Forwarder",
            "将一台 USB ADB 设备安全转发到本机、Windows 共享端口与可选的远端 AOSP 主机",
            ToolSurfaceState.Ready)
    {
        Snapshot = snapshot;
        _plan = snapshot.Plan;
        _savedMappings = snapshot.ConfiguredMappings.ToArray();
        _selectedRouteId = NormalizeRoute(initialRouteId);
        _navigateRoute = navigateRoute;
        _saveMappings = saveMappings;
        _preview = preview;
        _executeBrokered = executeBrokered;
        _forwarding = forwarding ?? new AdbForwardingWorkflowService();
        _forwardBroker = forwardBroker;
        _saveEnvironment = saveEnvironment;
        _adbExecutablePath = snapshot.AdbPath;
        _wakeupPadDeviceId = snapshot.ConfiguredState.WakeupPadDeviceId;
        if (snapshot.ConfiguredState.Error.Length > 0)
        {
            _isEnvironmentDirty = true;
            _environmentMessage = $"当前设备配置需要处理：{snapshot.ConfiguredState.Error}";
        }
        var persisted = snapshot.PersistedWorkflow;
        if (persisted is not null)
        {
            _connectionMode = persisted.Request.ConnectionMode;
            _localForwardPort = persisted.Request.LocalForwardPort;
            _sharedPort = persisted.Request.SharedPort;
            _devicePort = persisted.Request.DevicePort;
            _includeSsh = persisted.Request.IncludeSsh;
            _remoteHost = persisted.Request.RemoteHost;
            _remoteAdbPath = persisted.Request.RemoteAdbPath;
            _cleanupState = persisted.Cleanup;
            _approvalRequired = persisted.ApprovalRequired;
            _canRetryForward = persisted.ApprovalRequired;
            _forwardActionMessage = persisted.ApprovalRequired
                ? "检测到待完成的一次性管理员审批。点击“批准并继续”。"
                : "已恢复上次工作流拥有的资源，可继续使用或执行清理。";
        }
        _selectedForwardDevice = persisted is null
            ? DevicesForMode(_connectionMode).FirstOrDefault(device => device.State == "device")
              ?? DevicesForMode(_connectionMode).FirstOrDefault()
            : DevicesForMode(_connectionMode).FirstOrDefault(device => string.Equals(device.Id, persisted.Request.DeviceSerial, StringComparison.Ordinal))
              ?? DevicesForMode(_connectionMode).FirstOrDefault(device => device.State == "device")
              ?? DevicesForMode(_connectionMode).FirstOrDefault();
        Mappings = new ObservableCollection<AdbForwarderMappingEditorViewModel>(
            snapshot.ConfiguredMappings.Select(CreateEditor));
        ConfiguredForwardDeviceEditors = new ObservableCollection<AdbForwardDeviceSettingEditorViewModel>(
            snapshot.ConfiguredState.ForwardDevices.Select(device => CreateForwardDeviceEditor(
                new AdbForwarderForwardDeviceSetting(device.DeviceId, device.Port))));
        ConfiguredWifiDeviceEditors = new ObservableCollection<AdbWifiDeviceSettingEditorViewModel>(
            snapshot.ConfiguredState.WifiDevices.Select(device => CreateWifiDeviceEditor(
                new AdbForwarderWifiDeviceSetting(
                    device.Name,
                    device.Enabled,
                    device.UsbSerial,
                    device.Host,
                    device.Port,
                    device.IntervalSeconds))));
        PreflightChecks = [];
        ForwardSteps = new ObservableCollection<AdbForwarderWorkflowStepViewModel>(
            _forwarding.CreatePendingSteps(_connectionMode).Select(step => new AdbForwarderWorkflowStepViewModel(step)));
        ForwardLogs = [];

        BrowseAllToolsCommand = new MptAsyncRelayCommand(() => browseAllTools?.Invoke() ?? Task.CompletedTask);
        RefreshCommand = new MptAsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask, () => !IsForwardBusy);
        ShowForwardCommand = RouteCommand("forward");
        ShowRulesCommand = RouteCommand("rules");
        ShowDevicesCommand = RouteCommand("devices");
        ShowActivityCommand = RouteCommand("activity");
        ShowDiagnosticsCommand = RouteCommand("diagnostics");
        ShowSettingsCommand = RouteCommand("settings");
        SelectWiredForwardCommand = new MptAsyncRelayCommand(() => SelectConnectionModeAsync(AdbForwardConnectionMode.Wired));
        SelectWirelessForwardCommand = new MptAsyncRelayCommand(() => SelectConnectionModeAsync(AdbForwardConnectionMode.Wireless));
        AddMappingCommand = new MptAsyncRelayCommand(AddMappingAsync);
        ImportCurrentRulesCommand = new MptAsyncRelayCommand(ImportCurrentRulesAsync);
        SaveMappingsCommand = new MptAsyncRelayCommand(SaveMappingsAsync, () => CanSaveMappings);
        PreviewChangesCommand = new MptAsyncRelayCommand(PreviewAsync, () => CanPreview);
        ApplyCommand = new MptAsyncRelayCommand(
            () => ExecuteBrokeredAsync("adb-forwarder.portproxy.apply", useSavedMappings: false),
            () => CanApply);
        RevertCommand = new MptAsyncRelayCommand(
            () => ExecuteBrokeredAsync("adb-forwarder.portproxy.revert", useSavedMappings: true),
            () => CanRevert);
        PreflightForwardCommand = new MptAsyncRelayCommand(PreflightForwardAsync, () => CanPreflightForward);
        StartForwardCommand = new MptAsyncRelayCommand(() => RunForwardAsync(retry: false), () => CanStartForward);
        RetryForwardCommand = new MptAsyncRelayCommand(() => RunForwardAsync(retry: true), () => CanRetryForward);
        CancelForwardCommand = new MptAsyncRelayCommand(CancelForwardAsync, () => IsForwardBusy);
        CleanupForwardCommand = new MptAsyncRelayCommand(CleanupForwardAsync, () => CanCleanupForward);
        AddConfiguredForwardDeviceCommand = new MptAsyncRelayCommand(AddConfiguredForwardDeviceAsync);
        AddConfiguredWifiDeviceCommand = new MptAsyncRelayCommand(AddConfiguredWifiDeviceAsync);
        SaveEnvironmentCommand = new MptAsyncRelayCommand(SaveEnvironmentAsync, () => CanSaveEnvironment);
    }

    public AdbForwarderSnapshot Snapshot { get; private set; }

    /// <summary>Replaces the snapshot in place and fires a broad change notification for refresh.</summary>
    public void UpdateSnapshot(AdbForwarderSnapshot fresh)
    {
        var selectedDeviceId = SelectedForwardDevice?.Id;
        Snapshot = fresh;
        SelectedForwardDevice = DevicesForMode(ConnectionMode).FirstOrDefault(device =>
                                    string.Equals(device.Id, selectedDeviceId, StringComparison.Ordinal))
                                ?? DevicesForMode(ConnectionMode).FirstOrDefault(device => device.IsOnline)
                                ?? DevicesForMode(ConnectionMode).FirstOrDefault();
        OnPropertyChanged(null);
    }
    public ObservableCollection<AdbForwarderMappingEditorViewModel> Mappings { get; }
    public ObservableCollection<AdbForwarderPreflightCheckViewModel> PreflightChecks { get; }
    public ObservableCollection<AdbForwarderWorkflowStepViewModel> ForwardSteps { get; }
    public ObservableCollection<AdbForwarderWorkflowLogViewModel> ForwardLogs { get; }
    public ObservableCollection<AdbForwardDeviceSettingEditorViewModel> ConfiguredForwardDeviceEditors { get; }
    public ObservableCollection<AdbWifiDeviceSettingEditorViewModel> ConfiguredWifiDeviceEditors { get; }
    public IReadOnlyList<AdbForwarderRule> CurrentRules => Snapshot.CurrentRules;
    public IReadOnlyList<AdbForwarderDevice> Devices => Snapshot.Devices;
    public IReadOnlyList<AdbForwarderActivity> Activity => Snapshot.Activity;
    public IReadOnlyList<AdbConfiguredForwardDevice> ConfiguredForwardDevices => Snapshot.ConfiguredState.ForwardDevices;
    public IReadOnlyList<AdbConfiguredWifiDevice> ConfiguredWifiDevices => Snapshot.ConfiguredState.WifiDevices;
    public ICommand BrowseAllToolsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ShowForwardCommand { get; }
    public ICommand ShowRulesCommand { get; }
    public ICommand ShowDevicesCommand { get; }
    public ICommand ShowActivityCommand { get; }
    public ICommand ShowDiagnosticsCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand SelectWiredForwardCommand { get; }
    public ICommand SelectWirelessForwardCommand { get; }
    public ICommand AddMappingCommand { get; }
    public ICommand ImportCurrentRulesCommand { get; }
    public ICommand SaveMappingsCommand { get; }
    public ICommand PreviewChangesCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand PreflightForwardCommand { get; }
    public ICommand StartForwardCommand { get; }
    public ICommand RetryForwardCommand { get; }
    public ICommand CancelForwardCommand { get; }
    public ICommand CleanupForwardCommand { get; }
    public ICommand AddConfiguredForwardDeviceCommand { get; }
    public ICommand AddConfiguredWifiDeviceCommand { get; }
    public ICommand SaveEnvironmentCommand { get; }
}
