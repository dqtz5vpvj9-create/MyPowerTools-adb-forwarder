# ADB runtime regressions

Run from the suite root (or use the equivalent relative path in this repository):

```sh
dotnet build tools/adb-forwarder/tests/AdbForwarder.Runtime.Regression/AdbForwarder.Runtime.Regression.csproj -m:1 -p:UseSharedCompilation=false
dotnet run --no-build --project tools/adb-forwarder/tests/AdbForwarder.Runtime.Regression/AdbForwarder.Runtime.Regression.csproj
```

This dependency-free console regression harness compiles the active process runner,
maintenance worker, configuration parser, and portproxy parser directly from their
production source files. It does not substitute copies of the affected algorithms.
Only unrelated Surface model types have minimal compilation stand-ins.

The process tests run an inert copy of the harness with a private temporary PID
file, including a child that never reads stdin. Each owned child has a 60-second
safety expiry and explicit test cleanup. Maintenance tests inject command/socket
operations and a monotonic clock; they never execute ADB, SSH, netsh, device input,
services, network requests, workflow persistence, or credential access.

Coverage includes blocked-write timeout/cancellation, pre-canceled requests,
normal stdin/EOF/output/exit behavior, independent device intervals, manual
refresh, configuration and executable changes, removal, enable/disable, recovery,
cancellation, concurrent refreshes, and clock jumps. Device cadence is measured
from the completed maintenance attempt. The background delay is capped by the
global refresh interval and shortened for an earlier Wi-Fi deadline. Explicit
manual refresh bypasses the cadence and begins a fresh interval.

`dotnet test` alone does not execute this console harness. CI must invoke it
explicitly with `dotnet run` (or run the built assembly). It does not establish
real device, network, desktop UI, or operating-system integration acceptance.

To list case names without running tests or creating fixtures, append `-- --list`
to the `dotnet run --no-build` command. The first line is `MPT_TEST_CASES_V1`.
