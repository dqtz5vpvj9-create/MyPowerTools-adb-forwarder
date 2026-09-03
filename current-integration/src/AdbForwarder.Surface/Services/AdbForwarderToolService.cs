using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;

namespace AdbForwarder.Surface.Services;

public sealed class AdbForwarderToolService
{
    private const string ModuleId = "adb-forwarder";
    private readonly ShellCommandExecutionService _commands = new();
    private readonly AdbForwarderConfigurationService _configuration = new();
    private readonly AdbForwarderServiceClient? _service;
    private AdbForwardingWorkflowService _forwarding;
    private readonly bool _managesWorkflow;
    private string _workflowAdbPath = "adb";

    public AdbForwarderToolService(
        AdbForwardingWorkflowService? forwarding = null,
        AdbForwarderServiceClient? service = null)
    {
        _forwarding = forwarding ?? new AdbForwardingWorkflowService();
        _managesWorkflow = forwarding is null;
        _service = service;
    }

    public AdbForwardingWorkflowService Forwarding => _forwarding;

    public async Task<AdbForwarderSnapshot> LoadAsync(
        bool refreshService = false,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var settings = await client.GetSettingsAsync(ModuleId, cancellationToken).ConfigureAwait(false);
        var settingsJson = JsonStructMapper.ToJsonObject(settings.Values);
        var adbPath = ReadString(settingsJson, "adbPath", "adb").Trim();
        if (_managesWorkflow && !string.Equals(adbPath, _workflowAdbPath, StringComparison.Ordinal))
        {
            _workflowAdbPath = adbPath.Length == 0 ? "adb" : adbPath;
            _forwarding = new AdbForwardingWorkflowService(adbPath: _workflowAdbPath);
        }

        var serviceStateTask = LoadServiceStateAsync(refreshService, cancellationToken);
        await serviceStateTask.ConfigureAwait(false);

        var moduleSettings = settingsJson;
        var serviceState = serviceStateTask.Result;
        var workflowDevices = serviceState is null
            ? await ScanWorkflowDevicesAsync(cancellationToken).ConfigureAwait(false)
            : BuildServiceDevices(serviceState);
        var currentRules = serviceState?.PortProxyRules
            .Select(rule => new AdbForwarderRule(
                rule.ListenAddress,
                rule.ListenPort,
                rule.ConnectAddress,
                rule.ConnectPort))
            .ToArray() ?? [];
        var configuredState = serviceState is null
            ? await _configuration.LoadAsync(
                workflowDevices,
                currentRules,
                cancellationToken).ConfigureAwait(false)
            : BuildConfiguredState(serviceState);
        var adbAvailable = serviceState is not null &&
                           !string.Equals(serviceState.Health, "degraded", StringComparison.OrdinalIgnoreCase);

        return new AdbForwarderSnapshot(
            adbAvailable,
            serviceState?.AdbPath ?? _workflowAdbPath,
            workflowDevices,
            serviceState is not null,
            currentRules,
            ParseMappings(moduleSettings["mappings"] as JsonArray),
            new AdbForwarderPlan(currentRules, [], [], [], false),
            [],
            settings.Revision)
        {
            ForwardDevices = workflowDevices,
            AdbPath = _workflowAdbPath,
            PersistedWorkflow = _forwarding.LoadPersistedState(),
            ConfiguredState = configuredState,
            ServiceStatus = serviceState is null
                ? null
                : new AdbForwarderServiceStatus(
                    serviceState.Pid,
                    serviceState.Health,
                    serviceState.Summary,
                    serviceState.UpdatedAt)
        };
    }

    private static IReadOnlyList<AdbForwarderDevice> BuildServiceDevices(
        AdbForwarderServiceSnapshot state)
    {
        var devices = state.ForwardDevices.Select(device => new AdbForwarderDevice(
            device.DeviceId,
            device.Status switch
            {
                "online" => "device",
                "offline" => "offline",
                _ => "disconnected"
            },
            "USB ADB 设备",
            "",
            ""));
        var wifi = state.WifiDevices.Select(device => new AdbForwarderDevice(
            $"{device.Host}:{device.Port}",
            string.Equals(device.Status, "reachable", StringComparison.OrdinalIgnoreCase)
                ? "device"
                : "offline",
            device.Name,
            "",
            ""));
        return devices.Concat(wifi).ToArray();
    }

    private async Task<AdbForwarderServiceSnapshot?> LoadServiceStateAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        if (_service is null) return null;
        try
        {
            return await _service.LoadAsync(refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<IReadOnlyList<AdbForwarderDevice>> ScanWorkflowDevicesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _forwarding.ScanDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            return [];
        }
    }

