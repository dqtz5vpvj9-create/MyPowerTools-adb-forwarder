using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbForwarder.MyPowerTools;
using AdbForwarder.Service;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;

var pipeName = GetOption(args, "--pipe") ?? "adb-forwarder.core";
var heartbeatFile = GetOption(args, "--heartbeat-file");
var intervalMs = int.TryParse(GetOption(args, "--interval-ms"), out var parsedInterval)
    ? Math.Clamp(parsedInterval, 1000, 60000)
    : 5000;
var toolDataRoot = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT");
if (string.IsNullOrWhiteSpace(toolDataRoot))
{
    toolDataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MyPowerTools", "state", "tools", "adb-forwarder");
}
var configurationPath = Environment.GetEnvironmentVariable("MPT_ADB_CONFIG_PATH");
if (string.IsNullOrWhiteSpace(configurationPath))
{
    configurationPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AdbForwarder", "devices.ini");
}

var cacheRoot = Path.Combine(toolDataRoot, "cache");
var logRoot = Path.Combine(toolDataRoot, "logs");
var settingsPath = Path.Combine(toolDataRoot, "settings.json");
Directory.CreateDirectory(toolDataRoot);
Directory.CreateDirectory(cacheRoot);
Directory.CreateDirectory(logRoot);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();

var module = new AdbForwarderModule();
var initialized = await module.InitializeAsync(
    new ModuleContext(
        HostVersion: "adb-forwarder-service/0.2.0",
        ProtocolVersion: "1.0",
        PackageId: "adb-forwarder",
        ModuleId: "adb-forwarder",
        DataDirectory: toolDataRoot,
        CacheDirectory: cacheRoot,
        LogDirectory: logRoot,
        Platform: OperatingSystem.IsWindows() ? "windows" : "portable",
        GrantedCapabilities: []),
    cancellation.Token);
if (!initialized.Ok)
{
    Console.Error.WriteLine("ADB Forwarder Service failed to initialize its module runtime.");
    return 2;
}

var settingsRevision = new SettingsRevisionStore(Path.Combine(toolDataRoot, "settings.revision"));
await RestoreSettingsAsync(module, settingsPath, settingsRevision.Current, cancellation.Token);
var worker = new AdbMaintenanceWorker(
    toolDataRoot,
    configurationPath,
    settingsPath,
    TimeSpan.FromMilliseconds(intervalMs));
var workerTask = Task.Run(() => worker.RunAsync(cancellation.Token));
var pipeTask = Task.Run(() => ServePipeAsync(
    pipeName,
    module,
    worker,
    settingsPath,
    settingsRevision,
    cancellation.Token));

