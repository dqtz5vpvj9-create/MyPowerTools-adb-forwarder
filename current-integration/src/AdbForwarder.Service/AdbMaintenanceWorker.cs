using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbForwarder.MyPowerTools;

namespace AdbForwarder.Service;

internal sealed class AdbMaintenanceWorker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _toolDataRoot;
    private readonly string _configurationPath;
    private readonly string _settingsPath;
    private readonly string _statePath;
    private readonly string _logPath;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Queue<string> _activity = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeenOnline = new(StringComparer.OrdinalIgnoreCase);
    private AdbServiceState _state;
    private DateTimeOffset _lastWakeup = DateTimeOffset.MinValue;

    public AdbMaintenanceWorker(
        string toolDataRoot,
        string configurationPath,
        string settingsPath,
        TimeSpan interval)
    {
        _toolDataRoot = toolDataRoot;
        _configurationPath = configurationPath;
        _settingsPath = settingsPath;
        _statePath = Path.Combine(toolDataRoot, "service-state.json");
        _logPath = Path.Combine(toolDataRoot, "logs", "service.jsonl");
        _interval = interval;
        _state = AdbServiceState.Starting(Environment.ProcessId, configurationPath);
    }

    public AdbServiceState Snapshot
    {
        get
        {
            lock (_stateGate) return _state;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                await RecordAsync("error", $"refresh failed: {exception.Message}", CancellationToken.None).ConfigureAwait(false);
                SetState(Snapshot with
                {
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Health = "degraded",
                    Summary = exception.Message
                });
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<AdbServiceState> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = AdbServiceConfiguration.Load(_configurationPath);
            var adbPath = ReadAdbPath(_settingsPath);
            var devicesResult = await RunAsync(adbPath, ["devices", "-l"], TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);
            var devices = ParseDevices(devicesResult.StandardOutput);
            var portProxyResult = OperatingSystem.IsWindows()
                ? await RunAsync("netsh", ["interface", "portproxy", "show", "v4tov4"], TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false)
                : ProcessResult.Unavailable("Windows portproxy is unavailable on this platform.");
            var rules = portProxyResult.ExitCode == 0
                ? PortProxyParser.Parse(portProxyResult.StandardOutput)
                : [];

            var forwardDevices = new List<AdbForwardDeviceState>();
            foreach (var entry in configuration.ForwardDevices)
            {
                var deviceState = devices.GetValueOrDefault(entry.DeviceId, "disconnected");
                if (string.Equals(deviceState, "device", StringComparison.OrdinalIgnoreCase))
                {
                    _lastSeenOnline[entry.DeviceId] = DateTimeOffset.Now;
                }
                var internalPort = checked(entry.Port + 15000);
                var proxyReady = rules.Any(rule =>
                    rule.ListenPort == entry.Port &&
                    rule.ConnectPort == internalPort &&
                    string.Equals(rule.ConnectAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase));
                var forwardReady = false;
                var lastAction = "等待设备连接";
                if (string.Equals(deviceState, "device", StringComparison.OrdinalIgnoreCase))
                {
                    var forward = await RunAsync(
                        adbPath,
                        ["-s", entry.DeviceId, "forward", $"tcp:{internalPort}", "tcp:5555"],
                        TimeSpan.FromSeconds(8),
                        cancellationToken).ConfigureAwait(false);
                    forwardReady = forward.ExitCode == 0;
                    lastAction = forwardReady
                        ? $"USB 5555 → 本机 {internalPort} 已保持"
                        : Compact(forward.ErrorText);
                }

                forwardDevices.Add(new AdbForwardDeviceState(
                    entry.DeviceId,
                    entry.Port,
                    internalPort,
                    NormalizeDeviceState(deviceState),
                    _lastSeenOnline.GetValueOrDefault(entry.DeviceId),
                    proxyReady,
                    forwardReady,
                    lastAction));
            }

            var wifiDevices = new List<AdbWifiDeviceState>();
            foreach (var entry in configuration.WifiDevices)
            {
                wifiDevices.Add(await RefreshWifiAsync(adbPath, entry, devices, cancellationToken).ConfigureAwait(false));
            }

            if (!string.IsNullOrWhiteSpace(configuration.WakeupPadDeviceId) &&
                DateTimeOffset.UtcNow - _lastWakeup >= TimeSpan.FromSeconds(30) &&
                string.Equals(devices.GetValueOrDefault(configuration.WakeupPadDeviceId), "device", StringComparison.OrdinalIgnoreCase))
            {
                _lastWakeup = DateTimeOffset.UtcNow;
                _ = await RunAsync(
                    adbPath,
                    ["-s", configuration.WakeupPadDeviceId, "shell", "input", "keyevent", "KEYCODE_ENTER"],
                    TimeSpan.FromSeconds(8),
                    cancellationToken).ConfigureAwait(false);
            }

            var online = forwardDevices.Count(item => item.Status == "online");
            var reachableWifi = wifiDevices.Count(item => item.Status == "reachable");
            var health = devicesResult.ExitCode == 0 && configuration.Error.Length == 0 ? "active" : "degraded";
            var summary = configuration.Error.Length > 0
                ? configuration.Error
                : $"{online}/{forwardDevices.Count} 台有线设备在线，{reachableWifi}/{wifiDevices.Count} 台无线设备可连接";
            var next = new AdbServiceState(
                Environment.ProcessId,
                DateTimeOffset.UtcNow,
                health,
                summary,
                adbPath,
                _configurationPath,
                configuration.WakeupPadDeviceId,
                forwardDevices,
                wifiDevices,
                rules.Select(rule => new AdbPortProxyState(
                    rule.ListenAddress,
                    rule.ListenPort,
                    rule.ConnectAddress,
                    rule.ConnectPort)).ToArray(),
                ActivitySnapshot());
            SetState(next);
            await PersistStateAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<AdbWifiDeviceState> RefreshWifiAsync(
        string adbPath,
        AdbWifiConfiguration entry,
        IReadOnlyDictionary<string, string> devices,
        CancellationToken cancellationToken)
    {
        if (!entry.Enabled)
        {
            return new AdbWifiDeviceState(entry.Name, false, entry.UsbSerial, entry.Host, entry.Port, entry.IntervalSeconds, "disabled", "配置中已停用");
        }

        var reachable = await CanConnectAsync(entry.Host, entry.Port, TimeSpan.FromSeconds(2), cancellationToken)
            .ConfigureAwait(false);
        if (reachable)
        {
            _ = await RunAsync(adbPath, ["connect", $"{entry.Host}:{entry.Port}"], TimeSpan.FromSeconds(5), cancellationToken)
                .ConfigureAwait(false);
            return new AdbWifiDeviceState(entry.Name, true, entry.UsbSerial, entry.Host, entry.Port, entry.IntervalSeconds, "reachable", "网络 ADB 可连接");
        }

        if (!string.Equals(devices.GetValueOrDefault(entry.UsbSerial), "device", StringComparison.OrdinalIgnoreCase))
        {
            return new AdbWifiDeviceState(entry.Name, true, entry.UsbSerial, entry.Host, entry.Port, entry.IntervalSeconds, "waiting-usb", "等待恢复用 USB 设备");
        }

        var tcpip = await RunAsync(
            adbPath,
            ["-s", entry.UsbSerial, "tcpip", entry.Port.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        if (tcpip.ExitCode == 0)
        {
            _ = await RunAsync(adbPath, ["connect", $"{entry.Host}:{entry.Port}"], TimeSpan.FromSeconds(8), cancellationToken)
                .ConfigureAwait(false);
        }
        var detail = tcpip.ExitCode == 0 ? "已通过 USB 请求开启网络 ADB" : Compact(tcpip.ErrorText);
        return new AdbWifiDeviceState(entry.Name, true, entry.UsbSerial, entry.Host, entry.Port, entry.IntervalSeconds, "recovering", detail);
    }

    private async Task RecordAsync(string level, string message, CancellationToken cancellationToken)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}";
        lock (_stateGate)
        {
            _activity.Enqueue(line);
            while (_activity.Count > 100) _activity.Dequeue();
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var json = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, level, message });
            await File.AppendAllTextAsync(_logPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ADB service log write failed: {exception.Message}");
        }
    }

    private void SetState(AdbServiceState state)
    {
        lock (_stateGate) _state = state with { RecentActivity = ActivitySnapshotUnsafe() };
    }

    private IReadOnlyList<string> ActivitySnapshot()
    {
        lock (_stateGate) return ActivitySnapshotUnsafe();
    }

    private IReadOnlyList<string> ActivitySnapshotUnsafe() => _activity.Reverse().Take(50).ToArray();

    private async Task PersistStateAsync(AdbServiceState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_toolDataRoot);
        var temporary = _statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, _statePath, overwrite: true);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return ProcessResult.Unavailable($"无法启动 {executable}");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new ProcessResult(-1, await stdout.ConfigureAwait(false), $"{executable} 执行超时");
            }
            return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return ProcessResult.Unavailable(exception.Message);
        }
    }

    private static async Task<bool> CanConnectAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await client.ConnectAsync(host, port, timeoutSource.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseDevices(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) || line.StartsWith('*')) continue;
            var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2) result[columns[0]] = columns[1];
        }
        return result;
    }

    private static string ReadAdbPath(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath)) return "adb";
            var values = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
            return values?["adbPath"]?.GetValue<string>()?.Trim() is { Length: > 0 } value ? value : "adb";
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            return "adb";
        }
    }

    private static string NormalizeDeviceState(string value) => value.ToLowerInvariant() switch
    {
        "device" => "online",
        "offline" => "offline",
        _ => "disconnected"
    };

    private static string Compact(string value)
    {
        var first = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault()?.Trim() ?? "";
        return first.Length == 0 ? "命令执行失败" : first;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string ErrorText => StandardError.Length > 0 ? StandardError : StandardOutput;
        public static ProcessResult Unavailable(string message) => new(-1, "", message);
    }
}

