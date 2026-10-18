// Only unrelated UI model dependencies are stood in for. The process runner,
// maintenance worker, configuration parser and portproxy parser are real source links.
// No workflow/persistence or real device operation is invoked by this harness.
namespace AdbForwarder.Surface.Services;

public sealed record AdbForwarderDevice(string Id, string State, string Model, string Product, string TransportId);
public sealed record AdbForwarderMapping(string Id, string Name, bool Enabled, string ListenAddress,
    int ListenPort, string ConnectAddress, int ConnectPort);
internal static class AdbForwarderRedaction
{
    public static string DisplayDeviceId(string value) => "<test-device>";
}
