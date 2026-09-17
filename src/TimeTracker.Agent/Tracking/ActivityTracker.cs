using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Interop;
using TimeTracker.Agent.Storage;
using TimeTracker.Agent.Sync;
using TimeTracker.Shared.Events;

namespace TimeTracker.Agent.Tracking;

/// <summary>
/// Polls the foreground window and emits AppUsageEventDto segments whenever the
/// active process/window changes. When the outgoing segment belongs to a known
/// browser, also emits a title-only UrlVisitEventDto for the same span, since the
/// window title is the only URL signal available without UI Automation.
/// </summary>
public class ActivityTracker : BackgroundService
{
    private static readonly Dictionary<string, BrowserKind> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = BrowserKind.Chrome,
        ["msedge"] = BrowserKind.Edge,
        ["firefox"] = BrowserKind.Firefox,
    };

    private readonly IEventStore _store;
    private readonly DeviceIdentity _deviceIdentity;
    private readonly DevicePolicyCache _policyCache;
    private readonly AgentOptions _options;
    private readonly ILogger<ActivityTracker> _logger;

    private (string ProcessName, string WindowTitle, DateTimeOffset StartedAtUtc, Guid AppUsageEventId, Guid UrlVisitEventId)? _current;

    public ActivityTracker(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        DevicePolicyCache policyCache,
        IOptions<AgentOptions> options,
        ILogger<ActivityTracker> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
        _policyCache = policyCache;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.ActivePollIntervalSeconds));

        do
        {
            try
            {
                await PollAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to poll foreground window");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_current is { } segment)
        {
            try
            {
                await CloseSegmentAsync(segment, DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (Exception ex)
            {
                // Last write before shutdown - nothing left to retry into, so this one really is
                // lost. Logged rather than thrown so it can't block the host from stopping.
                _logger.LogWarning(ex, "Failed to flush the final activity segment on shutdown");
            }

            _current = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task PollAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hWnd = NativeMethods.GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0)
        {
            return;
        }

        string processName;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            // Process exited between GetForegroundWindow and GetProcessById; skip this poll.
            return;
        }

        var windowTitle = NativeMethods.GetWindowTitle(hWnd);

        if (_current is { } current && current.ProcessName == processName && current.WindowTitle == windowTitle)
        {
            return;
        }

        if (_current is { } previous)
        {
            // Awaited, and deliberately ahead of advancing _current: a throwing write (a locked
            // outbox, most likely) leaves the old segment open, so the next poll retries it with a
            // later end time and the span is merely delayed. The previous fire-and-forget dropped
            // it silently - _current had already moved on, so that stretch of the user's day was
            // gone for good with only an event-log warning behind it.
            await CloseSegmentAsync(previous, now, cancellationToken);
        }

        _current = (processName, windowTitle, now, Guid.NewGuid(), Guid.NewGuid());
    }

    private async Task CloseSegmentAsync(
        (string ProcessName, string WindowTitle, DateTimeOffset StartedAtUtc, Guid AppUsageEventId, Guid UrlVisitEventId) segment,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        var duration = (endedAtUtc - segment.StartedAtUtc).TotalSeconds;
        if (duration <= 0)
        {
            return;
        }

        var policy = _policyCache.Current;

        if (policy.CaptureAppUsage)
        {
            await _store.AddAppUsageEventAsync(
                new AppUsageEventDto(
                    segment.AppUsageEventId,
                    _deviceIdentity.DeviceId,
                    segment.ProcessName,
                    segment.WindowTitle,
                    segment.StartedAtUtc,
                    endedAtUtc,
                    duration),
                cancellationToken);
        }

        if (policy.CaptureUrlVisits && KnownBrowsers.TryGetValue(segment.ProcessName, out var browser))
        {
            await _store.AddUrlVisitAsync(
                new UrlVisitEventDto(
                    segment.UrlVisitEventId,
                    _deviceIdentity.DeviceId,
                    browser,
                    null,
                    segment.WindowTitle,
                    segment.StartedAtUtc,
                    endedAtUtc,
                    duration,
                    UrlCaptureMethod.TitleOnly),
                cancellationToken);
        }
    }
}