    private static IReadOnlyList<AdbForwarderDevice> MergeServiceAndDiagnosticDevices(
        IReadOnlyList<AdbForwarderServiceDevice> serviceDevices,
        IReadOnlyList<AdbForwarderDevice> diagnosticDevices)
    {
        var devicesById = diagnosticDevices.ToDictionary(device => device.Id, StringComparer.Ordinal);
        var merged = new List<AdbForwarderDevice>(diagnosticDevices);
        foreach (var serviceDevice in serviceDevices)
        {
            if (devicesById.ContainsKey(serviceDevice.DeviceId))
            {
                continue;
            }

            merged.Add(new AdbForwarderDevice(
                serviceDevice.DeviceId,
                serviceDevice.Status switch
                {
                    "online" => "device",
                    "offline" => "offline",
                    _ => "disconnected"
                },
                "USB ADB 设备",
                "",
                ""));
        }
        return merged;
    }

    public static AdbForwarderConfiguredState BuildConfiguredState(AdbForwarderServiceSnapshot state)
    {
        var forward = state.ForwardDevices.Select(device => new AdbConfiguredForwardDevice(
            device.DeviceId,
            device.PublicPort,
            device.InternalPort,
            device.Status switch
            {
                "online" => AdbConfiguredDeviceState.Online,
                "offline" => AdbConfiguredDeviceState.Offline,
                _ => AdbConfiguredDeviceState.Disconnected
            },
            device.LastSeenOnline,
            device.PortProxyReady,
            device.AdbForwardReady,
            device.LastAction)).ToArray();
        var wifi = state.WifiDevices.Select(device => new AdbConfiguredWifiDevice(
            device.Name,
            device.Enabled,
            device.UsbSerial,
            device.Host,
            device.Port,
            device.IntervalSeconds,
            string.Equals(device.Status, "reachable", StringComparison.OrdinalIgnoreCase),
            string.Equals(device.Status, "recovering", StringComparison.OrdinalIgnoreCase),
            device.Status,
            device.LastAction)).ToArray();
        return new AdbForwarderConfiguredState(
            state.ConfigurationPath,
            state.WakeupPadDeviceId,
            forward,
            wifi,
            state.Health == "degraded" ? state.Summary : "");
    }

    public async Task<AdbForwarderPlan> PreviewAsync(
        IReadOnlyList<AdbForwarderMapping> mappings,
        CancellationToken cancellationToken = default)
    {
        var result = await _commands.ExecuteAsync(
            "adb-forwarder.portproxy.plan",
            BuildMappingArgs(mappings),
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(result.Response.State, "succeeded", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result.Response.ErrorMessage.Length > 0
                ? result.Response.ErrorMessage
                : result.Response.Summary);
        }

        var root = ParseObject(result.Response.Summary);
        return ParsePlan(root["plan"] as JsonObject);
    }

    public async Task<ulong> SaveMappingsAsync(
        ulong expectedRevision,
        IReadOnlyList<AdbForwarderMapping> mappings,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var patch = new JsonObject
        {
            ["mappings"] = BuildMappingArray(mappings)
        };
        var updated = await client.UpdateSettingsAsync(
            ModuleId,
            expectedRevision,
            JsonStructMapper.ToStruct(patch),
            cancellationToken).ConfigureAwait(false);
        return updated.Revision;
    }

    public async Task<ulong> SaveEnvironmentAsync(
        ulong expectedRevision,
        AdbForwarderEnvironmentSettings configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var adbPath = configuration.AdbPath.Trim();
        if (adbPath.Length == 0)
        {
            throw new ArgumentException("ADB 可执行文件路径不能为空。", nameof(configuration));
        }

        await _configuration.SaveAsync(configuration.Devices, cancellationToken).ConfigureAwait(false);

        using var client = HostControlClient.ForDefaultEndpoint();
        var patch = new JsonObject
        {
            ["adbPath"] = adbPath
        };
        var updated = await client.UpdateSettingsAsync(
            ModuleId,
            expectedRevision,
            JsonStructMapper.ToStruct(patch),
            cancellationToken).ConfigureAwait(false);

        if (_managesWorkflow && !string.Equals(adbPath, _workflowAdbPath, StringComparison.Ordinal))
        {
            _workflowAdbPath = adbPath;
            _forwarding = new AdbForwardingWorkflowService(adbPath: adbPath);
        }

        return updated.Revision;
    }

    public static JsonObject BuildMappingArgs(IReadOnlyList<AdbForwarderMapping> mappings)
    {
        return new JsonObject
        {
            ["mappings"] = BuildMappingArray(mappings)
        };
    }

