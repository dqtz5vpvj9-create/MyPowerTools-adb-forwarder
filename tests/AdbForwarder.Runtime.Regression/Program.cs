using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AdbForwarder.Service;
using AdbForwarder.Surface.Services;

if (args is ["--fixture-child", var mode, var pidPath])
{
    // The only actual subprocess is another copy of this harness. It has no
    // network, device, service, shell, credential or application side effects.
    await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString());
    if (mode == "echo")
    {
        var input = await Console.In.ReadToEndAsync();
        Console.WriteLine($"received:{input.Length}");
        Console.Error.WriteLine("fixture stderr");
        return 7;
    }
    if (mode == "read-then-wait") await Console.In.ReadToEndAsync();
    await Task.Delay(TimeSpan.FromSeconds(60)); // safety expiry if parent dies
    return 0;
}

var cases = new (string Name, Func<Task> Run)[]
{
    ("blocked stdin is covered by the process timeout", TimeoutBlockedInput),
    ("canceling blocked stdin kills the owned child", CancelBlockedInput),
    ("pre-canceled request never starts a child", PreCanceledInput),
    ("timeout after stdin EOF still works", TimeoutAfterInput),
    ("canceling an exit wait kills the owned child", CancelExitWait),
    ("stdin, EOF, output, stderr and exit code are preserved", SuccessfulInput),
    ("missing executable retains start-failure result", MissingExecutable),
    ("background maintenance honors independent device intervals", IndependentIntervals),
    ("cached device state survives a non-due refresh", CachedState),
    ("manual refresh is immediate and resets device cadence", ManualRefresh),
    ("edited device configuration is refreshed immediately", ConfigurationChanges),
    ("disabled devices do no work and reenable immediately", DisableAndReenable),
    ("removed devices do not reuse stale cadence when readded", RemoveAndReadd),
    ("changing adb executable invalidates device maintenance cache", AdbPathChanges),
    ("USB recovery attempts use the same per-device interval", RecoveryInterval),
    ("canceled maintenance is not cached as a completed attempt", CanceledMaintenance),
    ("next delay honors earlier device deadlines and global cap", NextDelay),
    ("wall clock changes do not change elapsed device cadence", MonotonicInterval),
    ("concurrent scheduled refreshes serialize and reuse the first result", ConcurrentRefresh),
    ("out-of-range intervals cannot produce a busy loop", InvalidInterval)
};
if (args is ["--list"])
{
    Console.WriteLine("MPT_TEST_CASES_V1");
    foreach (var test in cases) Console.WriteLine(test.Name);
    return 0;
}
var failed = 0;
foreach (var (name, run) in cases)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine($"RESULT: {cases.Length - failed} passed / {failed} failed / {cases.Length} total");
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task TimeoutBlockedInput()
{
    using var child = new ChildFixture();
    var task = new AdbForwarderProcessRunner().RunAsync(child.Request("block", TimeSpan.FromSeconds(1), new string('x', 8 * 1024 * 1024)));
    try
    {
        await child.WaitForStart();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.Started && result.TimedOut && result.ExitCode == -1, "Expected a started, timed-out result.");
        Check(!child.IsAlive(), "Timeout left the owned child alive.");
        Check(result.Duration < TimeSpan.FromSeconds(5), "Stdin blocked beyond timeout cleanup allowance.");
    }
    finally { child.Kill(); await Observe(task); }
}
static async Task CancelBlockedInput()
{
    using var child = new ChildFixture();
    using var cancellation = new CancellationTokenSource();
    var task = new AdbForwarderProcessRunner().RunAsync(child.Request("block", TimeSpan.FromSeconds(30), new string('x', 8 * 1024 * 1024)), cancellation.Token);
    try
    {
        await child.WaitForStart();
        cancellation.Cancel();
        await ExpectCanceled(task);
        Check(!child.IsAlive(), "Cancellation escaped the stdin write without killing the owned child.");
    }
    finally { child.Kill(); await Observe(task); }
}
static async Task PreCanceledInput()
{
    using var child = new ChildFixture();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var task = new AdbForwarderProcessRunner().RunAsync(child.Request("block", TimeSpan.FromSeconds(30), "input"), cancellation.Token);
    try
    {
        await ExpectCanceled(task);
        await Task.Delay(300);
        Check(!File.Exists(child.PidPath), "Pre-canceled request started the child.");
    }
    finally { child.Kill(); await Observe(task); }
}
static async Task TimeoutAfterInput()
{
    using var child = new ChildFixture();
    var task = new AdbForwarderProcessRunner().RunAsync(child.Request("read-then-wait", TimeSpan.FromSeconds(1), "input"));
    try
    {
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(result.TimedOut && !child.IsAlive(), "Expected timed-out child to be killed after reading EOF.");
    }
    finally { child.Kill(); await Observe(task); }
}
static async Task CancelExitWait()
{
    using var child = new ChildFixture();
    using var cancellation = new CancellationTokenSource();
    var task = new AdbForwarderProcessRunner().RunAsync(child.Request("block", TimeSpan.FromSeconds(30)), cancellation.Token);
    try
    {
        await child.WaitForStart();
        cancellation.Cancel();
        await ExpectCanceled(task);
        Check(!child.IsAlive(), "Cancellation left the waiting child alive.");
    }
    finally { child.Kill(); await Observe(task); }
}
static async Task SuccessfulInput()
{
    using var child = new ChildFixture();
    var result = await new AdbForwarderProcessRunner().RunAsync(child.Request("echo", TimeSpan.FromSeconds(10), new string('x', 1024 * 1024)));
    Check(result.Started && !result.TimedOut && result.ExitCode == 7 && !result.Success, "Exit result changed.");
    Check(result.StandardOutput == "received:1048576" && result.StandardError == "fixture stderr", "Input/output/EOF contract changed.");
}
static async Task MissingExecutable()
{
    var result = await new AdbForwarderProcessRunner().RunAsync(new(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "not-an-executable"), [], TimeSpan.FromSeconds(1)));
    Check(!result.Started && result.ExitCode == -1 && !result.TimedOut, "Missing executable result changed.");
}
static async Task ExpectCanceled(Task task)
{
    try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected caller cancellation.");
}
static async Task Observe(Task task)
{
    try { await task.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
}
static async Task IndependentIntervals()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("fast", "fast-host", 10), new Wifi("slow", "slow-host", 30));
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Initial refresh must visit each device.");
    fixture.Clock.Advance(5);
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "A global tick repeated maintenance before a device was due.");
    fixture.Clock.Advance(5);
    await fixture.Scheduled();
    Check(fixture.Probes.SequenceEqual(["fast-host", "slow-host", "fast-host"]), "10-second device was not independently due.");
    fixture.Clock.Advance(20);
    await fixture.Scheduled();
    Check(fixture.Probes.Count(host => host == "fast-host") == 3 && fixture.Probes.Count(host => host == "slow-host") == 2, "Exact interval boundary was not refreshed.");
}
static async Task CachedState()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    var initial = await fixture.Scheduled();
    fixture.Reachable = false;
    fixture.Clock.Advance(5);
    var cached = await fixture.Scheduled();
    Check(cached.WifiDevices.Single() == initial.WifiDevices.Single() && cached.WifiDevices.Single().Status == "reachable", "Non-due refresh discarded the last maintenance result.");
}
static async Task ManualRefresh()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    fixture.Clock.Advance(5);
    await fixture.Worker.RefreshAsync(CancellationToken.None);
    Check(fixture.Probes.Count == 2, "Manual refresh was incorrectly throttled.");
    fixture.Clock.Advance(25);
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Manual refresh did not reset cadence.");
    fixture.Clock.Advance(5);
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 3, "Device was not refreshed 30 seconds after manual refresh.");
}
static async Task ConfigurationChanges()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "old-host", 30));
    await fixture.Scheduled();
    fixture.Configure(new Wifi("one", "new-host", 30));
    await fixture.Scheduled();
    fixture.Configure(new Wifi("one", "new-host", 10));
    await fixture.Scheduled();
    fixture.Configure(new Wifi("one", "new-host", 10, UsbSerial: "USB2", Port: 5556));
    await fixture.Scheduled();
    Check(fixture.Probes.SequenceEqual(["old-host", "new-host", "new-host", "new-host"]), "Changed configuration reused stale maintenance.");
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 4, "Unchanged configuration did not reuse cadence.");
}
static async Task DisableAndReenable()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    fixture.Configure(new Wifi("one", "one-host", 30, Enabled: false));
    var disabled = await fixture.Scheduled();
    Check(disabled.WifiDevices.Single().Status == "disabled" && fixture.Probes.Count == 1, "Disabled device performed maintenance or retained enabled status.");
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Reenabled device did not immediately refresh.");
}
static async Task RemoveAndReadd()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    fixture.Configure();
    Check((await fixture.Scheduled()).WifiDevices.Count == 0, "Removed device remained visible.");
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Readded device reused an obsolete cadence.");
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Readded device did not start a fresh cadence.");
}
static async Task AdbPathChanges()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    fixture.SetAdbPath("another-inert-test-command");
    await fixture.Scheduled();
    Check(fixture.Commands.Any(call => call.Executable == "another-inert-test-command" && call.Arguments[0] == "connect"), "New ADB path did not trigger maintenance.");
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "New ADB path did not reset cadence.");
}
static async Task RecoveryInterval()
{
    using var fixture = new MaintenanceFixture { Reachable = false };
    fixture.Configure(new Wifi("one", "one-host", 30));
    var first = await fixture.Scheduled();
    fixture.Clock.Advance(5);
    var second = await fixture.Scheduled();
    Check(first.WifiDevices.Single().Status == "recovering" && second.WifiDevices.Single().Status == "recovering", "USB recovery state changed.");
    Check(fixture.Commands.Count(call => call.Arguments.Contains("tcpip")) == 1, "Non-due tick repeated the USB recovery command.");
}
static async Task CanceledMaintenance()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    using var cancellation = new CancellationTokenSource();
    fixture.OnProbe = (_, _) => { cancellation.Cancel(); return Task.FromCanceled<bool>(cancellation.Token); };
    await ExpectCanceled(fixture.Worker.RefreshAsync(cancellation.Token, forceWifiRefresh: false));
    fixture.OnProbe = null;
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Canceled maintenance was cached and blocked retry.");
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Successful retry was not cached.");
}
static async Task NextDelay()
{
    using var fixture = new MaintenanceFixture(TimeSpan.FromSeconds(60));
    fixture.Configure(new Wifi("fast", "fast-host", 5), new Wifi("slow", "slow-host", 30));
    await fixture.Scheduled();
    Check(await fixture.Worker.GetRefreshDelayAsync(CancellationToken.None) == TimeSpan.FromSeconds(5), "Global 60-second delay overrides a 5-second device.");
    fixture.Clock.Advance(4);
    Check(await fixture.Worker.GetRefreshDelayAsync(CancellationToken.None) == TimeSpan.FromSeconds(1), "Remaining device deadline is not honored.");
    fixture.Clock.Advance(1);
    Check(await fixture.Worker.GetRefreshDelayAsync(CancellationToken.None) == TimeSpan.Zero, "Due device should not wait another global interval.");
    fixture.Configure();
    await fixture.Scheduled();
    Check(await fixture.Worker.GetRefreshDelayAsync(CancellationToken.None) == TimeSpan.FromSeconds(60), "Empty config changed the global refresh interval.");
}
static async Task MonotonicInterval()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    await fixture.Scheduled();
    fixture.Clock.WallClockOffset = TimeSpan.FromDays(1);
    fixture.Clock.Advance(5);
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 1, "Wall clock jump made maintenance prematurely due.");
    fixture.Clock.WallClockOffset = TimeSpan.FromDays(-1);
    fixture.Clock.Advance(25);
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 2, "Wall clock rollback postponed due maintenance.");
}
static async Task ConcurrentRefresh()
{
    using var fixture = new MaintenanceFixture();
    fixture.Configure(new Wifi("one", "one-host", 30));
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.OnProbe = async (_, token) => { started.TrySetResult(); await release.Task.WaitAsync(token); return true; };
    var first = fixture.Scheduled();
    await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var second = fixture.Scheduled();
    release.SetResult();
    await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
    Check(fixture.Probes.Count == 1, "Concurrent refreshes repeated the same not-yet-due maintenance.");
}
static async Task InvalidInterval()
{
    using var fixture = new MaintenanceFixture(TimeSpan.FromSeconds(60));
    fixture.Configure(new Wifi("one", "one-host", -1));
    await fixture.Scheduled();
    Check(await fixture.Worker.GetRefreshDelayAsync(CancellationToken.None) == TimeSpan.FromSeconds(5), "Invalid interval was not clamped to the supported 5-second minimum.");
    await fixture.Scheduled();
    Check(fixture.Probes.Count == 1, "Invalid interval produced repeated immediate work.");
}

