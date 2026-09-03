using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace AdbForwarder.Surface.Services;

public interface IAdbForwarderProcessRunner
{
    Task<AdbForwarderProcessResult> RunAsync(
        AdbForwarderProcessRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> CanConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<int>> FindProcessIdsAsync(
        string processName,
        CancellationToken cancellationToken = default);

    Task<AdbForwarderProcessStopResult> StopProcessesAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken = default);

    Task<AdbForwarderLongRunningProcessResult> StartLongRunningAsync(
        AdbForwarderLongRunningProcessRequest request,
        CancellationToken cancellationToken = default);

    Task<AdbForwarderOwnedProcess?> GetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<AdbForwarderOwnedProcess?>(null);
}

public sealed record AdbForwarderProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? StandardInput = null,
    string? WorkingDirectory = null);

public sealed record AdbForwarderProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration)
{
    public bool Success => Started && !TimedOut && ExitCode == 0;
}

public sealed record AdbForwarderLongRunningProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan StartupTimeout,
    string? WorkingDirectory = null);

public sealed record AdbForwarderLongRunningProcessResult(
    bool Started,
    int ProcessId,
    string Error,
    AdbForwarderOwnedProcess? Ownership = null)
{
    public bool Success => Started && ProcessId > 0;
}

public sealed record AdbForwarderOwnedProcess(
    int ProcessId,
    long StartTimeUtcTicks,
    string ExecutablePath,
    string OwnershipToken);

public sealed record AdbForwarderProcessStopResult(
    IReadOnlyList<int> StoppedProcessIds,
    IReadOnlyList<int> RemainingProcessIds);

public sealed class AdbForwarderProcessRunner : IAdbForwarderProcessRunner
{
    private const int MaximumCapturedCharacters = 64 * 1024;

    public async Task<AdbForwarderProcessResult> RunAsync(
        AdbForwarderProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "进程超时必须大于零。");
        }

        var startedAt = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return FailedToStart(startedAt.Elapsed, "进程未能启动。");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (request.StandardInput is { } standardInput)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                var exited = await WaitAfterKillAsync(process).ConfigureAwait(false);
                var killStatus = exited ? "" : " 进程树停止状态未确认。";
                return new AdbForwarderProcessResult(
                    true,
                    -1,
                    ReadCompleted(outputTask),
                    $"命令在 {request.Timeout.TotalSeconds:0.#} 秒后超时。{killStatus} {ReadCompleted(errorTask)}".Trim(),
                    true,
                    startedAt.Elapsed);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitAfterKillAsync(process).ConfigureAwait(false);
                throw;
            }

            return new AdbForwarderProcessResult(
                true,
                process.ExitCode,
                Limit(await outputTask.ConfigureAwait(false)),
                Limit(await errorTask.ConfigureAwait(false)),
                false,
                startedAt.Elapsed);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return FailedToStart(startedAt.Elapsed, $"找不到可执行文件 {request.FileName}。");
        }
    }

    public async Task<bool> CanConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public Task<IReadOnlyList<int>> FindProcessIdsAsync(
        string processName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var name = Path.GetFileNameWithoutExtension(processName);
        IReadOnlyList<int> ids = Process.GetProcessesByName(name)
            .Select(process =>
            {
                using (process)
                {
                    return process.Id;
                }
            })
            .Distinct()
            .Order()
            .ToArray();
        return Task.FromResult(ids);
    }

    public async Task<AdbForwarderProcessStopResult> StopProcessesAsync(
        IReadOnlyList<int> processIds,
        CancellationToken cancellationToken = default)
    {
        var stopped = new List<int>();
        var remaining = new List<int>();
        foreach (var processId in processIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    stopped.Add(processId);
                    continue;
                }
                process.Kill(entireProcessTree: true);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                if (process.HasExited)
                {
                    stopped.Add(processId);
                }
                else
                {
                    remaining.Add(processId);
                }
            }
            catch (ArgumentException)
            {
                stopped.Add(processId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (ProcessHasExited(processId))
                {
                    stopped.Add(processId);
                }
                else
                {
                    remaining.Add(processId);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                if (ProcessHasExited(processId))
                {
                    stopped.Add(processId);
                }
                else
                {
                    remaining.Add(processId);
                }
            }
        }
        return new AdbForwarderProcessStopResult(stopped, remaining);
    }

    public async Task<AdbForwarderLongRunningProcessResult> StartLongRunningAsync(
        AdbForwarderLongRunningProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new AdbForwarderLongRunningProcessResult(false, 0, "进程未能启动。");
            }

            var settle = request.StartupTimeout < TimeSpan.FromSeconds(1)
                ? request.StartupTimeout
                : TimeSpan.FromSeconds(1);
            try
            {
                await Task.Delay(settle, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await WaitAfterKillAsync(process).ConfigureAwait(false);
                throw;
            }
            if (process.HasExited)
            {
                return new AdbForwarderLongRunningProcessResult(
                    false,
                    0,
                    $"隧道进程提前退出，退出码 {process.ExitCode}。");
            }
            var ownership = ReadProcessIdentity(process, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            return new AdbForwarderLongRunningProcessResult(true, process.Id, "", ownership);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            return new AdbForwarderLongRunningProcessResult(false, 0, $"找不到可执行文件 {request.FileName}。");
        }
    }

    public Task<AdbForwarderOwnedProcess?> GetProcessIdentityAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.GetProcessById(processId);
            return Task.FromResult<AdbForwarderOwnedProcess?>(ReadProcessIdentity(process, ""));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return Task.FromResult<AdbForwarderOwnedProcess?>(null);
        }
    }

    private static AdbForwarderOwnedProcess ReadProcessIdentity(Process process, string ownershipToken)
    {
        string executablePath;
        try
        {
            executablePath = process.MainModule?.FileName ?? "";
        }
        catch (Win32Exception)
        {
            executablePath = "";
        }
        return new AdbForwarderOwnedProcess(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            executablePath,
            ownershipToken);
    }

    private static AdbForwarderProcessResult FailedToStart(TimeSpan duration, string error)
    {
        return new AdbForwarderProcessResult(false, -1, "", error, false, duration);
    }

    private static string Limit(string value)
    {
        value = value.Replace("\0", "", StringComparison.Ordinal).Trim();
        return value.Length <= MaximumCapturedCharacters
            ? value
            : value[..MaximumCapturedCharacters] + "…";
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static string ReadCompleted(Task<string> readTask)
    {
        if (!readTask.IsCompletedSuccessfully)
        {
            return "";
        }
        return Limit(readTask.Result);
    }

    private static async Task<bool> WaitAfterKillAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}

public enum AdbForwarderBrokerAction
{
    Ensure,
    Remove
}

public enum AdbForwarderBrokerDisposition
{
    Applied,
    ApprovalRequired,
    Failed
}

public sealed record AdbForwarderBrokerRequestResult(
    AdbForwarderBrokerDisposition Disposition,
    string Message,
    bool Changed = false);

public enum AdbForwarderPreflightState
{
    Passed,
    Warning,
    Failed,
    Skipped
}

public enum AdbForwardConnectionMode
{
    Wired,
    Wireless
}

public sealed record AdbForwarderPreflightCheck(
    string Id,
    string Title,
    AdbForwarderPreflightState State,
    string Detail)
{
    public bool IsBlocking => State == AdbForwarderPreflightState.Failed;
}

public sealed record AdbForwardRequest(
    string DeviceSerial,
    int LocalForwardPort = 15556,
    int SharedPort = 15557,
    int DevicePort = 5555,
    bool IncludeSsh = false,
    string RemoteHost = "r743",
    string RemoteAdbPath = "/android/aosp/out/soong/host/linux-x86/bin/adb",
    AdbForwardConnectionMode ConnectionMode = AdbForwardConnectionMode.Wired);

public sealed record AdbForwardPreflightResult(
    bool CanRun,
    IReadOnlyList<AdbForwarderPreflightCheck> Checks,
    IReadOnlyList<AdbForwarderDevice> Devices,
    string DeviceTcpPort,
    bool AdbForwardPresent,
    bool AdbForwardConflict,
    bool PortProxyPresent,
    bool PortProxyConflict,
    bool LocalEndpointConnected,
    bool SharedEndpointConnected,
    bool SshForwardAvailable,
    bool SshReachable,
    string Summary);

