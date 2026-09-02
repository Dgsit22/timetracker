using TimeTracker.Shared.Devices;

namespace TimeTracker.Agent.Sync;

/// <summary>
/// Holds the most recently fetched per-device capture policy. Defaults to
/// "capture everything" until the first successful fetch, so a fresh install
/// isn't silently blind before it can reach the server.
/// </summary>
public class DevicePolicyCache
{
    private volatile DeviceCapturePolicyDto _current = new(true, true, true, true, true);

    public DeviceCapturePolicyDto Current => _current;

    public void Update(DeviceCapturePolicyDto policy) => _current = policy;
}