sealed class ChildFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "adb-runtime-child-" + Guid.NewGuid().ToString("N"));
    public ChildFixture() => Directory.CreateDirectory(_directory);
    public string PidPath => Path.Combine(_directory, "owned-child.pid");
    public AdbForwarderProcessRequest Request(string mode, TimeSpan timeout, string? input = null)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
        var arguments = new List<string>();
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(Assembly.GetExecutingAssembly().Location);
        arguments.AddRange(["--fixture-child", mode, PidPath]);
        return new(executable, arguments, timeout, input);
    }
    public async Task WaitForStart()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!TryReadPid(out _)) await Task.Delay(10, deadline.Token);
    }
    private bool TryReadPid(out int pid)
    {
        pid = 0;
        try { return File.Exists(PidPath) && int.TryParse(File.ReadAllText(PidPath), out pid) && pid > 0; }
        catch (IOException) { return false; }
    }
    public bool IsAlive()
    {
        if (!TryReadPid(out var pid)) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    public void Kill()
    {
        if (!TryReadPid(out var pid)) return;
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
        catch (ArgumentException) { }
    }
    public void Dispose() { Kill(); Directory.Delete(_directory, recursive: true); }
}
sealed class ManualClock : TimeProvider
{
    private long _ticks;
    public TimeSpan WallClockOffset { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_ticks) + WallClockOffset;
    public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
}
sealed record Wifi(string Name, string Host, int Interval, bool Enabled = true, string UsbSerial = "USB1", int Port = 5555);
sealed record CommandCall(string Executable, IReadOnlyList<string> Arguments);
sealed class MaintenanceFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adb-runtime-maintenance-" + Guid.NewGuid().ToString("N"));
    public ManualClock Clock { get; } = new();
    public List<string> Probes { get; } = [];
    public List<CommandCall> Commands { get; } = [];
    public bool Reachable { get; set; } = true;
    public Func<string, CancellationToken, Task<bool>>? OnProbe { get; set; }
    public AdbMaintenanceWorker Worker { get; }
    public MaintenanceFixture(TimeSpan? globalInterval = null)
    {
        Directory.CreateDirectory(_root);
        SetAdbPath("inert-test-command");
        Worker = new AdbMaintenanceWorker(_root, Path.Combine(_root, "devices.ini"), Path.Combine(_root, "settings.json"), globalInterval ?? TimeSpan.FromSeconds(5), Clock, RunCommand, Probe);
    }
    public void SetAdbPath(string value) => File.WriteAllText(Path.Combine(_root, "settings.json"), JsonSerializer.Serialize(new { adbPath = value }));
    public void Configure(params Wifi[] devices) => File.WriteAllText(Path.Combine(_root, "devices.ini"), string.Join("\n", devices.Select(device => $"[WifiAdb:{device.Name}]\nenabled={device.Enabled.ToString().ToLowerInvariant()}\nusbSerial={device.UsbSerial}\nhost={device.Host}\nport={device.Port}\nintervalSeconds={device.Interval}\n")));
    public Task<AdbServiceState> Scheduled() => Worker.RefreshAsync(CancellationToken.None, forceWifiRefresh: false);
    private Task<AdbMaintenanceWorker.ProcessResult> RunCommand(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Commands.Add(new(executable, arguments.ToArray()));
        return Task.FromResult(new AdbMaintenanceWorker.ProcessResult(0, arguments[0] == "devices" ? "List of devices attached\nUSB1 device model:Fixture\nUSB2 device model:Fixture\n" : "", ""));
    }
    private Task<bool> Probe(string host, int port, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Probes.Add(host);
        return OnProbe?.Invoke(host, token) ?? Task.FromResult(Reachable);
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
