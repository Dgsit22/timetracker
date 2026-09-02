using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Shared.Devices;

namespace TimeTracker.Agent.Sync;

/// <summary>
/// Periodically pulls this device's capture policy from the server so an admin
/// dialing back (or restoring) an event type takes effect without reinstalling.
/// </summary>
public class PolicySyncService : BackgroundService
{
    private readonly DevicePolicyCache _cache;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AgentOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PolicySyncService> _logger;

    public PolicySyncService(
        DevicePolicyCache cache,
        DeviceIdentity deviceIdentity,
        IOptions<AgentOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<PolicySyncService> logger)
    {
        _cache = cache;
        _deviceIdentity = deviceIdentity;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PolicyRefreshIntervalSeconds));

        do
        {
            try
            {
                var client = _httpClientFactory.CreateClient("TimeTrackerServer");
                var policy = await client.GetFromJsonAsync<DeviceCapturePolicyDto>(
                    $"/api/devices/{_deviceIdentity.DeviceId}/policy", stoppingToken);

                if (policy is not null)
                {
                    _cache.Update(policy);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh capture policy; keeping previous policy");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