    private static JsonArray BuildMappingArray(IReadOnlyList<AdbForwarderMapping> mappings)
    {
        var array = new JsonArray();
        foreach (var mapping in mappings)
        {
            array.Add(new JsonObject
            {
                ["id"] = mapping.Id,
                ["name"] = mapping.Name,
                ["enabled"] = mapping.Enabled,
                ["listenAddress"] = mapping.ListenAddress,
                ["listenPort"] = mapping.ListenPort,
                ["connectAddress"] = mapping.ConnectAddress,
                ["connectPort"] = mapping.ConnectPort
            });
        }

        return array;
    }

    private static AdbForwarderPlan ParsePlan(JsonObject? plan)
    {
        return new AdbForwarderPlan(
            ParseRules(plan?["currentRules"] as JsonArray),
            ParseRules(plan?["toApply"] as JsonArray),
            ParseRules(plan?["toRemove"] as JsonArray),
            ReadStrings(plan?["warnings"] as JsonArray),
            ReadBool(plan, "hasChanges"));
    }

    private static IReadOnlyList<AdbForwarderRule> ParseRules(JsonArray? rules)
    {
        if (rules is null)
        {
            return [];
        }

        return rules.OfType<JsonObject>()
            .Select(rule => new AdbForwarderRule(
                ReadString(rule, "listenAddress"),
                ReadInt(rule, "listenPort"),
                ReadString(rule, "connectAddress"),
                ReadInt(rule, "connectPort")))
            .Where(rule => rule.ListenPort > 0 && rule.ConnectPort > 0)
            .ToArray();
    }

    private static IReadOnlyList<AdbForwarderMapping> ParseMappings(JsonArray? mappings)
    {
        if (mappings is null)
        {
            return [];
        }

        var index = 0;
        return mappings.OfType<JsonObject>()
            .Select(mapping =>
            {
                index++;
                return new AdbForwarderMapping(
                    ReadString(mapping, "id", $"mapping-{index}"),
                    ReadString(mapping, "name", $"映射 {index}"),
                    ReadBool(mapping, "enabled", true),
                    ReadString(mapping, "listenAddress", "0.0.0.0"),
                    ReadInt(mapping, "listenPort"),
                    ReadString(mapping, "connectAddress", "127.0.0.1"),
                    ReadInt(mapping, "connectPort"));
            })
            .ToArray();
    }

