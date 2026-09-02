using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Interop;
using TimeTracker.Agent.Storage;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Tracking;

/// <summary>
/// Polls system-wide last-input time and emits an IdlePeriodEventDto for each
/// span the user was idle beyond the configured threshold.
/// </summary>
public class IdleTracker : BackgroundService
{
    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AgentOptions _options;
    private readonly ILogger<IdleTracker> _logger;

    private DateTimeOffset? _idleStartedAtUtc;

    public IdleTracker(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        IOptions<AgentOptions> options,
        ILogger<IdleTracker> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.IdlePollIntervalSeconds));

        do
        {
            try
            {
                await PollAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll idle state");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PollAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var idleSeconds = NativeMethods.GetIdleMilliseconds() / 1000.0;
        var isIdle = idleSeconds >= _options.IdleThresholdSeconds;

        if (isIdle && _idleStartedAtUtc is null)
        {
            _idleStartedAtUtc = now - TimeSpan.FromSeconds(idleSeconds);
            return;
        }

        if (!isIdle && _idleStartedAtUtc is { } startedAtUtc)
        {
            _idleStartedAtUtc = null;

            var duration = (now - startedAtUtc).TotalSeconds;
            await _store.AddIdlePeriodAsync(
                new IdlePeriodEventDto(
                    Guid.NewGuid(),
                    _deviceIdentity.DeviceId,
                    startedAtUtc,
                    now,
                    duration,
                    _options.IdleThresholdSeconds),
                cancellationToken);
        }
    }
}
