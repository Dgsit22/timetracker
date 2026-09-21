using Microsoft.EntityFrameworkCore;

namespace TimeTracker.Server.Data;

/// <summary>
/// Counts ingest requests refused for a bad API key. In-memory and deliberately not a database
/// write: a misconfigured agent retries every 30 seconds, and the auth path is the wrong place to
/// take a write lock. <see cref="AlertMonitor"/> reads this on its own schedule and decides
/// whether the condition is worth an alert.
/// </summary>
public class IngestRejectionTracker
{
    private long _count;
    private DateTimeOffset? _lastUtc;
    private string? _lastRemoteIp;

    public void Record(string? remoteIp)
    {
        Interlocked.Increment(ref _count);
        _lastUtc = DateTimeOffset.UtcNow;
        _lastRemoteIp = remoteIp;
    }

    public (long Count, DateTimeOffset? LastUtc, string? LastRemoteIp) Read() =>
        (Interlocked.Read(ref _count), _lastUtc, _lastRemoteIp);
}

/// <summary>
/// Raises and clears alerts. Runs on a timer rather than reacting to events, because the
/// conditions worth alerting on are absences - a device that stopped reporting produces nothing to
/// react to, by definition.
/// </summary>
public class AlertMonitor : BackgroundService
{
    /// <summary>
    /// Matches the Devices page. The Agent checks in at least hourly even on an idle machine, so
    /// two hours is a missed heartbeat plus room for one retry - not a single slow sync.
    /// </summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromHours(2);

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>A key-rejection alert clears once the rejections stop for this long.</summary>
    private static readonly TimeSpan RejectionsQuietFor = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestRejectionTracker _rejections;
    private readonly ILogger<AlertMonitor> _logger;

    private long _rejectionsHandled;

    public AlertMonitor(
        IServiceScopeFactory scopeFactory,
        IngestRejectionTracker rejections,
        ILogger<AlertMonitor> logger)
    {
        _scopeFactory = scopeFactory;
        _rejections = rejections;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let migrations and seeding finish before the first pass touches the tables.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(CheckInterval);

        do
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Alerting failing must never take the server with it.
                _logger.LogError(ex, "Alert check failed; will retry on the next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TimeTrackerDbContext>();

        var now = DateTimeOffset.UtcNow;
        var open = await db.Alerts.Where(a => a.ResolvedUtc == null).ToListAsync(cancellationToken);

        await CheckDevicesAsync(db, open, now, cancellationToken);
        CheckRejections(db, open, now);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CheckDevicesAsync(
        TimeTrackerDbContext db, List<Alert> open, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var devices = await db.Devices
            .Select(d => new { d.DeviceId, d.MachineName, d.LastSeenUtc })
            .ToListAsync(cancellationToken);

        foreach (var device in devices)
        {
            var scope = device.DeviceId.ToString();
            var existing = open.FirstOrDefault(a => a.Kind == AlertKind.DeviceSilent && a.Scope == scope);
            var silent = now - device.LastSeenUtc >= SilentAfter;

            if (silent)
            {
                var since = FormatSince(now - device.LastSeenUtc);

                if (existing is null)
                {
                    db.Alerts.Add(new Alert
                    {
                        AlertId = Guid.NewGuid(),
                        Kind = AlertKind.DeviceSilent,
                        Scope = scope,
                        Title = $"{device.MachineName} stopped reporting",
                        Detail = $"No check-in for {since}. The Agent may be stopped, the machine off, or unable to reach this server.",
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                    });

                    _logger.LogWarning(
                        "Device {MachineName} ({DeviceId}) has not reported for {Since}",
                        device.MachineName, device.DeviceId, since);
                }
                else
                {
                    existing.LastSeenUtc = now;
                    existing.Detail = $"No check-in for {since}. The Agent may be stopped, the machine off, or unable to reach this server.";
                }
            }
            else if (existing is not null)
            {
                existing.ResolvedUtc = now;
                _logger.LogInformation("Device {MachineName} is reporting again", device.MachineName);
            }
        }
    }

    private void CheckRejections(TimeTrackerDbContext db, List<Alert> open, DateTimeOffset now)
    {
        var (count, lastUtc, lastRemoteIp) = _rejections.Read();
        var existing = open.FirstOrDefault(a => a.Kind == AlertKind.IngestKeyRejected);
        var newSinceLastCheck = count - _rejectionsHandled;
        _rejectionsHandled = count;

        if (newSinceLastCheck > 0 && lastUtc is { } seen)
        {
            var from = string.IsNullOrEmpty(lastRemoteIp) ? "an agent" : $"an agent at {lastRemoteIp}";
            var detail = $"{newSinceLastCheck} sync attempt(s) refused since the last check, most recently from {from}. "
                + "The Agent's AGENTAPIKEY must match this server's AGENT_API_KEY exactly; until it does, that machine queues its events locally.";

            if (existing is null)
            {
                db.Alerts.Add(new Alert
                {
                    AlertId = Guid.NewGuid(),
                    Kind = AlertKind.IngestKeyRejected,
                    Scope = string.Empty,
                    Title = "An agent is being refused",
                    Detail = detail,
                    FirstSeenUtc = seen,
                    LastSeenUtc = seen,
                });
            }
            else
            {
                existing.LastSeenUtc = seen;
                existing.Detail = detail;
            }
        }
        else if (existing is not null && now - existing.LastSeenUtc >= RejectionsQuietFor)
        {
            existing.ResolvedUtc = now;
        }
    }

    private static string FormatSince(TimeSpan span) => span switch
    {
        { TotalDays: >= 2 } => $"{(int)span.TotalDays} days",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h",
        _ => $"{(int)span.TotalMinutes}m",
    };
}