public enum AdbForwarderStepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    ApprovalRequired,
    Canceled,
    Cleaned
}

public sealed record AdbForwarderWorkflowStep(
    string Id,
    string Title,
    AdbForwarderStepState State,
    string Detail);

public sealed record AdbForwarderWorkflowEvent(
    DateTimeOffset Time,
    AdbForwarderWorkflowStep Step,
    string Level,
    string Message);

public sealed record AdbForwardCleanupState(
    bool DeviceTcpChanged,
    string OriginalDeviceTcpPort,
    bool AdbForwardOwned,
    bool PortProxyRequested,
    bool PortProxyOwned,
    bool TunnelStarted,
    IReadOnlyList<int> TunnelProcessIds,
    IReadOnlyList<string> ConnectedEndpoints,
    IReadOnlyList<AdbForwarderOwnedProcess>? TunnelOwnership = null)
{
    public static AdbForwardCleanupState Empty { get; } = new(
        false,
        "",
        false,
        false,
        false,
        false,
        [],
        []);

    public bool HasWork => DeviceTcpChanged ||
                           AdbForwardOwned ||
                           PortProxyRequested ||
                           PortProxyOwned ||
                           TunnelStarted ||
                           TunnelProcessIds.Count > 0 ||
                           (TunnelOwnership?.Count ?? 0) > 0 ||
                           ConnectedEndpoints.Count > 0;
}

public sealed record AdbForwardWorkflowResult(
    bool Success,
    bool Canceled,
    bool ApprovalRequired,
    string Message,
    IReadOnlyList<AdbForwarderWorkflowStep> Steps,
    AdbForwardCleanupState CleanupState);

public sealed class AdbForwardingWorkflowService
{
    private const int MaximumConnectAttempts = 4;
    private static readonly Regex PortProxyRow = new(
        @"^\s*(?<listen>\S+)\s+(?<listenPort>\d{1,5})\s+(?<connect>\S+)\s+(?<connectPort>\d{1,5})\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeRemoteHost = new(
        @"^[A-Za-z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeRemoteAdbPath = new(
        @"^/[A-Za-z0-9._/-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly (string Id, string Title)[] ForwardSteps =
    [
        ("preflight", "检查设备与工具"),
        ("portproxy", "准备 Windows 共享端口"),
        ("tunnel-cleanup", "清理旧 SSH 隧道"),
        ("device-ready", "等待 USB ADB 设备"),
        ("device-tcp", "启用设备 ADB over TCP"),
        ("adb-forward", "创建主机 ADB 转发"),
        ("connect-local", "连接本机直连端点"),
        ("connect-shared", "连接 Windows 共享端点"),
        ("verify-local", "验证本机连接"),
        ("ssh-tunnel", "创建 SSH 反向隧道"),
        ("remote-aosp", "连接并 remount 远端 AOSP"),
        ("complete", "完成转发")
    ];
    private const string RemoteAospScript = """
        set -euo pipefail
        ADB="$1"
        ENDPOINT="127.0.0.1:$2"
        "$ADB" kill-server
        "$ADB" connect "$ENDPOINT"
        STATE="$("$ADB" devices | awk -v endpoint="$ENDPOINT" '$1 == endpoint { print $2 }')"
        test "$STATE" = "device"
        "$ADB" -s "$ENDPOINT" remount
        "$ADB" devices -l
        """;

    private readonly IAdbForwarderProcessRunner _runner;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _isWindows;
    private readonly string _adbPath;
    private readonly string _netshPath;
    private readonly string _wherePath;
    private readonly string _sshPath;
    private readonly string _sshForwardPath;
    private readonly bool _usesProductionCommandResolution;
    private readonly IAdbForwarderWorkflowStateStore _stateStore;

    public AdbForwardingWorkflowService(
        IAdbForwarderProcessRunner? runner = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        bool? isWindows = null,
        string adbPath = "adb",
        string sshPath = "ssh",
        string sshForwardPath = "ssh-forward.exe",
        IAdbForwarderWorkflowStateStore? stateStore = null)
    {
        var usesProductionRunner = runner is null;
        var resolveProductionCommands = runner is null || runner is AdbForwarderProcessRunner;
        _runner = runner ?? new AdbForwarderProcessRunner();
        _delay = delay ?? Task.Delay;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _usesProductionCommandResolution = resolveProductionCommands;
        _adbPath = adbPath;
        _netshPath = resolveProductionCommands ? ResolveSystemExecutable("netsh.exe") : "netsh";
        _wherePath = resolveProductionCommands ? ResolveSystemExecutable("where.exe") : "where.exe";
        _sshPath = resolveProductionCommands ? ResolveSshExecutable(sshPath) : sshPath;
        _sshForwardPath = resolveProductionCommands
            ? ResolveTunnelExecutable(sshForwardPath, _sshPath)
            : sshForwardPath;
        _stateStore = stateStore ?? (usesProductionRunner
            ? new AdbForwarderWorkflowStateStore()
            : new InMemoryAdbForwarderWorkflowStateStore());
    }

    public AdbForwarderPersistedWorkflowState? LoadPersistedState() => _stateStore.Load();

    public void PersistSession(AdbForwardRequest request, AdbForwardCleanupState cleanup, bool approvalRequired)
    {
        if (!cleanup.HasWork && !approvalRequired)
        {
            _stateStore.Clear();
            return;
        }
        _stateStore.Save(new AdbForwarderPersistedWorkflowState(request, cleanup, approvalRequired, DateTimeOffset.UtcNow));
    }

    public IReadOnlyList<AdbForwarderWorkflowStep> CreatePendingSteps(
        AdbForwardConnectionMode mode = AdbForwardConnectionMode.Wired)
    {
        return ForwardSteps
            .Select(step => new AdbForwarderWorkflowStep(
                step.Id,
                step.Id switch
                {
                    "device-ready" when mode == AdbForwardConnectionMode.Wireless => "检查无线 ADB 设备",
                    "device-tcp" when mode == AdbForwardConnectionMode.Wireless => "保留无线设备网络配置",
                    _ => step.Title
                },
                AdbForwarderStepState.Pending,
                "等待执行"))
            .ToArray();
    }

    public async Task<IReadOnlyList<AdbForwarderDevice>> ScanDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            _adbPath,
            ["devices", "-l"],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        return result.Success ? ParseDevices(result.StandardOutput) : [];
    }

