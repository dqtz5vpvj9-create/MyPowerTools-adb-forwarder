using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class AdbForwarderToolService
{
    private const string ModuleId = "adb-forwarder";
    private readonly ShellCommandExecutionService _commands = new();
    private readonly AdbForwarderConfigurationService _configuration = new();
    private AdbForwardingWorkflowService _forwarding;
    private readonly bool _managesWorkflow;
    private string _workflowAdbPath = "adb";

    public AdbForwarderToolService(AdbForwardingWorkflowService? forwarding = null)
    {
        _forwarding = forwarding ?? new AdbForwardingWorkflowService();
        _managesWorkflow = forwarding is null;
    }

    public AdbForwardingWorkflowService Forwarding => _forwarding;

    public async Task<AdbForwarderSnapshot> LoadAsync(CancellationToken cancellationToken = default)
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

        var diagnosticsTask = _commands.ExecuteAsync("adb-forwarder.diagnostics.summary", cancellationToken: cancellationToken);
        var planTask = _commands.ExecuteAsync("adb-forwarder.portproxy.plan", cancellationToken: cancellationToken);
        var logsTask = client.TailLogsAsync(ModuleId, cancellationToken);
        var workflowDevicesTask = _forwarding.ScanDevicesAsync(cancellationToken);
        await Task.WhenAll(diagnosticsTask, planTask, logsTask, workflowDevicesTask).ConfigureAwait(false);

        var diagnostics = ParseObject(diagnosticsTask.Result.Response.Summary);
        var planRoot = ParseObject(planTask.Result.Response.Summary);
        var moduleSettings = settingsJson;

        var adbVersion = diagnostics["adbVersion"] as JsonObject;
        var devicesResult = diagnostics["devices"] as JsonObject;
        var portProxyResult = diagnostics["portproxy"] as JsonObject;
        var plan = ParsePlan(planRoot["plan"] as JsonObject);
        var logs = logsTask.Result
            .OrderByDescending(entry => entry.Time?.ToDateTimeOffset() ?? DateTimeOffset.MinValue)
            .Take(40)
            .Select(entry => new AdbForwarderActivity(
                entry.Time?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                entry.Level,
                RedactKnownDeviceIds(entry.Message, workflowDevicesTask.Result)))
            .ToArray();
        var redactedDevices = ParseDevices(ReadString(devicesResult, "stdout"));
        var workflowDevices = ApplyModuleRedaction(workflowDevicesTask.Result, redactedDevices);
        var configuredState = await _configuration.LoadAsync(
            workflowDevicesTask.Result,
            ParseRules(portProxyResult?["rules"] as JsonArray),
            cancellationToken).ConfigureAwait(false);

        return new AdbForwarderSnapshot(
            ReadBool(adbVersion, "available"),
            FirstLine(ReadString(adbVersion, "stdout")),
            redactedDevices,
            ReadBool(portProxyResult, "available"),
            ParseRules(portProxyResult?["rules"] as JsonArray),
            ParseMappings(moduleSettings["mappings"] as JsonArray),
            plan,
            logs,
            settings.Revision)
        {
            ForwardDevices = workflowDevices,
            AdbPath = _workflowAdbPath,
            PersistedWorkflow = _forwarding.LoadPersistedState(),
            ConfiguredState = configuredState
        };
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
                    ReadString(mapping, "name", $"Mapping {index}"),
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
}

public sealed record AdbForwarderDevice(
    string Id,
    string State,
    string Model,
    string Product,
    string TransportId,
    string SafeId = "")
{
    public string DisplayId => AdbForwardingWorkflowService.IsNetworkSerial(Id)
        ? Id
        : SafeId.StartsWith("<adb-device-", StringComparison.Ordinal)
        ? SafeId
        : AdbForwarderRedaction.DisplayDeviceId(Id);
}

internal static class AdbForwarderRedaction
{
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
        return string.IsNullOrWhiteSpace(deviceId)
            ? value
            : value.Replace(deviceId, DisplayDeviceId(deviceId), StringComparison.Ordinal);
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
