using System.Globalization;
using System.Net.Sockets;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class AdbForwarderConfigurationService
{
    private readonly Dictionary<string, DateTimeOffset> _lastSeenOnline =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<AdbForwarderConfiguredState> LoadAsync(
        IReadOnlyList<AdbForwarderDevice> adbDevices,
        IReadOnlyList<AdbForwarderRule> portProxyRules,
        CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AdbForwarder",
            "devices.ini");
        var parsed = Parse(configPath);
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
            configPath,
            parsed.WakeupPadDeviceId,
            forwardDevices,
            wifiDevices,
            parsed.Error);
    }

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
