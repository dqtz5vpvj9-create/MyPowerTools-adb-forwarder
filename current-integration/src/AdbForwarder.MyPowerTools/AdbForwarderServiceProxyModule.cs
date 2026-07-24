using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace AdbForwarder.MyPowerTools;

/// <summary>
/// Runner-facing contract proxy. Device polling, persistent forwarding maintenance and external
/// process ownership live in AdbForwarder.Service, so Runner and Shell restarts leave forwarding
/// intact.
/// </summary>
public sealed class AdbForwarderServiceProxyModule : IMptModule
{
    private readonly AdbForwarderModule _contractSource = new();
    private AdbForwarderServicePipeClient? _service;

    public string Id => "adb-forwarder";
    public string PackageId => "adb-forwarder";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        var units = context.TryGetCapability<IServiceUnitClientFactory>("service.units", out var factory)
            ? factory.ForTool(Id)
            : new NullServiceUnitClient(Id);
        _service = new AdbForwarderServicePipeClient(units);
        return ValueTask.FromResult(new InitializeResult(
            true,
            context.ProtocolVersion,
            ["status", "commands", "settings", "logs", "dashboardCard", "detailPage"]));
    }

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Service.GetModuleStatusAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return new ModuleStatusSnapshot(
                Id,
                "degraded",
                $"ADB Forwarder Service unavailable: {exception.Message}",
                DateTimeOffset.UtcNow,
                [new HealthCheckSnapshot("service-unit", "ADB Forwarder Service", false, exception.Message)],
                0);
        }
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken) =>
        _contractSource.ListCommandsAsync(cancellationToken);

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken) =>
        await Service.ExecuteAsync(request, cancellationToken);

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        _contractSource.GetSettingsSchemaAsync(cancellationToken);

    public async ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Service.GetSettingsAsync(cancellationToken);
        }
        catch
        {
            return new SettingsSnapshotDocument(Id, 0, new JsonObject(), DateTimeOffset.UtcNow);
        }
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(
        SettingsPatch patch,
        CancellationToken cancellationToken) =>
        _contractSource.ValidateSettingsAsync(patch, cancellationToken);

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(
        SettingsSnapshotDocument snapshot,
        CancellationToken cancellationToken)
    {
        var current = await Service.GetSettingsAsync(cancellationToken);
        return await Service.UpdateSettingsAsync(current.Revision, snapshot.Values, cancellationToken);
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        _contractSource.ListSurfacesAsync(cancellationToken);

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _service = null;
        return ValueTask.CompletedTask;
    }

    private AdbForwarderServicePipeClient Service =>
        _service ?? throw new InvalidOperationException("ADB Forwarder proxy has not been initialized.");
}

internal sealed class AdbForwarderServicePipeClient
{
    private const string UnitId = "adb-forwarder.service";
    private const string DefaultPipeName = "adb-forwarder.core";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IServiceUnitClient _units;

    public AdbForwarderServicePipeClient(IServiceUnitClient units)
    {
        _units = units;
    }

    public async Task<ModuleStatusSnapshot> GetModuleStatusAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new { command = "moduleStatus" }, cancellationToken);
        return JsonSerializer.Deserialize<ModuleStatusSnapshot>(
                   response.RootElement.GetProperty("data").GetRawText(), JsonOptions)
               ?? throw new InvalidDataException("ADB Forwarder Service returned no module status.");
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new
        {
            command = "execute",
            invocationId = request.InvocationId,
            commandId = request.CommandId,
            args = request.Args
        }, cancellationToken);
        return JsonSerializer.Deserialize<CommandExecutionResult>(
                   response.RootElement.GetProperty("data").GetRawText(), JsonOptions)
               ?? throw new InvalidDataException("ADB Forwarder Service returned no command result.");
    }

    public async Task<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new { command = "getSettings" }, cancellationToken);
        return ParseSettings(response.RootElement.GetProperty("data"));
    }

    public async Task<SettingsSnapshotDocument> UpdateSettingsAsync(
        ulong expectedRevision,
        JsonObject patch,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(new { command = "updateSettings", expectedRevision, patch }, cancellationToken);
        return ParseSettings(response.RootElement.GetProperty("data"));
    }

    private async Task<JsonDocument> SendAsync(object request, CancellationToken cancellationToken)
    {
        var unit = await EnsureRunningAsync(cancellationToken);
        var readiness = unit.Readiness;
        var readinessAddress = readiness?.Address;
        var pipeName = string.Equals(readiness?.Kind, "pipe", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(readinessAddress)
            ? readinessAddress
            : DefaultPipeName;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(payload, timeout.Token);
        await pipe.FlushAsync(timeout.Token);

        await ReadExactlyAsync(pipe, header, timeout.Token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException($"ADB Forwarder Service returned invalid message length {length}.");
        }
        var responsePayload = new byte[length];
        await ReadExactlyAsync(pipe, responsePayload, timeout.Token);
        var response = JsonDocument.Parse(responsePayload);
        if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var error = response.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            response.Dispose();
            throw new InvalidOperationException(error ?? "ADB Forwarder Service request failed.");
        }
        return response;
    }

    private async Task<ServiceUnitSnapshot> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var units = await _units.ListAsync(cancellationToken);
        var unit = units.FirstOrDefault(item => string.Equals(item.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        if (unit is null)
        {
            await _units.ReloadAsync(cancellationToken);
            units = await _units.ListAsync(cancellationToken);
            unit = units.FirstOrDefault(item => string.Equals(item.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        }
        if (unit is null) throw new InvalidOperationException($"Service Unit '{UnitId}' is unavailable.");
        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            unit = await _units.StartAsync(UnitId, cancellationToken);
        }
        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            throw new InvalidOperationException(unit.LastError ?? "ADB Forwarder Service failed to start.");
        }
        return unit;
    }

    private static SettingsSnapshotDocument ParseSettings(JsonElement data)
    {
        var values = data.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(valuesElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var revision = data.TryGetProperty("revision", out var revisionElement) && revisionElement.TryGetUInt64(out var value)
            ? value
            : 0;
        var updatedAt = data.TryGetProperty("updatedAt", out var updatedElement) &&
                        updatedElement.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(updatedElement.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new SettingsSnapshotDocument("adb-forwarder", revision, values, updatedAt);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