var pid = Environment.ProcessId;
Console.WriteLine($"ADB Forwarder Service active pid={pid} pipe={pipeName} config={configurationPath}");
try
{
    while (!cancellation.IsCancellationRequested)
    {
        var heartbeat = $"heartbeat pid={pid} ts={DateTimeOffset.UtcNow:O} health={worker.Snapshot.Health}";
        Console.WriteLine(heartbeat);
        if (!string.IsNullOrWhiteSpace(heartbeatFile))
        {
            try
            {
                await AppendHeartbeatAsync(heartbeatFile, heartbeat, cancellation.Token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"ADB Forwarder heartbeat write failed: {exception.Message}");
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
finally
{
    cancellation.Cancel();
    try { await Task.WhenAll(workerTask, pipeTask); } catch (OperationCanceledException) { }
    await module.DisposeAsync(CancellationToken.None);
}

return 0;

static async Task AppendHeartbeatAsync(string path, string line, CancellationToken cancellationToken)
{
    const long maxHeartbeatBytes = 4L * 1024 * 1024;
    var parent = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
    var current = new FileInfo(path);
    if (current.Exists && current.Length >= maxHeartbeatBytes)
    {
        File.Move(path, path + ".1", overwrite: true);
    }

    await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
}

static async Task ServePipeAsync(
    string name,
    AdbForwarderModule module,
    AdbMaintenanceWorker worker,
    string settingsPath,
    SettingsRevisionStore settingsRevision,
    CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await using var server = MptNamedPipePolicy.CreateServer(name);
        try
        {
            await server.WaitForConnectionAsync(cancellationToken);
            while (server.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                using var request = await ReadFramedMessageAsync(server, cancellationToken);
                if (request is null) break;
                var response = await HandleRequestAsync(
                    module,
                    worker,
                    settingsPath,
                    settingsRevision,
                    request.RootElement,
                    cancellationToken);
                await WriteFramedMessageAsync(server, response, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ADB Forwarder Service pipe error: {exception.Message}");
        }
    }
}

static async Task<object> HandleRequestAsync(
    AdbForwarderModule module,
    AdbMaintenanceWorker worker,
    string settingsPath,
    SettingsRevisionStore settingsRevision,
    JsonElement request,
    CancellationToken cancellationToken)
{
    var command = ReadString(request, "command", "serviceState");
    try
    {
        object data = command switch
        {
            "ping" => new { pong = true, pid = Environment.ProcessId },
            "state" or "serviceState" => worker.Snapshot,
            "refresh" => await worker.RefreshAsync(cancellationToken),
            "moduleStatus" => await module.GetStatusAsync(cancellationToken),
            "listCommands" => await module.ListCommandsAsync(cancellationToken),
            "execute" => await ExecuteAsync(module, request, cancellationToken),
            "getSettings" => (await module.GetSettingsAsync(cancellationToken)) with { Revision = settingsRevision.Current },
            "updateSettings" => await UpdateSettingsAsync(
                module, worker, settingsPath, settingsRevision, request, cancellationToken),
            _ => throw new InvalidOperationException($"Unknown command '{command}'.")
        };
        return new { ok = true, command, data };
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"ADB Forwarder Service command '{command}' failed: {exception}");
        return new { ok = false, command, error = exception.Message };
    }
}

static async Task<CommandExecutionResult> ExecuteAsync(
    AdbForwarderModule module,
    JsonElement request,
    CancellationToken cancellationToken)
{
    var invocationId = ReadString(request, "invocationId", Guid.NewGuid().ToString("N"));
    var commandId = ReadString(request, "commandId", "");
    var commandArgs = request.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(argsElement.GetRawText()) as JsonObject ?? new JsonObject()
        : new JsonObject();
    return await module.ExecuteCommandAsync(new CommandRequest(invocationId, commandId, commandArgs), cancellationToken);
}

static async Task<SettingsSnapshotDocument> UpdateSettingsAsync(
    AdbForwarderModule module,
    AdbMaintenanceWorker worker,
    string settingsPath,
    SettingsRevisionStore settingsRevision,
    JsonElement request,
    CancellationToken cancellationToken)
{
    var current = (await module.GetSettingsAsync(cancellationToken)) with { Revision = settingsRevision.Current };
    var expectedRevision = request.TryGetProperty("expectedRevision", out var revisionElement) &&
                           revisionElement.TryGetUInt64(out var revision)
        ? revision
        : current.Revision;
    if (expectedRevision != current.Revision)
    {
        throw new InvalidOperationException($"Settings revision conflict: expected {expectedRevision}, current {current.Revision}.");
    }

    var patch = request.TryGetProperty("patch", out var patchElement) && patchElement.ValueKind == JsonValueKind.Object
        ? JsonNode.Parse(patchElement.GetRawText()) as JsonObject ?? new JsonObject()
        : new JsonObject();
    var merged = (JsonObject)current.Values.DeepClone();
    foreach (var property in patch) merged[property.Key] = property.Value?.DeepClone();
    var validation = await module.ValidateSettingsAsync(
        new SettingsPatch("adb-forwarder", expectedRevision, merged), cancellationToken);
    if (!validation.Ok) throw new InvalidOperationException(string.Join("; ", validation.Messages));

    var nextRevision = checked(current.Revision + 1);
    var updated = await module.ApplySettingsAsync(
        new SettingsSnapshotDocument("adb-forwarder", nextRevision, merged, DateTimeOffset.UtcNow),
        cancellationToken);
    await WriteSettingsAsync(settingsPath, updated.Values, cancellationToken);
    settingsRevision.Commit(nextRevision);
    _ = worker.RefreshAsync(CancellationToken.None);
    return updated with { Revision = nextRevision };
}

static async Task RestoreSettingsAsync(
    AdbForwarderModule module,
    string settingsPath,
    ulong revision,
    CancellationToken cancellationToken)
{
    if (!File.Exists(settingsPath)) return;
    try
    {
        var values = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath, cancellationToken)) as JsonObject;
        if (values is not null)
        {
            await module.ApplySettingsAsync(
                new SettingsSnapshotDocument("adb-forwarder", revision, values, DateTimeOffset.UtcNow),
                cancellationToken);
        }
    }
    catch (Exception exception) when (exception is IOException or JsonException)
    {
        Console.Error.WriteLine($"ADB Forwarder settings restore failed: {exception.Message}");
    }
}

static async Task WriteSettingsAsync(string path, JsonObject values, CancellationToken cancellationToken)
{
    var directory = Path.GetDirectoryName(path);
    if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    var temporary = path + ".tmp";
    await File.WriteAllTextAsync(
        temporary,
        values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        cancellationToken);
    File.Move(temporary, path, overwrite: true);
}

static async Task<JsonDocument?> ReadFramedMessageAsync(Stream stream, CancellationToken cancellationToken)
{
    var header = new byte[4];
    if (!await ReadExactlyOrEofAsync(stream, header, cancellationToken)) return null;
    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
    if (length <= 0 || length > 4 * 1024 * 1024) throw new InvalidDataException($"Invalid message length {length}.");
    var payload = new byte[length];
    await ReadExactlyAsync(stream, payload, cancellationToken);
    return JsonDocument.Parse(payload);
}

static async Task WriteFramedMessageAsync(Stream stream, object message, CancellationToken cancellationToken)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    });
    var header = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
    await stream.WriteAsync(header, cancellationToken);
    await stream.WriteAsync(json, cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
        if (read == 0)
        {
            if (offset == 0) return false;
            throw new EndOfStreamException();
        }
        offset += read;
    }
    return true;
}

static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
{
    if (!await ReadExactlyOrEofAsync(stream, buffer, cancellationToken)) throw new EndOfStreamException();
}

static string ReadString(JsonElement element, string name, string fallback) =>
    element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? fallback
        : fallback;

static string? GetOption(string[] values, string name)
{
    for (var index = 0; index < values.Length - 1; index++)
    {
        if (string.Equals(values[index], name, StringComparison.OrdinalIgnoreCase)) return values[index + 1];
    }
    return null;
}

internal sealed class SettingsRevisionStore
{
    private readonly string _path;

    public SettingsRevisionStore(string path)
    {
        _path = path;
        Current = Read(path);
    }

    public ulong Current { get; private set; }

    public void Commit(ulong revision)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temporary, _path, overwrite: true);
        Current = revision;
    }

    private static ulong Read(string path) =>
        File.Exists(path) && ulong.TryParse(File.ReadAllText(path), out var revision) && revision > 0
            ? revision
            : 1;
}
