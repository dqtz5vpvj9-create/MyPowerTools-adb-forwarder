using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace AdbForwarder.Surface.Services;

/// <summary>
/// Surface-local command executor that wraps HostControlClient per-call (same pattern as the
/// Shell's ShellCommandExecutionService). This lets dotnet-surface tools invoke Runner commands
/// without depending on the Shell assembly.
/// </summary>
public sealed class ShellCommandExecutionService
{
    public async Task<ShellCommandExecutionResult> ExecuteAsync(string commandId, JsonObject? args = null, CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(Guid.NewGuid().ToString("N"), commandId, args, cancellationToken);
    }

    public async Task<ShellCommandExecutionResult> ExecuteAsync(string invocationId, string commandId, JsonObject? args = null, CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = args is null
            ? await client.ExecuteCommandAsync(invocationId, commandId, new JsonObject(), cancellationToken)
            : await client.ExecuteCommandAsync(invocationId, commandId, args, cancellationToken);
        return new ShellCommandExecutionResult(
            $"{response.State}: {response.Summary}",
            response,
            string.Equals(response.State, "permission-required", StringComparison.OrdinalIgnoreCase));
    }

    public async IAsyncEnumerable<ShellCommandExecutionEvent> ExecuteStreamAsync(string invocationId, string commandId, JsonObject? args = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        await foreach (var evt in client.ExecuteCommandStreamAsync(invocationId, commandId, args ?? new JsonObject(), cancellationToken))
        {
            var final = evt.FinalResponse;
            var statusText = $"{evt.State}: {evt.Message}";
            yield return new ShellCommandExecutionEvent(
                statusText,
                evt,
                final is not null && string.Equals(final.State, "permission-required", StringComparison.OrdinalIgnoreCase));
        }
    }
}

public sealed record ShellCommandExecutionResult(
    string StatusText,
    HostProto.CommandExecutionResponse Response,
    bool RequiresPermissionPrompt);

public sealed record ShellCommandExecutionEvent(
    string StatusText,
    HostProto.CommandExecutionEvent Event,
    bool RequiresPermissionPrompt);