    private static IReadOnlyList<AdbForwarderDevice> ParseDevices(string output)
    {
        var rows = new List<AdbForwarderDevice>();
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase))
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
                .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
            rows.Add(new AdbForwarderDevice(
                columns[0],
                columns[1],
                attributes.GetValueOrDefault("model", "Unknown device").Replace('_', ' '),
                attributes.GetValueOrDefault("product", ""),
                attributes.GetValueOrDefault("transport_id", "")));
        }

        return rows;
    }

    private static IReadOnlyList<AdbForwarderDevice> ApplyModuleRedaction(
        IReadOnlyList<AdbForwarderDevice> workflowDevices,
        IReadOnlyList<AdbForwarderDevice> moduleDevices)
    {
        var available = moduleDevices.ToList();
        var result = new List<AdbForwarderDevice>(workflowDevices.Count);
        for (var index = 0; index < workflowDevices.Count; index++)
        {
            var device = workflowDevices[index];
            var matchIndex = available.FindIndex(candidate =>
                string.Equals(candidate.State, device.State, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.Model, device.Model, StringComparison.OrdinalIgnoreCase));
            var safeId = matchIndex >= 0
                ? available[matchIndex].Id
                : $"<adb-device-{index + 1}>";
            if (matchIndex >= 0)
            {
                available.RemoveAt(matchIndex);
            }
            result.Add(device with { SafeId = safeId });
        }
        foreach (var device in available)
        {
            result.Add(device with
            {
                SafeId = AdbForwardingWorkflowService.IsNetworkSerial(device.Id)
                    ? device.Id
                    : device.DisplayId
            });
        }
        return result;
    }

    private static string RedactKnownDeviceIds(
        string value,
        IReadOnlyList<AdbForwarderDevice> devices)
    {
        foreach (var device in devices)
        {
            value = AdbForwarderRedaction.DisplayText(value, device.Id);
        }
        return value;
    }

    private static JsonObject ParseObject(string value)
    {
        try
        {
            return JsonNode.Parse(value) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static IReadOnlyList<string> ReadStrings(JsonArray? values)
    {
        return values?.Select(value => value?.GetValue<string>() ?? "")
            .Where(value => value.Length > 0)
            .ToArray() ?? [];
    }

    private static string FirstLine(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault()?.Trim() ?? "";
    }

    private static string ReadString(JsonObject? source, string key, string fallback = "")
    {
        try
        {
            return source?[key]?.GetValue<string>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static int ReadInt(JsonObject? source, string key)
    {
        try
        {
            return source?[key]?.GetValue<int>() ?? 0;
        }
        catch (InvalidOperationException)
        {
            return int.TryParse(ReadString(source, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
    }

    private static bool ReadBool(JsonObject? source, string key, bool fallback = false)
    {
        try
        {
            return source?[key]?.GetValue<bool>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}

public sealed record AdbForwarderSnapshot(
    bool AdbAvailable,
    string AdbVersion,
    IReadOnlyList<AdbForwarderDevice> Devices,
    bool PortProxyAvailable,
    IReadOnlyList<AdbForwarderRule> CurrentRules,
    IReadOnlyList<AdbForwarderMapping> ConfiguredMappings,
    AdbForwarderPlan Plan,
    IReadOnlyList<AdbForwarderActivity> Activity,
    ulong SettingsRevision)
{
    public IReadOnlyList<AdbForwarderDevice> ForwardDevices { get; init; } = [];
    public string AdbPath { get; init; } = "adb";
    public AdbForwarderPersistedWorkflowState? PersistedWorkflow { get; init; }
    public AdbForwarderConfiguredState ConfiguredState { get; init; } = AdbForwarderConfiguredState.Empty;
    public bool BrokerAvailable { get; init; } = true;
    public string BrokerAvailabilityMessage { get; init; } = "管理员 Broker 已就绪。";
    public AdbForwarderServiceStatus? ServiceStatus { get; init; }
}

public sealed record AdbForwarderServiceStatus(
    int Pid,
    string Health,
    string Summary,
    DateTimeOffset UpdatedAt)
{
    public bool IsActive => string.Equals(Health, "active", StringComparison.OrdinalIgnoreCase);
}

public sealed record AdbForwarderEnvironmentSettings(
    string AdbPath,
    AdbForwarderDeviceConfiguration Devices);

public sealed record AdbForwarderDevice(
    string Id,
    string State,
    string Model,
    string Product,
    string TransportId,
    string SafeId = "")
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => string.IsNullOrWhiteSpace(Model) ||
                                 string.Equals(Model, "Unknown device", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(Model, "Configured USB device", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(Model, "USB ADB 设备", StringComparison.OrdinalIgnoreCase)
        ? AdbForwardingWorkflowService.IsNetworkSerial(Id) ? "无线 ADB 设备" : "USB ADB 设备"
        : Model;
    public string ConnectionLabel => AdbForwardingWorkflowService.IsNetworkSerial(Id) ? "无线 ADB" : "USB";
    public string IdentityLabel => $"ADB serial · {Id}";
    public string StateLabel => State.ToLowerInvariant() switch
    {
        "device" => "可用",
        "unauthorized" => "等待授权",
        "offline" => "离线",
        "disconnected" => "未连接",
        _ => State
    };
    public string DisplayId => Id;
}

internal static class AdbForwarderRedaction
{
    private static readonly System.Text.RegularExpressions.Regex PublicBridge = new(
        @"0\.0\.0\.0:(?<port>\d+)\s*(?:→|->)\s*127\.0\.0\.1:\d+",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex LoopbackBridge = new(
        @"127\.0\.0\.1:\d+",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string DisplayDeviceId(string value)
    {
        if (value.StartsWith("<adb-device-", StringComparison.Ordinal) ||
            value.StartsWith("127.0.0.1:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return $"<adb-device-{Convert.ToHexString(digest)[..8].ToLowerInvariant()}>";
    }

    public static string DisplayText(string value, string? deviceId)
    {
        var safe = string.IsNullOrWhiteSpace(deviceId)
            ? value
            : value.Replace(deviceId, DisplayDeviceId(deviceId), StringComparison.Ordinal);
        safe = PublicBridge.Replace(safe, match => $"共享端口 {match.Groups["port"].Value}");
        return LoopbackBridge.Replace(safe, "内部连接");
    }
}

public sealed record AdbForwarderRule(string ListenAddress, int ListenPort, string ConnectAddress, int ConnectPort)
{
    public string ListenEndpoint => $"{ListenAddress}:{ListenPort.ToString(CultureInfo.InvariantCulture)}";
    public string ConnectEndpoint => $"{ConnectAddress}:{ConnectPort.ToString(CultureInfo.InvariantCulture)}";
}

public sealed record AdbForwarderMapping(
    string Id,
    string Name,
    bool Enabled,
    string ListenAddress,
    int ListenPort,
    string ConnectAddress,
    int ConnectPort);

public sealed record AdbForwarderPlan(
    IReadOnlyList<AdbForwarderRule> CurrentRules,
    IReadOnlyList<AdbForwarderRule> ToApply,
    IReadOnlyList<AdbForwarderRule> ToRemove,
    IReadOnlyList<string> Warnings,
    bool HasChanges);

public sealed record AdbForwarderActivity(DateTimeOffset Time, string Level, string Message)
{
    public string TimeText => Time == DateTimeOffset.MinValue ? "--:--" : Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
}
