using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class AdbForwarderViewModel : ToolProductPageViewModel, IDisposable
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
    private int _localForwardPort = 15556;
    private int _sharedPort = 15557;
    private int _devicePort = 5555;
    private AdbForwardConnectionMode _connectionMode = AdbForwardConnectionMode.Wired;
    private bool _includeSsh;
    private string _remoteHost = "r743";
    private string _remoteAdbPath = "/android/aosp/out/soong/host/linux-x86/bin/adb";
    private string _forwardActionMessage = "选择一台已授权设备，然后运行预检。";

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
        Func<AdbForwarderBrokerAction, AdbForwarderMapping, CancellationToken, Task<AdbForwarderBrokerRequestResult>>? forwardBroker = null)
        : base(
            "ADB Forwarder",
            "将一台 USB ADB 设备安全转发到本机、Windows 共享端口与可选的远端 AOSP 主机",
            ToolProductState.Ready)
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
        PreflightChecks = [];
        ForwardSteps = new ObservableCollection<AdbForwarderWorkflowStepViewModel>(
            _forwarding.CreatePendingSteps(_connectionMode).Select(step => new AdbForwarderWorkflowStepViewModel(step)));
        ForwardLogs = [];

        BrowseAllToolsCommand = new AsyncRelayCommand(() => browseAllTools?.Invoke() ?? Task.CompletedTask);
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask, () => !IsForwardBusy);
        ShowForwardCommand = RouteCommand("forward");
        ShowRulesCommand = RouteCommand("rules");
        ShowDevicesCommand = RouteCommand("devices");
        ShowActivityCommand = RouteCommand("activity");
        ShowDiagnosticsCommand = RouteCommand("diagnostics");
        SelectWiredForwardCommand = new AsyncRelayCommand(() => SelectConnectionModeAsync(AdbForwardConnectionMode.Wired));
        SelectWirelessForwardCommand = new AsyncRelayCommand(() => SelectConnectionModeAsync(AdbForwardConnectionMode.Wireless));
        AddMappingCommand = new AsyncRelayCommand(AddMappingAsync);
        ImportCurrentRulesCommand = new AsyncRelayCommand(ImportCurrentRulesAsync);
        SaveMappingsCommand = new AsyncRelayCommand(SaveMappingsAsync, () => CanSaveMappings);
        PreviewChangesCommand = new AsyncRelayCommand(PreviewAsync, () => CanPreview);
        ApplyCommand = new AsyncRelayCommand(
            () => ExecuteBrokeredAsync("adb-forwarder.portproxy.apply", useSavedMappings: false),
            () => CanApply);
        RevertCommand = new AsyncRelayCommand(
            () => ExecuteBrokeredAsync("adb-forwarder.portproxy.revert", useSavedMappings: true),
            () => CanRevert);
        PreflightForwardCommand = new AsyncRelayCommand(PreflightForwardAsync, () => CanPreflightForward);
        StartForwardCommand = new AsyncRelayCommand(() => RunForwardAsync(retry: false), () => CanStartForward);
        RetryForwardCommand = new AsyncRelayCommand(() => RunForwardAsync(retry: true), () => CanRetryForward);
        CancelForwardCommand = new AsyncRelayCommand(CancelForwardAsync, () => IsForwardBusy);
        CleanupForwardCommand = new AsyncRelayCommand(CleanupForwardAsync, () => CanCleanupForward);
    }

    public AdbForwarderSnapshot Snapshot { get; }
    public ObservableCollection<AdbForwarderMappingEditorViewModel> Mappings { get; }
    public ObservableCollection<AdbForwarderPreflightCheckViewModel> PreflightChecks { get; }
    public ObservableCollection<AdbForwarderWorkflowStepViewModel> ForwardSteps { get; }
    public ObservableCollection<AdbForwarderWorkflowLogViewModel> ForwardLogs { get; }
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
}