internal sealed record AdbServiceState(
    int Pid,
    DateTimeOffset UpdatedAt,
    string Health,
    string Summary,
    string AdbPath,
    string ConfigurationPath,
    string WakeupPadDeviceId,
    IReadOnlyList<AdbForwardDeviceState> ForwardDevices,
    IReadOnlyList<AdbWifiDeviceState> WifiDevices,
    IReadOnlyList<AdbPortProxyState> PortProxyRules,
    IReadOnlyList<string> RecentActivity)
{
    public static AdbServiceState Starting(int pid, string configurationPath) =>
        new(pid, DateTimeOffset.UtcNow, "starting", "正在初始化 ADB 转发服务", "adb", configurationPath, "", [], [], [], []);
}

internal sealed record AdbForwardDeviceState(
    string DeviceId,
    int PublicPort,
    int InternalPort,
    string Status,
    DateTimeOffset LastSeenOnline,
    bool PortProxyReady,
    bool AdbForwardReady,
    string LastAction);

internal sealed record AdbWifiDeviceState(
    string Name,
    bool Enabled,
    string UsbSerial,
    string Host,
    int Port,
    int IntervalSeconds,
    string Status,
    string LastAction);

internal sealed record AdbPortProxyState(
    string ListenAddress,
    int ListenPort,
    string ConnectAddress,
    int ConnectPort);

