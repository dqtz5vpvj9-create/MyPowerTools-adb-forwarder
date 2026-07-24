using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace AdbForwarder.Surface.Services;

public sealed class AdbForwarderServiceClient
{
    private const string UnitId = "adb-forwarder.service";
    private const string DefaultPipeName = "adb-forwarder.core";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IServiceUnitClient _serviceUnits;

    public AdbForwarderServiceClient(IServiceUnitClient serviceUnits)
    {
        _serviceUnits = serviceUnits;
    }

    public async Task<AdbForwarderServiceSnapshot> LoadAsync(
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(new { command = refresh ? "refresh" : "serviceState" }, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<AdbForwarderServiceSnapshot>(
                   response.RootElement.GetProperty("data").GetRawText(), JsonOptions)
               ?? throw new InvalidDataException("ADB Forwarder Service returned no state.");
    }

    private async Task<JsonDocument> SendAsync(object request, CancellationToken cancellationToken)
    {
        var unit = await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        var pipeName = string.Equals(unit.Readiness?.Kind, "pipe", StringComparison.OrdinalIgnoreCase) &&
                       !string.IsNullOrWhiteSpace(unit.Readiness?.Address)
            ? unit.Readiness!.Address
            : DefaultPipeName;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await pipe.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);

        await ReadExactlyAsync(pipe, header, timeout.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException($"ADB Forwarder Service returned invalid message length {length}.");
        }
        var responsePayload = new byte[length];
        await ReadExactlyAsync(pipe, responsePayload, timeout.Token).ConfigureAwait(false);
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
        var units = await _serviceUnits.ListAsync(cancellationToken).ConfigureAwait(false);
        var unit = units.FirstOrDefault(item => string.Equals(item.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        if (unit is null)
        {
            await _serviceUnits.ReloadAsync(cancellationToken).ConfigureAwait(false);
            units = await _serviceUnits.ListAsync(cancellationToken).ConfigureAwait(false);
            unit = units.FirstOrDefault(item => string.Equals(item.Id, UnitId, StringComparison.OrdinalIgnoreCase));
        }
        if (unit is null) throw new InvalidOperationException($"Service Unit '{UnitId}' 尚未部署。");
        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            unit = await _serviceUnits.StartAsync(UnitId, cancellationToken).ConfigureAwait(false);
        }
        if (unit.State is not ServiceUnitState.Active and not ServiceUnitState.Degraded)
        {
            throw new InvalidOperationException(unit.LastError ?? "ADB Forwarder Service 启动失败。");
        }
        return unit;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}

public sealed record AdbForwarderServiceSnapshot(
    int Pid,
    DateTimeOffset UpdatedAt,
    string Health,
    string Summary,
    string AdbPath,
    string ConfigurationPath,
    IReadOnlyList<AdbForwarderServiceDevice> ForwardDevices,
    IReadOnlyList<AdbForwarderServiceWifiDevice> WifiDevices,
    IReadOnlyList<AdbForwarderServicePortProxy> PortProxyRules,
    IReadOnlyList<string> RecentActivity);

public sealed record AdbForwarderServiceDevice(
    string DeviceId,
    int PublicPort,
    int InternalPort,
    string Status,
    bool PortProxyReady,
    bool AdbForwardReady,
    string LastAction);

public sealed record AdbForwarderServiceWifiDevice(
    string Name,
    bool Enabled,
    string UsbSerial,
    string Host,
    int Port,
    string Status,
    string LastAction);

public sealed record AdbForwarderServicePortProxy(
    string ListenAddress,
    int ListenPort,
    string ConnectAddress,
    int ConnectPort);
