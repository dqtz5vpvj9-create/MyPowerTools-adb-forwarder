using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class AdbForwarderConfigurationService
{
    private readonly string _configPath;
    private readonly Dictionary<string, DateTimeOffset> _lastSeenOnline =
        new(StringComparer.OrdinalIgnoreCase);

    public AdbForwarderConfigurationService(string? configPath = null)
    {
        _configPath = configPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdbForwarder",
            "devices.ini");
    }

    public string ConfigPath => _configPath;

    public async Task<AdbForwarderConfiguredState> LoadAsync(
        IReadOnlyList<AdbForwarderDevice> adbDevices,
        IReadOnlyList<AdbForwarderRule> portProxyRules,
        CancellationToken cancellationToken)
    {
        var parsed = Parse(_configPath);
        var now = DateTimeOffset.Now;

        var forwardDevices = parsed.ForwardDevices.Select(entry =>
        {
            var adb = adbDevices.FirstOrDefault(device =>
                string.Equals(device.Id, entry.DeviceId, StringComparison.OrdinalIgnoreCase));
            var state = adb?.State?.ToLowerInvariant() switch
            {
                "device" => AdbConfiguredDeviceState.Online,
                "offline" => AdbConfiguredDeviceState.Offline,
                _ => AdbConfiguredDeviceState.Disconnected
            };
            if (state == AdbConfiguredDeviceState.Online)
            {
                _lastSeenOnline[entry.DeviceId] = now;
            }

            var internalPort = entry.Port + 15000;
            var proxyReady = portProxyRules.Any(rule =>
                rule.ListenPort == entry.Port &&
                rule.ConnectPort == internalPort &&
                string.Equals(rule.ConnectAddress, "127.0.0.1", StringComparison.OrdinalIgnoreCase));
            return new AdbConfiguredForwardDevice(
                entry.DeviceId,
                entry.Port,
                internalPort,
                state,
                _lastSeenOnline.GetValueOrDefault(entry.DeviceId),
                proxyReady);
        }).ToArray();

        var wifiTasks = parsed.WifiDevices.Select(async entry =>
        {
            var reachable = entry.Enabled && await CanConnectAsync(
                entry.Host,
                entry.Port,
                TimeSpan.FromMilliseconds(700),
                cancellationToken).ConfigureAwait(false);
            var usb = adbDevices.FirstOrDefault(device =>
                string.Equals(device.Id, entry.UsbSerial, StringComparison.OrdinalIgnoreCase));
            var usbReady = string.Equals(usb?.State, "device", StringComparison.OrdinalIgnoreCase);
            return new AdbConfiguredWifiDevice(
                entry.Name,
                entry.Enabled,
                entry.UsbSerial,
                entry.Host,
                entry.Port,
                entry.IntervalSeconds,
                reachable,
                usbReady);
        });
        var wifiDevices = await Task.WhenAll(wifiTasks).ConfigureAwait(false);

        return new AdbForwarderConfiguredState(
            _configPath,
            parsed.WakeupPadDeviceId,
            forwardDevices,
            wifiDevices,
            parsed.Error);
    }

    public async Task SaveAsync(
        AdbForwarderDeviceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration);

        var directory = Path.GetDirectoryName(_configPath)
            ?? throw new InvalidOperationException("ADB 设备配置路径缺少父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_configPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                Serialize(configuration),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _configPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Validate(AdbForwarderDeviceConfiguration configuration)
    {
        var errors = new List<string>();
        var duplicateForwardDevices = configuration.ForwardDevices
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceId))
            .GroupBy(device => device.DeviceId.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicateForwardPorts = configuration.ForwardDevices
            .Where(device => device.Port is >= 1 and <= 65535)
            .GroupBy(device => device.Port)
            .FirstOrDefault(group => group.Count() > 1);
        var duplicateWifiNames = configuration.WifiDevices
            .Where(device => !string.IsNullOrWhiteSpace(device.Name))
            .GroupBy(device => device.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        for (var index = 0; index < configuration.ForwardDevices.Count; index++)
        {
            var device = configuration.ForwardDevices[index];
            if (!IsSafeIniToken(device.DeviceId, allowEquals: false))
            {
                errors.Add($"有线设备 {index + 1} 的设备 ID 无效。");
            }
            if (device.Port is < 1 or > 65535)
            {
                errors.Add($"有线设备 {index + 1} 的共享端口需要在 1 到 65535 之间。");
            }
        }

        for (var index = 0; index < configuration.WifiDevices.Count; index++)
        {
            var device = configuration.WifiDevices[index];
            if (!IsSafeIniSectionName(device.Name))
            {
                errors.Add($"无线设备 {index + 1} 的名称无效。");
            }
            if (!IsSafeIniToken(device.UsbSerial, allowEquals: true))
            {
                errors.Add($"无线设备 {index + 1} 缺少恢复用 USB 序列号。");
            }
            if (!IsSafeIniToken(device.Host, allowEquals: true))
            {
                errors.Add($"无线设备 {index + 1} 缺少主机地址。");
            }
            if (device.Port is < 1 or > 65535)
            {
                errors.Add($"无线设备 {index + 1} 的端口需要在 1 到 65535 之间。");
            }
            if (device.IntervalSeconds is < 5 or > 86400)
            {
                errors.Add($"无线设备 {index + 1} 的检查间隔需要在 5 到 86400 秒之间。");
            }
        }

        if (!string.IsNullOrWhiteSpace(configuration.WakeupPadDeviceId) &&
            !IsSafeIniToken(configuration.WakeupPadDeviceId, allowEquals: true))
        {
            errors.Add("WakeupPad 设备 ID 包含无效字符。");
        }
        if (duplicateForwardDevices is not null)
        {
            errors.Add($"有线设备 ID 重复：{duplicateForwardDevices.Key}。");
        }
        if (duplicateForwardPorts is not null)
        {
            errors.Add($"有线共享端口重复：{duplicateForwardPorts.Key}。");
        }
        if (duplicateWifiNames is not null)
        {
            errors.Add($"无线设备名称重复：{duplicateWifiNames.Key}。");
        }

        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(configuration));
        }
    }

    private static string Serialize(AdbForwarderDeviceConfiguration configuration)
    {
        var lines = new List<string>
        {
            "; Managed by MyPowerTools ADB Forwarder",
            "[ForwardDevices]"
        };
        lines.AddRange(configuration.ForwardDevices.Select(device =>
            $"{device.DeviceId.Trim()}={device.Port.ToString(CultureInfo.InvariantCulture)}"));
        lines.Add("");
        lines.Add("[WakeupPad]");
        lines.Add($"deviceId={configuration.WakeupPadDeviceId.Trim()}");

        foreach (var device in configuration.WifiDevices)
        {
            lines.Add("");
            lines.Add($"[WifiAdb:{device.Name.Trim()}]");
            lines.Add($"enabled={device.Enabled.ToString().ToLowerInvariant()}");
            lines.Add($"usbSerial={device.UsbSerial.Trim()}");
            lines.Add($"host={device.Host.Trim()}");
            lines.Add($"port={device.Port.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"intervalSeconds={device.IntervalSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static bool IsSafeIniSectionName(string value) =>
        IsSafeIniToken(value, allowEquals: true) && !value.Contains(']') && !value.Contains(':');

    private static bool IsSafeIniToken(string value, bool allowEquals) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('\r') &&
        !value.Contains('\n') &&
        (allowEquals || !value.Contains('='));

    private static ParsedConfiguration Parse(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return new ParsedConfiguration([], "", [], $"配置文件不存在：{configPath}");
        }

        try
        {
            var forward = new List<ParsedForwardDevice>();
            var wifi = new List<ParsedWifiDevice>();
            var wakeupPad = "";
            var section = "";
            Dictionary<string, string>? wifiValues = null;

            void FinishWifiSection()
            {
                if (!section.StartsWith("WifiAdb:", StringComparison.OrdinalIgnoreCase) || wifiValues is null)
                {
                    return;
                }

                var name = section["WifiAdb:".Length..].Trim();
                wifi.Add(new ParsedWifiDevice(
                    name,
                    ReadBool(wifiValues, "enabled"),
                    ReadRequired(wifiValues, "usbSerial"),
                    ReadRequired(wifiValues, "host"),
                    ReadPort(wifiValues, "port"),
                    ReadPositive(wifiValues, "intervalSeconds")));
            }

            foreach (var rawLine in File.ReadAllLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    FinishWifiSection();
                    section = line[1..^1].Trim();
                    wifiValues = section.StartsWith("WifiAdb:", StringComparison.OrdinalIgnoreCase)
                        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        : null;
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    throw new FormatException($"无效配置行：{rawLine}");
                }

                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (section.Equals("ForwardDevices", StringComparison.OrdinalIgnoreCase))
                {
                    forward.Add(new ParsedForwardDevice(key, ParsePort(value, key)));
                }
                else if (section.Equals("WakeupPad", StringComparison.OrdinalIgnoreCase) &&
                         key.Equals("deviceId", StringComparison.OrdinalIgnoreCase))
                {
                    wakeupPad = value;
                }
                else if (wifiValues is not null)
                {
                    wifiValues.Add(key, value);
                }
            }

            FinishWifiSection();
            return new ParsedConfiguration(forward, wakeupPad, wifi, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return new ParsedConfiguration([], "", [], ex.Message);
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
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    private static string ReadRequired(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"Wi-Fi ADB 配置缺少 {key}。");

    private static bool ReadBool(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = ReadRequired(values, key);
        return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value is "1" or "yes";
    }

    private static int ReadPort(IReadOnlyDictionary<string, string> values, string key) =>
        ParsePort(ReadRequired(values, key), key);

    private static int ReadPositive(IReadOnlyDictionary<string, string> values, string key)
    {
        var value = ReadRequired(values, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new FormatException($"{key} 必须是正整数。");
    }

    private static int ParsePort(string value, string key) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new FormatException($"{key} 的端口无效：{value}");

    private sealed record ParsedForwardDevice(string DeviceId, int Port);
    private sealed record ParsedWifiDevice(
        string Name,
        bool Enabled,
        string UsbSerial,
        string Host,
        int Port,
        int IntervalSeconds);
    private sealed record ParsedConfiguration(
        IReadOnlyList<ParsedForwardDevice> ForwardDevices,
        string WakeupPadDeviceId,
        IReadOnlyList<ParsedWifiDevice> WifiDevices,
        string Error);
}

public enum AdbConfiguredDeviceState
{
    Online,
    Offline,
    Disconnected
}

public sealed record AdbConfiguredForwardDevice(
    string DeviceId,
    int Port,
    int InternalPort,
    AdbConfiguredDeviceState State,
    DateTimeOffset LastSeenOnline,
    bool PortProxyReady)
{
    public string StatusLabel => State switch
    {
        AdbConfiguredDeviceState.Online => "在线",
        AdbConfiguredDeviceState.Offline => "离线",
        _ => "已断开"
    };
    public string LastSeenText => LastSeenOnline == default ? "—" : LastSeenOnline.ToString("yyyy-MM-dd HH:mm:ss");
    public string PublicEndpoint => $"0.0.0.0:{Port}";
    public string InternalEndpoint => $"127.0.0.1:{InternalPort}";
    public bool IsOnline => State == AdbConfiguredDeviceState.Online;
    public bool IsOffline => State == AdbConfiguredDeviceState.Offline;
    public bool IsDisconnected => State == AdbConfiguredDeviceState.Disconnected;
}

public sealed record AdbConfiguredWifiDevice(
    string Name,
    bool Enabled,
    string UsbSerial,
    string Host,
    int Port,
    int IntervalSeconds,
    bool Reachable,
    bool UsbRecoveryReady)
{
    public string Endpoint => $"{Host}:{Port}";
    public string StatusLabel => !Enabled ? "已停用" : Reachable ? "可连接" : UsbRecoveryReady ? "需要恢复" : "恢复设备未连接";
    public string LastAction => !Enabled
        ? "配置中已停用"
        : Reachable
            ? $"已连接 {Endpoint}"
            : UsbRecoveryReady
                ? $"可通过 USB {UsbSerial} 执行 adb tcp {Port}"
                : $"等待 USB {UsbSerial} 连接";
    public bool IsHealthy => Enabled && Reachable;
    public bool NeedsRecovery => Enabled && !Reachable && UsbRecoveryReady;
    public bool HasError => Enabled && !Reachable && !UsbRecoveryReady;
}

public sealed record AdbForwarderConfiguredState(
    string ConfigPath,
    string WakeupPadDeviceId,
    IReadOnlyList<AdbConfiguredForwardDevice> ForwardDevices,
    IReadOnlyList<AdbConfiguredWifiDevice> WifiDevices,
    string Error)
{
    public static AdbForwarderConfiguredState Empty { get; } = new("", "", [], [], "");
}

public sealed record AdbForwarderForwardDeviceSetting(string DeviceId, int Port);

public sealed record AdbForwarderWifiDeviceSetting(
    string Name,
    bool Enabled,
    string UsbSerial,
    string Host,
    int Port,
    int IntervalSeconds);

public sealed record AdbForwarderDeviceConfiguration(
    IReadOnlyList<AdbForwarderForwardDeviceSetting> ForwardDevices,
    string WakeupPadDeviceId,
    IReadOnlyList<AdbForwarderWifiDeviceSetting> WifiDevices);
