using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Broker;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Windows;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed record AdbForwarderBrokerLaunch(string ExecutablePath, string Sha256);

public interface IAdbForwarderBrokerLaunchResolver
{
    AdbForwarderBrokerLaunch Resolve();
}

public interface IAdbForwarderPortProxySnapshotProvider
{
    Task<IReadOnlyList<PortProxyRule>> ListAsync(CancellationToken cancellationToken);
}

public interface IAdbForwarderElevatedProcessLauncher
{
    Task<int> RunAsync(
        AdbForwarderBrokerLaunch launch,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class InstalledAdbForwarderBrokerLaunchResolver : IAdbForwarderBrokerLaunchResolver
{
    public AdbForwarderBrokerLaunch Resolve()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("管理员 Broker 仅支持 Windows。");
        }

        var candidates = new List<string>();
        var shellDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        if (string.Equals(shellDirectory.Name, "Shell", StringComparison.OrdinalIgnoreCase) && shellDirectory.Parent is not null)
        {
            candidates.Add(Path.Combine(shellDirectory.Parent.FullName, "Broker", "MyPowerTools.ElevatedBroker.exe"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(
                localAppData,
                "Programs",
                "MyPowerTools",
                "Broker",
                "MyPowerTools.ElevatedBroker.exe"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var executable = Path.GetFullPath(candidate);
            if (WindowsProtectedExecutable.IsTrusted(executable, out _))
            {
                return new AdbForwarderBrokerLaunch(executable, HashFile(executable));
            }
        }

        throw new InvalidOperationException("管理员组件尚未安装。重新运行 MyPowerTools 用户级安装程序后即可应用端口更改。");
    }

    internal static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

public sealed class WindowsAdbForwarderPortProxySnapshotProvider : IAdbForwarderPortProxySnapshotProvider
{
    private readonly INetworkBroker _network = OperatingSystem.IsWindows()
        ? new WindowsPlatformPack().Network
        : new UnsupportedNetworkBroker("Windows portproxy", "Portproxy snapshots require Windows.");

    public Task<IReadOnlyList<PortProxyRule>> ListAsync(CancellationToken cancellationToken) =>
        _network.ListPortProxyRulesAsync(cancellationToken);
}

public sealed class AdbForwarderElevatedProcessLauncher : IAdbForwarderElevatedProcessLauncher
{
    public async Task<int> RunAsync(
        AdbForwarderBrokerLaunch launch,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var reason = "The elevated Broker path is invalid.";
        if (!Path.IsPathFullyQualified(launch.ExecutablePath) ||
            !WindowsProtectedExecutable.IsTrusted(launch.ExecutablePath, out reason))
        {
            throw new InvalidOperationException($"拒绝启动未受信任的管理员 Broker：{reason}");
        }

        using var executableLock = new FileStream(
            launch.ExecutablePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var actualHash = Convert.ToHexString(SHA256.HashData(executableLock)).ToLowerInvariant();
        if (!FixedHexEquals(actualHash, launch.Sha256))
        {
            throw new InvalidOperationException("管理员 Broker 在审批后发生变化，已拒绝启动。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = launch.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(launch.ExecutablePath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("管理员 Broker 进程未能启动。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException("管理员 Broker 在两分钟内没有退出，已终止执行。");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new AdbForwarderUacCancelledException("管理员确认已取消。", ex);
        }
    }

    private static bool FixedHexEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }
}

public sealed class AdbForwarderUacCancelledException : OperationCanceledException
{
    public AdbForwarderUacCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed record AdbForwarderBrokerAvailability(bool IsAvailable, string Message);

public sealed class AdbForwarderElevationService
{
    private const string ModuleId = "adb-forwarder";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IAdbForwarderElevatedProcessLauncher _launcher;
    private readonly IAdbForwarderBrokerLaunchResolver _launchResolver;
    private readonly IAdbForwarderPortProxySnapshotProvider _snapshotProvider;
    private readonly string _requestDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AdbForwarderBrokerApprovalRequest? _pending;

    public AdbForwarderElevationService(
        IAdbForwarderElevatedProcessLauncher? launcher = null,
        string? requestDirectory = null,
        Func<DateTimeOffset>? clock = null,
        IAdbForwarderBrokerLaunchResolver? launchResolver = null,
        IAdbForwarderPortProxySnapshotProvider? snapshotProvider = null)
    {
        _launcher = launcher ?? new AdbForwarderElevatedProcessLauncher();
        _requestDirectory = requestDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _launchResolver = launchResolver ?? new InstalledAdbForwarderBrokerLaunchResolver();
        _snapshotProvider = snapshotProvider ?? new WindowsAdbForwarderPortProxySnapshotProvider();
    }

    public AdbForwarderBrokerAvailability GetAvailability()
    {
        try
        {
            _ = _launchResolver.Resolve();
            return new AdbForwarderBrokerAvailability(true, "管理员 Broker 已安装；特权操作会自动触发 Windows UAC。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            return new AdbForwarderBrokerAvailability(false, ex.Message);
        }
    }

    public async Task<AdbForwarderBrokerRequestResult> RequestOrApproveAsync(
        AdbForwarderBrokerAction action,
        AdbForwarderMapping mapping,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rules = new[] { ToRule(mapping) };
            var launch = _launchResolver.Resolve();
            if (_pending is not null &&
                _pending.ExpiresAt > _clock() &&
                _pending.Action == action &&
                _pending.Rules.SequenceEqual(rules))
            {
                var result = await ExecuteAsync(_pending, launch, cancellationToken).ConfigureAwait(false);
                if (result.Disposition != AdbForwarderBrokerDisposition.ApprovalRequired)
                {
                    _pending = null;
                }
                return result;
            }

            if (_pending is not null)
            {
                TryDelete(_pending.Path);
                _pending = null;
            }

            var current = await _snapshotProvider.ListAsync(cancellationToken).ConfigureAwait(false);
            _pending = CreateRequest(action, rules, current, launch);
            return new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.ApprovalRequired,
                $"已固定一次性管理员审批 {_pending.Token[..8]}。点击“批准并继续”后会显示 Windows UAC。" );
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AdbForwarderBrokerRequestResult> ExecuteImmediatelyAsync(
        AdbForwarderBrokerAction action,
        IReadOnlyList<AdbForwarderMapping> mappings,
        CancellationToken cancellationToken = default)
    {
        var enabled = mappings.Where(mapping => mapping.Enabled).Select(ToRule).ToArray();
        if (enabled.Length == 0)
        {
            return new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.Failed,
                "没有可执行的已启用映射。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var launch = _launchResolver.Resolve();
            var current = await _snapshotProvider.ListAsync(cancellationToken).ConfigureAwait(false);
            var request = CreateRequest(action, enabled, current, launch);
            return await ExecuteAsync(request, launch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private AdbForwarderBrokerApprovalRequest CreateRequest(
        AdbForwarderBrokerAction action,
        IReadOnlyList<PortProxyRule> rules,
        IReadOnlyList<PortProxyRule> current,
        AdbForwarderBrokerLaunch launch)
    {
        Directory.CreateDirectory(_requestDirectory);
        if (WindowsProtectedExecutable.ContainsReparsePoint(_requestDirectory))
        {
            throw new InvalidOperationException("管理员审批目录包含重解析点，写操作已安全禁用。");
        }
        PruneExpiredFiles();

        var now = _clock();
        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var path = Path.Combine(_requestDirectory, $"{token}.json");
        var preconditions = AdbPortProxyPreState.Capture(rules, current);
        var request = new AdbForwarderBrokerApprovalRequest(
            token,
            path,
            now,
            now.AddMinutes(5),
            action,
            rules,
            preconditions,
            AdbPortProxyPreState.Hash(preconditions),
            launch,
            "");
        var content = BuildJson(request).ToJsonString(JsonOptions);
        WriteAtomic(path, content);
        return request with { Digest = Digest(content) };
    }

    private async Task<AdbForwarderBrokerRequestResult> ExecuteAsync(
        AdbForwarderBrokerApprovalRequest request,
        AdbForwarderBrokerLaunch currentLaunch,
        CancellationToken cancellationToken)
    {
        if (request.ExpiresAt <= _clock())
        {
            TryDelete(request.Path);
            return new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.Failed,
                "一次性管理员审批已过期，请重新发起。");
        }
        if (!string.Equals(request.Broker.ExecutablePath, currentLaunch.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            !FixedHexEquals(request.Broker.Sha256, currentLaunch.Sha256))
        {
            TryDelete(request.Path);
            return new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.Failed,
                "管理员 Broker 在审批后发生变化，原审批已作废。");
        }

        var content = await File.ReadAllTextAsync(request.Path, cancellationToken).ConfigureAwait(false);
        if (!FixedHexEquals(Digest(content), request.Digest))
        {
            TryDelete(request.Path);
            return new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.Failed,
                "一次性审批文件在确认后发生变化，已拒绝执行。");
        }

        var arguments = new[]
        {
            "portproxy",
            "execute-request",
            "--request-file",
            request.Path,
            "--token",
            request.Token,
            "--digest",
            request.Digest,
            "--broker-sha256",
            request.Broker.Sha256
        };

        try
        {
            var exitCode = await _launcher.RunAsync(currentLaunch, arguments, cancellationToken).ConfigureAwait(false);
            return exitCode switch
            {
                0 => new AdbForwarderBrokerRequestResult(
                    AdbForwarderBrokerDisposition.Applied,
                    $"管理员 Broker 已执行并记录审批 {request.Token[..8]}。",
                    Changed: true),
                10 => new AdbForwarderBrokerRequestResult(
                    AdbForwarderBrokerDisposition.Applied,
                    $"审批 {request.Token[..8]} 已验证；目标规则由外部提前满足，未取得所有权。",
                    Changed: false),
                _ => new AdbForwarderBrokerRequestResult(
                    AdbForwarderBrokerDisposition.Failed,
                    $"管理员 Broker 拒绝执行或验证失败（退出码 {exitCode}）。")
            };
        }
        catch (AdbForwarderUacCancelledException ex)
        {
            return new AdbForwarderBrokerRequestResult(
                AdbForwarderBrokerDisposition.ApprovalRequired,
                $"{ex.Message} 点击“批准并继续”可重新显示 Windows UAC。");
        }
        catch (TimeoutException ex)
        {
            TryDelete(request.Path);
            return new AdbForwarderBrokerRequestResult(AdbForwarderBrokerDisposition.Failed, ex.Message);
        }
    }

    private void PruneExpiredFiles()
    {
        if (!Directory.Exists(_requestDirectory))
        {
            return;
        }

        var cutoff = _clock().UtcDateTime.AddMinutes(-10);
        foreach (var path in Directory.EnumerateFiles(_requestDirectory, "*.json"))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                    File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static JsonObject BuildJson(AdbForwarderBrokerApprovalRequest request)
    {
        var rules = new JsonArray();
        foreach (var rule in request.Rules)
        {
            rules.Add(new JsonObject
            {
                ["listenAddress"] = rule.ListenAddress,
                ["listenPort"] = rule.ListenPort,
                ["connectAddress"] = rule.ConnectAddress,
                ["connectPort"] = rule.ConnectPort
            });
        }

        var preconditions = new JsonArray();
        foreach (var item in request.Preconditions)
        {
            preconditions.Add(new JsonObject
            {
                ["listenAddress"] = item.ListenAddress,
                ["listenPort"] = item.ListenPort,
                ["exists"] = item.Exists,
                ["connectAddress"] = item.ConnectAddress,
                ["connectPort"] = item.ConnectPort
            });
        }

        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["token"] = request.Token,
            ["moduleId"] = ModuleId,
            ["createdAt"] = request.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["expiresAt"] = request.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
            ["action"] = request.Action.ToString(),
            ["rules"] = rules,
            ["preconditions"] = preconditions,
            ["preStateSha256"] = request.PreStateSha256,
            ["broker"] = new JsonObject
            {
                ["path"] = request.Broker.ExecutablePath,
                ["sha256"] = request.Broker.Sha256
            }
        };
    }

    private static PortProxyRule ToRule(AdbForwarderMapping mapping) => new(
        mapping.ListenAddress,
        mapping.ListenPort,
        mapping.ConnectAddress,
        mapping.ConnectPort);

    private static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static bool FixedHexEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));

    private static void WriteAtomic(string path, string content)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record AdbForwarderBrokerApprovalRequest(
    string Token,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    AdbForwarderBrokerAction Action,
    IReadOnlyList<PortProxyRule> Rules,
    IReadOnlyList<AdbPortProxyPrecondition> Preconditions,
    string PreStateSha256,
    AdbForwarderBrokerLaunch Broker,
    string Digest);
