using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Interop;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Tracking;

/// <summary>
/// Polls system-wide last-input time and emits an IdlePeriodEventDto for each
/// span the user was idle beyond the configured threshold. A still-ongoing idle
/// span is also flushed in IdleFlushIntervalSeconds chunks so the dashboard's idle
/// total advances while the user is still away, instead of jumping only once
/// activity resumes (which, for a long idle stretch, could be hours later).
/// </summary>
public class IdleTracker : BackgroundService
{
    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DevicePolicyCache _policyCache;
    private readonly AgentOptions _options;
    private readonly ILogger<IdleTracker> _logger;

    private DateTimeOffset? _idleStartedAtUtc;

    public IdleTracker(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        DevicePolicyCache policyCache,
        IOptions<AgentOptions> options,
        ILogger<IdleTracker> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _policyCache = policyCache;
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

        if (isIdle && _idleStartedAtUtc is { } segmentStartedAtUtc)
        {
            if ((now - segmentStartedAtUtc).TotalSeconds >= _options.IdleFlushIntervalSeconds)
            {
                await EmitIdlePeriodAsync(segmentStartedAtUtc, now, cancellationToken);
                _idleStartedAtUtc = now;
            }

            return;
        }

        if (!isIdle && _idleStartedAtUtc is { } startedAtUtc)
        {
            _idleStartedAtUtc = null;
            await EmitIdlePeriodAsync(startedAtUtc, now, cancellationToken);
        }
    }

    private async Task EmitIdlePeriodAsync(DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc, CancellationToken cancellationToken)
    {
        if (!_policyCache.Current.CaptureIdle)
        {
            return;
        }

        var duration = (endedAtUtc - startedAtUtc).TotalSeconds;
        await _store.AddIdlePeriodAsync(
            new IdlePeriodEventDto(
                Guid.NewGuid(),
                _deviceIdentity.DeviceId,
                startedAtUtc,
                endedAtUtc,
                duration,
                _options.IdleThresholdSeconds),
            cancellationToken);
    }
}
