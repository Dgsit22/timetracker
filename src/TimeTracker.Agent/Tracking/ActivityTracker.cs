using Microsoft.Extensions.Options;
using TimeTracker.Agent.Configuration;
using TimeTracker.Agent.Interop;
using TimeTracker.Agent.Storage;
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
    private readonly AgentOptions _options;
    private readonly ILogger<ActivityTracker> _logger;

    private (string ProcessName, string WindowTitle, DateTimeOffset StartedAtUtc)? _current;

    public ActivityTracker(
        IEventStore store,
        DeviceIdentity deviceIdentity,
        IOptions<AgentOptions> options,
        ILogger<ActivityTracker> logger)
    {
        _store = store;
        _deviceIdentity = deviceIdentity;
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
                Poll(DateTimeOffset.UtcNow);
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
            await CloseSegmentAsync(segment, DateTimeOffset.UtcNow, cancellationToken);
            _current = null;
        }

        await base.StopAsync(cancellationToken);
    }

    private void Poll(DateTimeOffset now)
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
            _ = CloseSegmentAsync(previous, now, CancellationToken.None);
        }

        _current = (processName, windowTitle, now);
    }

    private async Task CloseSegmentAsync(
        (string ProcessName, string WindowTitle, DateTimeOffset StartedAtUtc) segment,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        var duration = (endedAtUtc - segment.StartedAtUtc).TotalSeconds;
        if (duration <= 0)
        {
            return;
        }

        try
        {
            await _store.AddAppUsageEventAsync(
                new AppUsageEventDto(
                    Guid.NewGuid(),
                    _deviceIdentity.DeviceId,
                    segment.ProcessName,
                    segment.WindowTitle,
                    segment.StartedAtUtc,
                    endedAtUtc,
                    duration),
                cancellationToken);

            if (KnownBrowsers.TryGetValue(segment.ProcessName, out var browser))
            {
                await _store.AddUrlVisitAsync(
                    new UrlVisitEventDto(
                        Guid.NewGuid(),
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store activity segment for {ProcessName}", segment.ProcessName);
        }
    }
}