internal sealed record AdbForwardConfiguration(string DeviceId, int Port);
internal sealed record AdbWifiConfiguration(string Name, bool Enabled, string UsbSerial, string Host, int Port, int IntervalSeconds);

internal sealed record AdbServiceConfiguration(
    IReadOnlyList<AdbForwardConfiguration> ForwardDevices,
    string WakeupPadDeviceId,
    IReadOnlyList<AdbWifiConfiguration> WifiDevices,
    string Error)
{
    public static AdbServiceConfiguration Load(string path)
    {
        if (!File.Exists(path)) return new([], "", [], $"设备配置不存在：{path}");
        try
        {
            var forward = new List<AdbForwardConfiguration>();
            var wifi = new List<AdbWifiConfiguration>();
            var wakeup = "";
            var section = "";
            Dictionary<string, string>? wifiValues = null;

            void CompleteWifi()
            {
                if (!section.StartsWith("WifiAdb:", StringComparison.OrdinalIgnoreCase) || wifiValues is null) return;
                wifi.Add(new AdbWifiConfiguration(
                    section["WifiAdb:".Length..].Trim(),
                    ReadBool(wifiValues, "enabled"),
                    Read(wifiValues, "usbSerial"),
                    Read(wifiValues, "host"),
                    ReadInt(wifiValues, "port", 5555),
                    ReadInt(wifiValues, "intervalSeconds", 30)));
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    CompleteWifi();
                    section = line[1..^1].Trim();
                    wifiValues = section.StartsWith("WifiAdb:", StringComparison.OrdinalIgnoreCase)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : null;
                    continue;
                }
                var separator = line.IndexOf('=');
                if (separator <= 0) throw new FormatException($"无效配置行：{raw}");
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (section.Equals("ForwardDevices", StringComparison.OrdinalIgnoreCase))
                {
                    forward.Add(new AdbForwardConfiguration(key, int.Parse(value, CultureInfo.InvariantCulture)));
                }
                else if (section.Equals("WakeupPad", StringComparison.OrdinalIgnoreCase) && key.Equals("deviceId", StringComparison.OrdinalIgnoreCase))
                {
                    wakeup = value;
                }
                else if (wifiValues is not null)
                {
                    wifiValues[key] = value;
                }
            }
            CompleteWifi();
            return new(forward, wakeup, wifi, "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return new([], "", [], exception.Message);
        }
    }

    private static string Read(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : "";

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key) =>
        Read(values, key).Equals("true", StringComparison.OrdinalIgnoreCase) || Read(values, key) is "1" or "yes";

    private static int ReadInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        int.TryParse(Read(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