    public async Task<AdbForwardPreflightResult> PreflightAsync(
        AdbForwardRequest request,
        bool brokerAvailable,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var checks = new List<AdbForwarderPreflightCheck>();
        AddCheck(
            checks,
            "platform",
            "Windows 环境",
            _isWindows ? AdbForwarderPreflightState.Passed : AdbForwarderPreflightState.Failed,
            _isWindows ? "当前运行于 Windows。" : "原转发工作流只支持 Windows。");

        var adbVersion = await RunAsync(_adbPath, ["version"], TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        AddCheck(
            checks,
            "adb",
            "ADB 工具",
            adbVersion.Success ? AdbForwarderPreflightState.Passed : AdbForwarderPreflightState.Failed,
            adbVersion.Success ? FirstLine(adbVersion.StandardOutput) : Failure(adbVersion));

        IReadOnlyList<AdbForwarderDevice> devices = [];
        var tcpPort = "";
        var forwardPresent = false;
        var forwardConflict = false;
        if (adbVersion.Success)
        {
            devices = await ScanDevicesAsync(cancellationToken).ConfigureAwait(false);
            var selected = devices.FirstOrDefault(device =>
                string.Equals(device.Id, request.DeviceSerial, StringComparison.Ordinal));
            var state = selected?.State ?? "missing";
            var deviceState = state switch
            {
                "device" => AdbForwarderPreflightState.Passed,
                "unauthorized" or "offline" or "missing" => AdbForwarderPreflightState.Failed,
                _ => AdbForwarderPreflightState.Failed
            };
            AddCheck(
                checks,
                "device",
                "所选 ADB 设备",
                deviceState,
                selected is null
                    ? "当前设备列表中找不到所选序列号。"
                    : state == "device"
                        ? $"{selected.Model} · {AdbForwarderRedaction.DisplayDeviceId(selected.Id)} · {ConnectionModeLabel(request.ConnectionMode)}已连接"
                        : $"{AdbForwarderRedaction.DisplayDeviceId(selected.Id)} 当前状态为 {state}，请恢复{ConnectionModeLabel(request.ConnectionMode)}连接。");

            if (state == "device")
            {
                if (request.ConnectionMode == AdbForwardConnectionMode.Wired)
                {
                    var property = await RunAdbAsync(
                        request.DeviceSerial,
                        ["shell", "getprop", "persist.adb.tcp.port"],
                        TimeSpan.FromSeconds(6),
                        cancellationToken).ConfigureAwait(false);
                    tcpPort = property.StandardOutput.Trim();
                    AddCheck(
                        checks,
                        "device-tcp",
                        "设备 TCP 端口",
                        property.Success
                            ? tcpPort == request.DevicePort.ToString(CultureInfo.InvariantCulture)
                                ? AdbForwarderPreflightState.Passed
                                : AdbForwarderPreflightState.Warning
                            : AdbForwarderPreflightState.Failed,
                        property.Success
                            ? tcpPort == request.DevicePort.ToString(CultureInfo.InvariantCulture)
                                ? $"persist.adb.tcp.port 已为 {request.DevicePort}。"
                                : $"当前值为 {DisplayValue(tcpPort)}；运行时会设置为 {request.DevicePort}。"
                            : Failure(property));
                }
                else
                {
                    tcpPort = NetworkPort(request.DeviceSerial).ToString(CultureInfo.InvariantCulture);
                    AddCheck(
                        checks,
                        "device-tcp",
                        "无线 ADB 会话",
                        AdbForwarderPreflightState.Passed,
                        $"{AdbForwarderRedaction.DisplayDeviceId(request.DeviceSerial)} 已通过网络 ADB 连接；不会修改设备 persist.adb.tcp.port。" );
                }

                var forwards = await RunAdbAsync(
                    request.DeviceSerial,
                    ["forward", "--list"],
                    TimeSpan.FromSeconds(6),
                    cancellationToken).ConfigureAwait(false);
                forwardPresent = forwards.Success && HasAdbForward(
                    forwards.StandardOutput,
                    request.DeviceSerial,
                    request.LocalForwardPort,
                    request.DevicePort);
                forwardConflict = forwards.Success && HasAdbForwardConflict(
                    forwards.StandardOutput,
                    request.DeviceSerial,
                    request.LocalForwardPort,
                    request.DevicePort);
                AddCheck(
                    checks,
                    "adb-forward",
                    "ADB 主机转发",
                    forwardConflict
                        ? AdbForwarderPreflightState.Failed
                        : forwardPresent
                            ? AdbForwarderPreflightState.Passed
                            : AdbForwarderPreflightState.Warning,
                    forwardConflict
                        ? $"tcp:{request.LocalForwardPort} 已被其他 ADB forward 占用；工作流不会覆盖它。"
                        : forwardPresent
                        ? $"tcp:{request.LocalForwardPort} 已转发至设备 tcp:{request.DevicePort}。"
                        : $"运行时会创建 tcp:{request.LocalForwardPort} → tcp:{request.DevicePort}。");
            }
        }

        var portProxyResult = _isWindows
            ? await RunAsync(
                _netshPath,
                ["interface", "portproxy", "show", "v4tov4"],
                TimeSpan.FromSeconds(6),
                cancellationToken).ConfigureAwait(false)
            : new AdbForwarderProcessResult(false, -1, "", "非 Windows 平台", false, TimeSpan.Zero);
        var portProxyPresent = portProxyResult.Success && HasPortProxyRule(
            portProxyResult.StandardOutput,
            "0.0.0.0",
            request.SharedPort,
            "127.0.0.1",
            request.LocalForwardPort);
        var portProxyConflict = portProxyResult.Success && HasPortProxyConflict(
            portProxyResult.StandardOutput,
            "0.0.0.0",
            request.SharedPort,
            "127.0.0.1",
            request.LocalForwardPort);
        AddCheck(
            checks,
            "portproxy",
            "Windows portproxy",
            portProxyResult.Success
                ? portProxyConflict
                    ? AdbForwarderPreflightState.Failed
                    : portProxyPresent
                    ? AdbForwarderPreflightState.Passed
                    : brokerAvailable
                        ? AdbForwarderPreflightState.Warning
                        : AdbForwarderPreflightState.Failed
                : AdbForwarderPreflightState.Failed,
            !portProxyResult.Success
                ? Failure(portProxyResult)
                : portProxyConflict
                    ? $"0.0.0.0:{request.SharedPort} 已指向其他目标；工作流不会覆盖它。"
                    : portProxyPresent
                    ? $"0.0.0.0:{request.SharedPort} → 127.0.0.1:{request.LocalForwardPort} 已存在。"
                    : brokerAvailable
                        ? "缺少规则；开始时会请求 NetworkBroker 管理员确认。"
                        : "缺少规则，NetworkBroker 当前不可用。");

        var localEndpoint = Endpoint(request.LocalForwardPort);
        var sharedEndpoint = Endpoint(request.SharedPort);
        var localState = DeviceState(devices, localEndpoint);
        var sharedState = DeviceState(devices, sharedEndpoint);
        var localOpen = await _runner.CanConnectAsync(
            "127.0.0.1",
            request.LocalForwardPort,
            TimeSpan.FromMilliseconds(800),
            cancellationToken).ConfigureAwait(false);
        var sharedOpen = await _runner.CanConnectAsync(
            "127.0.0.1",
            request.SharedPort,
            TimeSpan.FromMilliseconds(800),
            cancellationToken).ConfigureAwait(false);
        AddEndpointCheck(
            checks,
            "local-endpoint",
            "本机直连端口",
            localEndpoint,
            localState,
            localOpen,
            forwardPresent);
        AddEndpointCheck(
            checks,
            "shared-endpoint",
            "Windows 共享端口",
            sharedEndpoint,
            sharedState,
            sharedOpen,
            portProxyPresent);

        var sshForwardAvailable = false;
        var sshReachable = false;
        if (request.IncludeSsh)
        {
            string tunnelClientDetail;
            if (_usesProductionCommandResolution)
            {
                sshForwardAvailable = File.Exists(_sshForwardPath);
                tunnelClientDetail = sshForwardAvailable
                    ? _sshForwardPath
                    : $"找不到可信 SSH 隧道客户端：{_sshForwardPath}";
            }
            else
            {
                var where = await RunAsync(
                    _wherePath,
                    [_sshForwardPath],
                    TimeSpan.FromSeconds(4),
                    cancellationToken).ConfigureAwait(false);
                sshForwardAvailable = where.Success;
                tunnelClientDetail = where.Success ? FirstLine(where.StandardOutput) : Failure(where);
            }
            AddCheck(
                checks,
                "ssh-forward",
                "SSH 隧道客户端",
                sshForwardAvailable ? AdbForwarderPreflightState.Passed : AdbForwarderPreflightState.Failed,
                tunnelClientDetail);

            var ssh = await RunAsync(
                _sshPath,
                ["-o", "BatchMode=yes", "-o", "ConnectTimeout=5", request.RemoteHost, "true"],
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);
            sshReachable = ssh.Success;
            AddCheck(
                checks,
                "ssh",
                "SSH 远端",
                ssh.Success ? AdbForwarderPreflightState.Passed : AdbForwarderPreflightState.Failed,
                ssh.Success ? $"{request.RemoteHost} 可通过批处理模式连接。" : Failure(ssh));
        }
        else
        {
            AddCheck(
                checks,
                "ssh-forward",
                "SSH 远端",
                AdbForwarderPreflightState.Skipped,
                "当前选择仅建立本机转发。");
        }

        checks = checks
            .Select(check => check with { Detail = RedactSensitive(check.Detail, request.DeviceSerial) })
            .ToList();
        tcpPort = RedactSensitive(tcpPort, request.DeviceSerial);
        var canRun = checks.All(check => !check.IsBlocking);
        return new AdbForwardPreflightResult(
            canRun,
            checks,
            devices,
            tcpPort,
            forwardPresent,
            forwardConflict,
            portProxyPresent,
            portProxyConflict,
            string.Equals(localState, "device", StringComparison.OrdinalIgnoreCase),
            string.Equals(sharedState, "device", StringComparison.OrdinalIgnoreCase),
            sshForwardAvailable,
            sshReachable,
            canRun ? "预检通过，可以开始转发。" : "预检发现阻塞项，请先修复标红项目。" );
    }

    public async Task<AdbForwardWorkflowResult> RunForwardAsync(
        AdbForwardRequest request,
        AdbForwardCleanupState? previousCleanup,
        Func<AdbForwarderBrokerAction, AdbForwarderMapping, CancellationToken, Task<AdbForwarderBrokerRequestResult>>? broker,
        Func<AdbForwarderWorkflowEvent, Task>? onEvent,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var tracker = new StepTracker(CreatePendingSteps(request.ConnectionMode), onEvent, request.DeviceSerial);
        var persisted = _stateStore.Load();
        var restoredCleanup = previousCleanup ??
            (persisted is not null && SameRequest(persisted.Request, request)
                ? persisted.Cleanup
                : AdbForwardCleanupState.Empty);
        var cleanup = new CleanupBuilder(restoredCleanup);
        var currentStep = "preflight";
        try
        {
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在执行只读预检。", "info").ConfigureAwait(false);
            var preflight = await PreflightAsync(request, broker is not null, cancellationToken).ConfigureAwait(false);
            if (!preflight.CanRun)
            {
                var detail = string.Join(" · ", preflight.Checks.Where(check => check.IsBlocking).Select(check => check.Detail));
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Failed, detail, "error").ConfigureAwait(false);
                return tracker.Result(false, false, false, preflight.Summary, cleanup.Build());
            }
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, preflight.Summary, "success").ConfigureAwait(false);

            currentStep = "portproxy";
            if (!preflight.PortProxyPresent)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在请求 NetworkBroker 创建共享规则。", "info").ConfigureAwait(false);
                if (broker is null)
                {
                    throw new WorkflowFailure(currentStep, "NetworkBroker 当前不可用。");
                }

                var mapping = PortProxyMapping(request);
                var decision = await broker(AdbForwarderBrokerAction.Ensure, mapping, cancellationToken).ConfigureAwait(false);
                cleanup.PortProxyRequested = true;
                if (decision.Disposition == AdbForwarderBrokerDisposition.ApprovalRequired)
                {
                    await tracker.EmitAsync(currentStep, AdbForwarderStepState.ApprovalRequired, decision.Message, "warning").ConfigureAwait(false);
                    return tracker.Result(false, false, true, "请完成管理员确认，然后点击重试。", cleanup.Build());
                }
                if (decision.Disposition == AdbForwarderBrokerDisposition.Failed)
                {
                    throw new WorkflowFailure(currentStep, decision.Message);
                }

                var portProxyNowPresent = await PortProxyPresentAsync(request, cancellationToken).ConfigureAwait(false);
                if (!portProxyNowPresent)
                {
                    throw new WorkflowFailure(currentStep, "NetworkBroker 返回成功，但 netsh 尚未显示目标规则。");
                }
                cleanup.PortProxyRequested = false;
                cleanup.PortProxyOwned = decision.Changed;
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, decision.Message, "success").ConfigureAwait(false);
            }
            else
            {
                if (cleanup.PortProxyRequested)
                {
                    cleanup.PortProxyRequested = false;
                    await tracker.EmitAsync(
                        currentStep,
                        AdbForwarderStepState.Skipped,
                        "目标规则已存在，但当前执行没有收到一次性 Broker 的成功回执；规则按外部资源保留。",
                        "warning").ConfigureAwait(false);
                }
                else
                {
                    await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "目标 portproxy 规则已存在，视为外部资源并保持原有所有权。", "info").ConfigureAwait(false);
                }
            }

            currentStep = "tunnel-cleanup";
            if (!request.IncludeSsh)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "仅本机模式无需处理 SSH 隧道。", "info").ConfigureAwait(false);
            }
            else if (cleanup.TunnelProcessIds.Count > 0 || cleanup.TunnelOwnership.Count > 0)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在关闭上一次尝试创建的 SSH 隧道。", "info").ConfigureAwait(false);
                var stop = await StopVerifiedOwnedTunnelsAsync(cleanup, cancellationToken).ConfigureAwait(false);
                ApplyTunnelStopOutcome(cleanup, stop);
                if (stop.RemainingOwnership.Count > 0)
                {
                    throw new WorkflowFailure(
                        currentStep,
                        $"仍有 {stop.RemainingOwnership.Count} 个本工作流隧道进程未退出，已保留进程标识供重试清理。");
                }
                await tracker.EmitAsync(
                    currentStep,
                    stop.StoppedCount > 0 ? AdbForwarderStepState.Succeeded : AdbForwarderStepState.Skipped,
                    stop.StoppedCount > 0 ? $"已停止本工作流拥有的 {stop.StoppedCount} 个隧道进程。" : "持久化进程标识已失效；系统中的其他 ssh-forward 保持运行。",
                    stop.StoppedCount > 0 ? "success" : "warning").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "没有本工作流拥有的旧 SSH 隧道。", "info").ConfigureAwait(false);
            }

            currentStep = "device-ready";
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, $"正在等待所选{ConnectionModeLabel(request.ConnectionMode)}设备。", "info").ConfigureAwait(false);
            EnsureSuccess(
                currentStep,
                await RunAdbAsync(request.DeviceSerial, ["wait-for-device"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false));
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, $"{ConnectionModeLabel(request.ConnectionMode)} ADB 设备已就绪。", "success").ConfigureAwait(false);

            currentStep = "device-tcp";
            if (request.ConnectionMode == AdbForwardConnectionMode.Wireless)
            {
                await tracker.EmitAsync(
                    currentStep,
                    AdbForwarderStepState.Skipped,
                    "无线设备已经通过网络 ADB 连接，不修改 persist.adb.tcp.port。",
                    "info").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在读取 persist.adb.tcp.port。", "info").ConfigureAwait(false);
                var property = await RunAdbAsync(
                    request.DeviceSerial,
                    ["shell", "getprop", "persist.adb.tcp.port"],
                    TimeSpan.FromSeconds(6),
                    cancellationToken).ConfigureAwait(false);
                EnsureSuccess(currentStep, property);
                var originalTcpPort = property.StandardOutput.Trim();
                if (originalTcpPort != request.DevicePort.ToString(CultureInfo.InvariantCulture))
                {
                    EnsureSuccess(
                        currentStep,
                        await RunAdbAsync(
                            request.DeviceSerial,
                            ["shell", "setprop", "persist.adb.tcp.port", request.DevicePort.ToString(CultureInfo.InvariantCulture)],
                            TimeSpan.FromSeconds(8),
                            cancellationToken).ConfigureAwait(false));
                    var verifiedProperty = await RunAdbAsync(
                        request.DeviceSerial,
                        ["shell", "getprop", "persist.adb.tcp.port"],
                        TimeSpan.FromSeconds(6),
                        cancellationToken).ConfigureAwait(false);
                    EnsureSuccess(currentStep, verifiedProperty);
                    if (!string.Equals(
                            verifiedProperty.StandardOutput.Trim(),
                            request.DevicePort.ToString(CultureInfo.InvariantCulture),
                            StringComparison.Ordinal))
                    {
                        throw new WorkflowFailure(currentStep, "设备未保存目标 persist.adb.tcp.port 值。");
                    }
                    cleanup.DeviceTcpChanged = true;
                    cleanup.OriginalDeviceTcpPort = originalTcpPort;
                    EnsureSuccess(
                        currentStep,
                        await RunAdbAsync(
                            request.DeviceSerial,
                            ["tcpip", request.DevicePort.ToString(CultureInfo.InvariantCulture)],
                            TimeSpan.FromSeconds(12),
                            cancellationToken).ConfigureAwait(false));
                    await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, $"设备 ADB TCP 端口已切换到 {request.DevicePort}。", "success").ConfigureAwait(false);
                }
                else
                {
                    await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, $"设备已使用 TCP {request.DevicePort}。", "info").ConfigureAwait(false);
                }
            }

            EnsureSuccess(
                currentStep,
                await RunAdbAsync(request.DeviceSerial, ["wait-for-device"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false));

            currentStep = "adb-forward";
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在创建 adb -a forward。", "info").ConfigureAwait(false);
            EnsureSuccess(
                currentStep,
                await RunAdbAsync(
                    request.DeviceSerial,
                    ["-a", "forward", $"tcp:{request.LocalForwardPort}", $"tcp:{request.DevicePort}"],
                    TimeSpan.FromSeconds(10),
                    cancellationToken).ConfigureAwait(false));
            if (!preflight.AdbForwardPresent)
            {
                cleanup.AdbForwardOwned = true;
            }
            await tracker.EmitAsync(
                currentStep,
                AdbForwarderStepState.Succeeded,
                $"tcp:{request.LocalForwardPort} → device tcp:{request.DevicePort} 已建立。",
                "success").ConfigureAwait(false);

            currentStep = "connect-local";
            await ConnectEndpointAsync(
                currentStep,
                Endpoint(request.LocalForwardPort),
                preflight.LocalEndpointConnected,
                cleanup,
                tracker,
                cancellationToken).ConfigureAwait(false);

            currentStep = "connect-shared";
            await ConnectEndpointAsync(
                currentStep,
                Endpoint(request.SharedPort),
                preflight.SharedEndpointConnected,
                cleanup,
                tracker,
                cancellationToken).ConfigureAwait(false);

            currentStep = "verify-local";
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在验证两个本机 ADB 端点。", "info").ConfigureAwait(false);
            var verifiedDevices = await ScanDevicesAsync(cancellationToken).ConfigureAwait(false);
            var missing = new[] { Endpoint(request.LocalForwardPort), Endpoint(request.SharedPort) }
                .Where(endpoint => !string.Equals(DeviceState(verifiedDevices, endpoint), "device", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new WorkflowFailure(currentStep, $"以下端点尚未进入 device 状态：{string.Join(", ", missing)}");
            }
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, "两个本机 ADB 端点均为 device。", "success").ConfigureAwait(false);

            currentStep = "ssh-tunnel";
            if (!request.IncludeSsh)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "已选择仅本机转发。", "info").ConfigureAwait(false);
                await tracker.EmitAsync("remote-aosp", AdbForwarderStepState.Skipped, "未执行远端 AOSP 操作。", "info").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, $"正在建立 {request.RemoteHost} 反向隧道。", "info").ConfigureAwait(false);
                var tunnel = await _runner.StartLongRunningAsync(
                    new AdbForwarderLongRunningProcessRequest(
                        _sshForwardPath,
                        [
                            "-CNg",
                            "-o", "BatchMode=yes",
                            "-o", "ExitOnForwardFailure=yes",
                            "-o", "ConnectTimeout=8",
                            "-o", "ServerAliveInterval=15",
                            "-o", "ServerAliveCountMax=2",
                            "-R", $"{request.SharedPort}:127.0.0.1:{request.SharedPort}",
                            request.RemoteHost
                        ],
                        TimeSpan.FromSeconds(3)),
                    cancellationToken).ConfigureAwait(false);
                if (!tunnel.Success)
                {
                    throw new WorkflowFailure(currentStep, tunnel.Error);
                }
                cleanup.TunnelStarted = true;
                cleanup.TunnelProcessIds = [tunnel.ProcessId];
                cleanup.TunnelOwnership = tunnel.Ownership is null ? [] : [tunnel.Ownership];
                PersistSession(request, cleanup.Build(), approvalRequired: false);
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, "SSH 反向隧道已建立。", "success").ConfigureAwait(false);

                currentStep = "remote-aosp";
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在远端执行 AOSP adb kill/connect/remount。", "warning").ConfigureAwait(false);
                var remote = await RunAsync(
                    _sshPath,
                    [
                        "-o", "BatchMode=yes",
                        "-o", "ConnectTimeout=8",
                        request.RemoteHost,
                        "bash", "-s", "--",
                        request.RemoteAdbPath,
                        request.SharedPort.ToString(CultureInfo.InvariantCulture)
                    ],
                    TimeSpan.FromSeconds(45),
                    cancellationToken,
                    RemoteAospScript).ConfigureAwait(false);
                EnsureSuccess(currentStep, remote);
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Succeeded, "远端 AOSP adb 已连接并完成 remount。", "success").ConfigureAwait(false);
            }

            currentStep = "complete";
            await tracker.EmitAsync(
                currentStep,
                AdbForwarderStepState.Succeeded,
                request.IncludeSsh ? "本机与远端 AOSP 转发均已完成。" : "本机 ADB 转发已完成。",
                "success").ConfigureAwait(false);
            return tracker.Result(true, false, false, "ADB 转发工作流已完成。", cleanup.Build());
        }
        catch (OperationCanceledException)
        {
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Canceled, "用户取消了当前工作流。", "warning").ConfigureAwait(false);
            return tracker.Result(false, true, false, "工作流已取消，可以执行清理或重试。", cleanup.Build());
        }
        catch (WorkflowFailure failure)
        {
            await tracker.EmitAsync(failure.StepId, AdbForwarderStepState.Failed, failure.Message, "error").ConfigureAwait(false);
            return tracker.Result(false, false, false, failure.Message, cleanup.Build());
        }
        catch (Exception ex)
        {
            var message = $"{currentStep} 发生未预期错误：{ex.GetType().Name}";
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Failed, message, "error").ConfigureAwait(false);
            return tracker.Result(false, false, false, message, cleanup.Build());
        }
        finally
        {
            PersistSession(request, cleanup.Build(), approvalRequired: false);
        }
    }

    public async Task<AdbForwardWorkflowResult> CleanupAsync(
        AdbForwardRequest request,
        AdbForwardCleanupState cleanupState,
        Func<AdbForwarderBrokerAction, AdbForwarderMapping, CancellationToken, Task<AdbForwarderBrokerRequestResult>>? broker,
        Func<AdbForwarderWorkflowEvent, Task>? onEvent,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var cleanupSteps = new[]
        {
            new AdbForwarderWorkflowStep("cleanup-tunnel", "停止 SSH 隧道", AdbForwarderStepState.Pending, "等待执行"),
            new AdbForwarderWorkflowStep("cleanup-connections", "断开本机 ADB 端点", AdbForwarderStepState.Pending, "等待执行"),
            new AdbForwarderWorkflowStep("cleanup-forward", "移除主机 ADB 转发", AdbForwarderStepState.Pending, "等待执行"),
            new AdbForwarderWorkflowStep("cleanup-device", "恢复设备 ADB 模式", AdbForwarderStepState.Pending, "等待执行"),
            new AdbForwarderWorkflowStep("cleanup-portproxy", "移除工作流共享规则", AdbForwarderStepState.Pending, "等待执行")
        };
        var tracker = new StepTracker(cleanupSteps, onEvent, request.DeviceSerial);
        var cleanup = new CleanupBuilder(cleanupState);
        var currentStep = "cleanup-tunnel";
        try
        {
            if (cleanup.TunnelProcessIds.Count > 0 || cleanup.TunnelOwnership.Count > 0)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在停止 ssh-forward。", "info").ConfigureAwait(false);
                var stop = await StopVerifiedOwnedTunnelsAsync(cleanup, cancellationToken).ConfigureAwait(false);
                ApplyTunnelStopOutcome(cleanup, stop);
                if (stop.RemainingOwnership.Count > 0)
                {
                    throw new WorkflowFailure(
                        currentStep,
                        $"仍有 {stop.RemainingOwnership.Count} 个本工作流隧道进程未退出，进程标识已保留，可以重试清理。");
                }
                await tracker.EmitAsync(
                    currentStep,
                    stop.StoppedCount > 0 ? AdbForwarderStepState.Cleaned : AdbForwarderStepState.Skipped,
                    stop.StoppedCount > 0 ? "SSH 隧道已停止。" : "未找到与持久化身份一致的本工具隧道；其他进程保持运行。",
                    stop.StoppedCount > 0 ? "success" : "warning").ConfigureAwait(false);
            }
            else if (cleanup.TunnelStarted)
            {
                cleanup.TunnelStarted = false;
                await tracker.EmitAsync(
                    currentStep,
                    AdbForwarderStepState.Skipped,
                    "缺少可验证的本工作流进程标识，已保留系统中的其他 ssh-forward 进程。",
                    "warning").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "本次工作流没有创建 SSH 隧道。", "info").ConfigureAwait(false);
            }

            currentStep = "cleanup-connections";
            if (cleanup.ConnectedEndpoints.Count > 0)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在断开工作流创建的 ADB 端点。", "info").ConfigureAwait(false);
                foreach (var endpoint in cleanup.ConnectedEndpoints)
                {
                    EnsureSuccess(
                        currentStep,
                        await RunAsync(_adbPath, ["disconnect", endpoint], TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false));
                }
                cleanup.ConnectedEndpoints = [];
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Cleaned, "工作流创建的 ADB 端点已断开。", "success").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "没有需要断开的 ADB 端点。", "info").ConfigureAwait(false);
            }

            currentStep = "cleanup-forward";
            if (cleanup.AdbForwardOwned)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在移除 adb forward。", "info").ConfigureAwait(false);
                EnsureSuccess(
                    currentStep,
                    await RunAdbAsync(
                        request.DeviceSerial,
                        ["forward", "--remove", $"tcp:{request.LocalForwardPort}"],
                        TimeSpan.FromSeconds(8),
                        cancellationToken).ConfigureAwait(false));
                cleanup.AdbForwardOwned = false;
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Cleaned, "主机 ADB 转发已移除。", "success").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "原有 ADB forward 保持不变。", "info").ConfigureAwait(false);
            }

            currentStep = "cleanup-device";
            if (cleanup.DeviceTcpChanged)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在恢复设备 ADB TCP 设置。", "info").ConfigureAwait(false);
                var original = NormalizeOriginalTcpPort(cleanup.OriginalDeviceTcpPort);
                EnsureSuccess(
                    currentStep,
                    await RunAdbAsync(
                        request.DeviceSerial,
                        ["shell", "setprop", "persist.adb.tcp.port", original.PropertyValue],
                        TimeSpan.FromSeconds(8),
                        cancellationToken).ConfigureAwait(false));
                EnsureSuccess(
                    currentStep,
                    await RunAdbAsync(
                        request.DeviceSerial,
                        original.UseUsb
                            ? ["usb"]
                            : ["tcpip", original.PropertyValue],
                        TimeSpan.FromSeconds(12),
                        cancellationToken).ConfigureAwait(false));
                cleanup.DeviceTcpChanged = false;
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Cleaned, "设备 ADB 模式已恢复。", "success").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "设备 TCP 设置未被本次工作流修改。", "info").ConfigureAwait(false);
            }

            currentStep = "cleanup-portproxy";
            if (cleanup.PortProxyOwned)
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Running, "正在请求 NetworkBroker 移除共享规则。", "info").ConfigureAwait(false);
                if (broker is null)
                {
                    throw new WorkflowFailure(currentStep, "NetworkBroker 当前不可用。");
                }
                var decision = await broker(
                    AdbForwarderBrokerAction.Remove,
                    PortProxyMapping(request),
                    cancellationToken).ConfigureAwait(false);
                if (decision.Disposition == AdbForwarderBrokerDisposition.ApprovalRequired)
                {
                    await tracker.EmitAsync(currentStep, AdbForwarderStepState.ApprovalRequired, decision.Message, "warning").ConfigureAwait(false);
                    return tracker.Result(false, false, true, "请完成管理员确认，然后再次执行清理。", cleanup.Build());
                }
                if (decision.Disposition == AdbForwarderBrokerDisposition.Failed)
                {
                    throw new WorkflowFailure(currentStep, decision.Message);
                }
                cleanup.PortProxyOwned = false;
                cleanup.PortProxyRequested = false;
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Cleaned, decision.Message, "success").ConfigureAwait(false);
            }
            else if (cleanup.PortProxyRequested)
            {
                cleanup.PortProxyRequested = false;
                await tracker.EmitAsync(
                    currentStep,
                    AdbForwarderStepState.Skipped,
                    "工作流只发出了审批请求，未取得规则所有权；现有 Windows 规则保持不变。",
                    "info").ConfigureAwait(false);
            }
            else
            {
                await tracker.EmitAsync(currentStep, AdbForwarderStepState.Skipped, "工作流未创建 Windows 共享规则。", "info").ConfigureAwait(false);
            }

            return tracker.Result(true, false, false, "工作流资源已清理。", cleanup.Build());
        }
        catch (OperationCanceledException)
        {
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Canceled, "清理已取消。", "warning").ConfigureAwait(false);
            return tracker.Result(false, true, false, "清理已取消，可以重试。", cleanup.Build());
        }
        catch (WorkflowFailure failure)
        {
            await tracker.EmitAsync(failure.StepId, AdbForwarderStepState.Failed, failure.Message, "error").ConfigureAwait(false);
            return tracker.Result(false, false, false, failure.Message, cleanup.Build());
        }
        catch (Exception ex)
        {
            var message = $"{currentStep} 清理发生未预期错误：{ex.GetType().Name}";
            await tracker.EmitAsync(currentStep, AdbForwarderStepState.Failed, message, "error").ConfigureAwait(false);
            return tracker.Result(false, false, false, message, cleanup.Build());
        }
        finally
        {
            PersistSession(request, cleanup.Build(), approvalRequired: false);
        }
    }

    private async Task<TunnelStopOutcome> StopVerifiedOwnedTunnelsAsync(
        CleanupBuilder cleanup,
        CancellationToken cancellationToken)
    {
        var verified = new List<AdbForwarderOwnedProcess>();
        var unresolved = new List<AdbForwarderOwnedProcess>();
        var staleCount = 0;
        foreach (var expected in cleanup.TunnelOwnership)
        {
            var actual = await _runner.GetProcessIdentityAsync(expected.ProcessId, cancellationToken).ConfigureAwait(false);
            if (actual is not null &&
                actual.StartTimeUtcTicks == expected.StartTimeUtcTicks &&
                expected.ExecutablePath.Length > 0 &&
                string.Equals(actual.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                verified.Add(expected);
                continue;
            }

            if (actual is null && expected.ExecutablePath.Length > 0)
            {
                var ids = await _runner.FindProcessIdsAsync(
                    Path.GetFileNameWithoutExtension(expected.ExecutablePath),
                    cancellationToken).ConfigureAwait(false);
                if (ids.Contains(expected.ProcessId))
                {
                    unresolved.Add(expected);
                    continue;
                }
            }
            staleCount++;
        }

        if (verified.Count == 0)
        {
            return new TunnelStopOutcome(0, staleCount, unresolved);
        }

        var result = await _runner.StopProcessesAsync(
            verified.Select(process => process.ProcessId).ToArray(),
            cancellationToken).ConfigureAwait(false);
        var stoppedIds = result.StoppedProcessIds.ToHashSet();
        var remainingIds = result.RemainingProcessIds.ToHashSet();
        var remaining = verified
            .Where(process => remainingIds.Contains(process.ProcessId) || !stoppedIds.Contains(process.ProcessId))
            .Concat(unresolved)
            .ToArray();
        var stoppedCount = verified.Count(process =>
            stoppedIds.Contains(process.ProcessId) && !remainingIds.Contains(process.ProcessId));
        return new TunnelStopOutcome(stoppedCount, staleCount, remaining);
    }

    private static void ApplyTunnelStopOutcome(CleanupBuilder cleanup, TunnelStopOutcome outcome)
    {
        cleanup.TunnelOwnership = outcome.RemainingOwnership;
        cleanup.TunnelProcessIds = outcome.RemainingOwnership
            .Select(process => process.ProcessId)
            .Distinct()
            .ToArray();
        cleanup.TunnelStarted = outcome.RemainingOwnership.Count > 0;
    }

    private static bool SameRequest(AdbForwardRequest left, AdbForwardRequest right) =>
        string.Equals(left.DeviceSerial, right.DeviceSerial, StringComparison.Ordinal) &&
        left.LocalForwardPort == right.LocalForwardPort &&
        left.SharedPort == right.SharedPort &&
        left.DevicePort == right.DevicePort &&
        left.IncludeSsh == right.IncludeSsh &&
        left.ConnectionMode == right.ConnectionMode &&
        string.Equals(left.RemoteHost, right.RemoteHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.RemoteAdbPath, right.RemoteAdbPath, StringComparison.Ordinal);

    private async Task ConnectEndpointAsync(
        string stepId,
        string endpoint,
        bool wasConnected,
        CleanupBuilder cleanup,
        StepTracker tracker,
        CancellationToken cancellationToken)
    {
        if (wasConnected)
        {
            await tracker.EmitAsync(stepId, AdbForwarderStepState.Skipped, $"{endpoint} 已处于 device 状态。", "info").ConfigureAwait(false);
            return;
        }

        await tracker.EmitAsync(stepId, AdbForwarderStepState.Running, $"正在连接 {endpoint}。", "info").ConfigureAwait(false);
        var attemptedConnect = false;
        for (var attempt = 1; attempt <= MaximumConnectAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await ScanDevicesAsync(cancellationToken).ConfigureAwait(false);
            var state = DeviceState(devices, endpoint);
            if (string.Equals(state, "device", StringComparison.OrdinalIgnoreCase))
            {
                if (attemptedConnect)
                {
                    cleanup.AddConnectedEndpoint(endpoint);
                }
                await tracker.EmitAsync(stepId, AdbForwarderStepState.Succeeded, $"{endpoint} 已连接。", "success").ConfigureAwait(false);
                return;
            }
            if (string.Equals(state, "unauthorized", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowFailure(stepId, $"{endpoint} 状态为 unauthorized，请先确认 ADB 授权。");
            }
            if (string.Equals(state, "offline", StringComparison.OrdinalIgnoreCase))
            {
                await tracker.LogAsync(stepId, "warning", $"{endpoint} 为 offline，先执行 adb disconnect。").ConfigureAwait(false);
                var disconnect = await RunAsync(_adbPath, ["disconnect", endpoint], TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
                await tracker.LogAsync(
                    stepId,
                    disconnect.Success ? "info" : "warning",
                    disconnect.Success ? $"已断开 offline 端点 {endpoint}。" : $"断开 {endpoint} 时返回：{Failure(disconnect)}").ConfigureAwait(false);
            }

            attemptedConnect = true;
            var connect = await RunAsync(_adbPath, ["connect", endpoint], TimeSpan.FromSeconds(7), cancellationToken).ConfigureAwait(false);
            await tracker.LogAsync(
                stepId,
                connect.Success ? "info" : "warning",
                $"第 {attempt}/{MaximumConnectAttempts} 次连接：{FirstNonEmpty(connect.StandardOutput, connect.StandardError, Failure(connect))}").ConfigureAwait(false);
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        var finalDevices = await ScanDevicesAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(DeviceState(finalDevices, endpoint), "device", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowFailure(stepId, $"{endpoint} 在 {MaximumConnectAttempts} 次有界尝试后仍未进入 device 状态。");
        }
        if (attemptedConnect)
        {
            cleanup.AddConnectedEndpoint(endpoint);
        }
        await tracker.EmitAsync(stepId, AdbForwarderStepState.Succeeded, $"{endpoint} 已连接。", "success").ConfigureAwait(false);
    }

    private async Task<bool> PortProxyPresentAsync(AdbForwardRequest request, CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            _netshPath,
            ["interface", "portproxy", "show", "v4tov4"],
            TimeSpan.FromSeconds(6),
            cancellationToken).ConfigureAwait(false);
        return result.Success && HasPortProxyRule(
            result.StandardOutput,
            "0.0.0.0",
            request.SharedPort,
            "127.0.0.1",
            request.LocalForwardPort);
    }

    private Task<AdbForwarderProcessResult> RunAdbAsync(
        string serial,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var fullArguments = new List<string> { "-s", serial };
        fullArguments.AddRange(arguments);
        return RunAsync(_adbPath, fullArguments, timeout, cancellationToken);
    }

    private Task<AdbForwarderProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? standardInput = null)
    {
        return _runner.RunAsync(
            new AdbForwarderProcessRequest(
                fileName,
                arguments,
                timeout,
                standardInput?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')),
            cancellationToken);
    }

    private static string ResolveSystemExecutable(string fileName)
    {
        var systemDirectory = Environment.SystemDirectory;
        if (string.IsNullOrWhiteSpace(systemDirectory))
        {
            systemDirectory = AppContext.BaseDirectory;
        }
        return Path.GetFullPath(Path.Combine(systemDirectory, fileName));
    }

    private static string ResolveSshExecutable(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }
        if (string.Equals(configuredPath, "ssh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuredPath, "ssh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"));
        }
        return ResolveBundledExecutable(configuredPath);
    }

    private static string ResolveBundledExecutable(string configuredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(configuredPath)));
    }

    private static string ResolveTunnelExecutable(string configuredPath, string fallbackSshPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredPath);
        if (Path.IsPathFullyQualified(configuredPath))
        {
            var absolute = Path.GetFullPath(configuredPath);
            if (File.Exists(absolute))
            {
                return absolute;
            }
        }
        return fallbackSshPath;
    }

    private static void ValidateRequest(AdbForwardRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceSerial) || request.DeviceSerial.Any(char.IsControl))
        {
            throw new ArgumentException("请选择有效的 ADB 设备。", nameof(request));
        }
        var isNetworkSerial = IsNetworkSerial(request.DeviceSerial);
        if (request.ConnectionMode == AdbForwardConnectionMode.Wired && isNetworkSerial)
        {
            throw new ArgumentException("有线设备转发需要选择 USB ADB 序列号。", nameof(request));
        }
        if (request.ConnectionMode == AdbForwardConnectionMode.Wireless && !isNetworkSerial)
        {
            throw new ArgumentException("无线设备转发需要选择已连接的 host:port ADB 设备。", nameof(request));
        }
        foreach (var (port, name) in new[]
        {
            (request.LocalForwardPort, "本机直连端口"),
            (request.SharedPort, "Windows 共享端口"),
            (request.DevicePort, "设备 ADB 端口")
        })
        {
            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(request), $"{name}需要在 1 到 65535 之间。");
            }
        }
        if (request.LocalForwardPort == request.SharedPort)
        {
            throw new ArgumentException("本机直连端口与 Windows 共享端口需要不同。", nameof(request));
        }
        if (request.IncludeSsh &&
            (request.RemoteHost.StartsWith("-", StringComparison.Ordinal) || !SafeRemoteHost.IsMatch(request.RemoteHost)))
        {
            throw new ArgumentException("远端主机只能包含字母、数字、点、下划线与连字符。", nameof(request));
        }
        if (request.IncludeSsh && !SafeRemoteAdbPath.IsMatch(request.RemoteAdbPath))
        {
            throw new ArgumentException("远端 ADB 路径需要使用只含安全字符的绝对 POSIX 路径。", nameof(request));
        }
    }

    public static bool IsNetworkSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
        {
            return false;
        }
        var separator = serial.LastIndexOf(':');
        return separator > 0 &&
               separator < serial.Length - 1 &&
               int.TryParse(serial[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
               port is > 0 and <= 65535;
    }

    private static int NetworkPort(string serial)
    {
        var separator = serial.LastIndexOf(':');
        return separator > 0 &&
               int.TryParse(serial[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : 0;
    }

    private static string ConnectionModeLabel(AdbForwardConnectionMode mode) =>
        mode == AdbForwardConnectionMode.Wired ? "USB 有线" : "Wi-Fi 无线";

    private static IReadOnlyList<AdbForwarderDevice> ParseDevices(string output)
    {
        var devices = new List<AdbForwarderDevice>();
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*'))
            {
                continue;
            }
            var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
            {
                continue;
            }
            var attributes = columns.Skip(2)
                .Select(value => value.Split(':', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);
            devices.Add(new AdbForwarderDevice(
                columns[0],
                columns[1],
                attributes.GetValueOrDefault("model", "Unknown device").Replace('_', ' '),
                attributes.GetValueOrDefault("product", ""),
                attributes.GetValueOrDefault("transport_id", "")));
        }
        return devices;
    }

    private static bool HasAdbForward(
        string output,
        string serial,
        int localPort,
        int devicePort)
    {
        var local = $"tcp:{localPort}";
        var remote = $"tcp:{devicePort}";
        return output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Any(columns => columns.Length >= 3 &&
                            string.Equals(columns[0], serial, StringComparison.Ordinal) &&
                            string.Equals(columns[1], local, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(columns[2], remote, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAdbForwardConflict(
        string output,
        string serial,
        int localPort,
        int devicePort)
    {
        var local = $"tcp:{localPort}";
        var remote = $"tcp:{devicePort}";
        return output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Any(columns => columns.Length >= 3 &&
                            string.Equals(columns[1], local, StringComparison.OrdinalIgnoreCase) &&
                            (!string.Equals(columns[0], serial, StringComparison.Ordinal) ||
                             !string.Equals(columns[2], remote, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasPortProxyRule(
        string output,
        string listenAddress,
        int listenPort,
        string connectAddress,
        int connectPort)
    {
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = PortProxyRow.Match(line);
            if (match.Success &&
                string.Equals(match.Groups["listen"].Value, listenAddress, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(match.Groups["listenPort"].Value, out var parsedListenPort) &&
                parsedListenPort == listenPort &&
                string.Equals(match.Groups["connect"].Value, connectAddress, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(match.Groups["connectPort"].Value, out var parsedConnectPort) &&
                parsedConnectPort == connectPort)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasPortProxyConflict(
        string output,
        string listenAddress,
        int listenPort,
        string connectAddress,
        int connectPort)
    {
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = PortProxyRow.Match(line);
            if (!match.Success ||
                !string.Equals(match.Groups["listen"].Value, listenAddress, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(match.Groups["listenPort"].Value, out var parsedListenPort) ||
                parsedListenPort != listenPort)
            {
                continue;
            }

            return !string.Equals(match.Groups["connect"].Value, connectAddress, StringComparison.OrdinalIgnoreCase) ||
                   !int.TryParse(match.Groups["connectPort"].Value, out var parsedConnectPort) ||
                   parsedConnectPort != connectPort;
        }
        return false;
    }

    private static string DeviceState(IReadOnlyList<AdbForwarderDevice> devices, string endpoint)
    {
        return devices.FirstOrDefault(device => string.Equals(device.Id, endpoint, StringComparison.OrdinalIgnoreCase))?.State ?? "missing";
    }

    private static void AddEndpointCheck(
        ICollection<AdbForwarderPreflightCheck> checks,
        string id,
        string title,
        string endpoint,
        string deviceState,
        bool portOpen,
        bool infrastructurePresent)
    {
        var state = string.Equals(deviceState, "device", StringComparison.OrdinalIgnoreCase)
            ? AdbForwarderPreflightState.Passed
            : portOpen && !infrastructurePresent && !string.Equals(deviceState, "offline", StringComparison.OrdinalIgnoreCase)
                ? AdbForwarderPreflightState.Failed
                : AdbForwarderPreflightState.Warning;
        var detail = state switch
        {
            AdbForwarderPreflightState.Passed => $"{endpoint} 已处于 device 状态。",
            AdbForwarderPreflightState.Failed => $"{endpoint} 已被未知服务占用。",
            _ when string.Equals(deviceState, "offline", StringComparison.OrdinalIgnoreCase) => $"{endpoint} 为 offline，运行时会先断开再重连。",
            _ => $"{endpoint} 尚未连接，运行时最多尝试 {MaximumConnectAttempts} 次。"
        };
        AddCheck(checks, id, title, state, detail);
    }

    private static void AddCheck(
        ICollection<AdbForwarderPreflightCheck> checks,
        string id,
        string title,
        AdbForwarderPreflightState state,
        string detail)
    {
        checks.Add(new AdbForwarderPreflightCheck(id, title, state, detail));
    }

    private static void EnsureSuccess(string stepId, AdbForwarderProcessResult result)
    {
        if (!result.Success)
        {
            throw new WorkflowFailure(stepId, Failure(result));
        }
    }

    private static string Failure(AdbForwarderProcessResult result)
    {
        if (result.TimedOut)
        {
            return FirstNonEmpty(result.StandardError, "命令超时。");
        }
        if (!result.Started)
        {
            return FirstNonEmpty(result.StandardError, "命令未能启动。");
        }
        return $"命令退出码 {result.ExitCode}：{FirstNonEmpty(result.StandardError, result.StandardOutput, "没有输出")}";
    }

    private static string FirstLine(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault()?.Trim() ?? "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }

    private static string RedactSensitive(string value, string deviceSerial)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(deviceSerial))
        {
            return value;
        }
        return value.Replace(
            deviceSerial,
            AdbForwarderRedaction.DisplayDeviceId(deviceSerial),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "<空>" : value;

    private static string Endpoint(int port) => $"127.0.0.1:{port}";

    private static AdbForwarderMapping PortProxyMapping(AdbForwardRequest request)
    {
        return new AdbForwarderMapping(
            $"aosp-forward-{request.SharedPort}",
            "AOSP ADB 共享端点",
            true,
            "0.0.0.0",
            request.SharedPort,
            "127.0.0.1",
            request.LocalForwardPort);
    }

    private static (string PropertyValue, bool UseUsb) NormalizeOriginalTcpPort(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? (port.ToString(CultureInfo.InvariantCulture), false)
            : ("-1", true);
    }

    private sealed record TunnelStopOutcome(
        int StoppedCount,
        int StaleCount,
        IReadOnlyList<AdbForwarderOwnedProcess> RemainingOwnership);

    private sealed class WorkflowFailure(string stepId, string message) : Exception(message)
    {
        public string StepId { get; } = stepId;
    }

    private sealed class StepTracker
    {
        private readonly List<AdbForwarderWorkflowStep> _steps;
        private readonly Func<AdbForwarderWorkflowEvent, Task>? _onEvent;
        private readonly string _deviceSerial;

        public StepTracker(
            IEnumerable<AdbForwarderWorkflowStep> steps,
            Func<AdbForwarderWorkflowEvent, Task>? onEvent,
            string deviceSerial)
        {
            _steps = steps.ToList();
            _onEvent = onEvent;
            _deviceSerial = deviceSerial;
        }

        public async Task EmitAsync(
            string stepId,
            AdbForwarderStepState state,
            string message,
            string level)
        {
            message = RedactSensitive(message, _deviceSerial);
            var index = _steps.FindIndex(step => step.Id == stepId);
            var step = index >= 0
                ? _steps[index] with { State = state, Detail = message }
                : new AdbForwarderWorkflowStep(stepId, stepId, state, message);
            if (index >= 0)
            {
                _steps[index] = step;
            }
            else
            {
                _steps.Add(step);
            }
            if (_onEvent is not null)
            {
                await _onEvent(new AdbForwarderWorkflowEvent(DateTimeOffset.Now, step, level, message)).ConfigureAwait(false);
            }
        }

        public async Task LogAsync(string stepId, string level, string message)
        {
            message = RedactSensitive(message, _deviceSerial);
            var step = _steps.First(step => step.Id == stepId);
            if (_onEvent is not null)
            {
                await _onEvent(new AdbForwarderWorkflowEvent(DateTimeOffset.Now, step, level, message)).ConfigureAwait(false);
            }
        }

        public AdbForwardWorkflowResult Result(
            bool success,
            bool canceled,
            bool approvalRequired,
            string message,
            AdbForwardCleanupState cleanup)
        {
            return new AdbForwardWorkflowResult(
                success,
                canceled,
                approvalRequired,
                RedactSensitive(message, _deviceSerial),
                _steps.ToArray(),
                cleanup);
        }
    }

    private sealed class CleanupBuilder
    {
        public CleanupBuilder(AdbForwardCleanupState state)
        {
            DeviceTcpChanged = state.DeviceTcpChanged;
            OriginalDeviceTcpPort = state.OriginalDeviceTcpPort;
            AdbForwardOwned = state.AdbForwardOwned;
            PortProxyRequested = state.PortProxyRequested;
            PortProxyOwned = state.PortProxyOwned;
            TunnelStarted = state.TunnelStarted;
            TunnelProcessIds = state.TunnelProcessIds.ToArray();
            TunnelOwnership = state.TunnelOwnership?.ToArray() ?? [];
            ConnectedEndpoints = state.ConnectedEndpoints.ToArray();
        }

        public bool DeviceTcpChanged { get; set; }
        public string OriginalDeviceTcpPort { get; set; }
        public bool AdbForwardOwned { get; set; }
        public bool PortProxyRequested { get; set; }
        public bool PortProxyOwned { get; set; }
        public bool TunnelStarted { get; set; }
        public IReadOnlyList<int> TunnelProcessIds { get; set; }
        public IReadOnlyList<AdbForwarderOwnedProcess> TunnelOwnership { get; set; }
        public IReadOnlyList<string> ConnectedEndpoints { get; set; }

        public void AddConnectedEndpoint(string endpoint)
        {
            ConnectedEndpoints = ConnectedEndpoints
                .Append(endpoint)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public AdbForwardCleanupState Build()
        {
            return new AdbForwardCleanupState(
                DeviceTcpChanged,
                OriginalDeviceTcpPort,
                AdbForwardOwned,
                PortProxyRequested,
                PortProxyOwned,
                TunnelStarted,
                TunnelProcessIds,
                ConnectedEndpoints,
                TunnelOwnership);
        }
    }
}
