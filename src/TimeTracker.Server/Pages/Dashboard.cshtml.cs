using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

/// <summary>
/// Org-wide (all devices combined) at-a-glance view - unlike Activity's per-device summary
/// cards, this rolls everything into one KPI/chart set for the selected period.
/// </summary>
[Authorize]
public class DashboardModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public DashboardModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<string> KnownUsers { get; private set; } = new();

    public List<Device> KnownDevices { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? UserName { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? DeviceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ToDate { get; set; }

    public double TotalActiveSeconds { get; private set; }

    public double TotalIdleSeconds { get; private set; }

    public int TotalEvents { get; private set; }

    public int ActiveDeviceCount { get; private set; }

    public int TotalDeviceCount { get; private set; }

    public double? ActiveChangePercent { get; private set; }

    public double? IdleChangePercent { get; private set; }

    public double? EventsChangePercent { get; private set; }

    public List<CategorySlice> TopApps { get; private set; } = new();

    public List<CategorySlice> TopSites { get; private set; } = new();

    public List<AgentStatus> AgentStatuses { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // First load (no date filter at all yet): default to today, matching the reference
        // dashboard's default "Today" quick filter.
        if (FromDate is null && ToDate is null)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            FromDate = today;
            ToDate = today;
        }

        var excludedUserNames = await ActivityAggregation.GetExcludedUserNamesAsync(_db, cancellationToken);

        KnownUsers = (await _db.AppUsageEvents.Select(e => e.UserName)
            .Union(_db.IdlePeriods.Select(e => e.UserName))
            .Union(_db.UrlVisits.Select(e => e.UserName))
            .Union(_db.SessionBreaks.Select(e => e.UserName))
            .Union(_db.Screenshots.Select(e => e.UserName))
            .Distinct()
            .OrderBy(u => u)
            .ToListAsync(cancellationToken))
            .Where(u => !excludedUserNames.Contains(u))
            .ToList();

        KnownDevices = await _db.Devices.OrderBy(d => d.MachineName).ToListAsync(cancellationToken);
        TotalDeviceCount = KnownDevices.Count;

        var fromUtc = FromDate is { } fd ? new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
        var toUtcExclusive = ToDate is { } td ? new DateTimeOffset(td.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1) : (DateTimeOffset?)null;

        var current = await LoadPeriodAsync(excludedUserNames, fromUtc, toUtcExclusive, cancellationToken);
        TotalActiveSeconds = current.ActiveSeconds;
        TotalIdleSeconds = current.IdleSeconds;
        TotalEvents = current.EventCount;
        ActiveDeviceCount = current.ActiveDeviceIds.Count;
        TopApps = ActivityAggregation.BuildTopSlices(current.AppTotals);
        TopSites = ActivityAggregation.BuildTopSlices(current.SiteTotals);

        AgentStatuses = await LoadAgentStatusesAsync(cancellationToken);

        // "vs previous period": same span length, immediately preceding the current range -
        // only meaningful when both bounds are actually set (a real, bounded period).
        if (fromUtc is { } from && toUtcExclusive is { } to)
        {
            var span = to - from;
            var previous = await LoadPeriodAsync(excludedUserNames, from - span, from, cancellationToken);
            ActiveChangePercent = PercentChange(previous.ActiveSeconds, current.ActiveSeconds);
            IdleChangePercent = PercentChange(previous.IdleSeconds, current.IdleSeconds);
            EventsChangePercent = PercentChange(previous.EventCount, current.EventCount);
        }
    }

    /// <summary>
    /// Deliberately ignores the page's date filter: this answers "is each agent reporting right
    /// now", which is a question about the present, not about whichever period is being reviewed.
    /// Silence is the failure mode that matters here - an agent that stops syncing looks identical
    /// to a quiet machine on every other view, so the last-seen age is surfaced explicitly rather
    /// than left to be inferred from missing rows.
    /// </summary>
    private async Task<List<AgentStatus>> LoadAgentStatusesAsync(CancellationToken cancellationToken)
    {
        var devices = await _db.Devices.ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var since = now.AddHours(-1);

        // One grouped query per type rather than per device, so this stays flat as the fleet grows.
        var appCounts = await CountByDeviceAsync(_db.AppUsageEvents.Where(e => e.StartedAtUtc >= since), cancellationToken);
        var idleCounts = await CountByDeviceAsync(_db.IdlePeriods.Where(e => e.StartedAtUtc >= since), cancellationToken);
        var urlCounts = await CountByDeviceAsync(_db.UrlVisits.Where(e => e.StartedAtUtc >= since), cancellationToken);
        var breakCounts = await _db.SessionBreaks.Where(e => e.BreakStartUtc >= since)
            .GroupBy(e => e.DeviceId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var shotCounts = await _db.Screenshots.Where(e => e.CapturedAtUtc >= since)
            .GroupBy(e => e.DeviceId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return devices
            .Select(d =>
            {
                var silentFor = now - d.LastSeenUtc;

                // Agents sync every 30s by default, so a couple of minutes of silence is normal
                // jitter, five means several missed attempts, and anything beyond that is a
                // machine that is off, disconnected, or failing to authenticate.
                var state = silentFor <= TimeSpan.FromMinutes(2) ? "Reporting"
                    : silentFor <= TimeSpan.FromMinutes(15) ? "Late"
                    : "Silent";

                return new AgentStatus(
                    d.DeviceId,
                    d.MachineName,
                    d.LastUserName,
                    d.LastSeenUtc,
                    silentFor,
                    state,
                    appCounts.GetValueOrDefault(d.DeviceId)
                        + idleCounts.GetValueOrDefault(d.DeviceId)
                        + urlCounts.GetValueOrDefault(d.DeviceId)
                        + breakCounts.GetValueOrDefault(d.DeviceId)
                        + shotCounts.GetValueOrDefault(d.DeviceId),
                    appCounts.GetValueOrDefault(d.DeviceId),
                    idleCounts.GetValueOrDefault(d.DeviceId),
                    DisabledCaptures(d),
                    d.IsPinned);
            })
            .OrderByDescending(a => a.IsPinned)
            .ThenBy(a => a.State == "Reporting" ? 1 : 0) // problems first
            .ThenBy(a => a.MachineName)
            .ToList();
    }

    private static Task<Dictionary<Guid, int>> CountByDeviceAsync<T>(IQueryable<T> query, CancellationToken cancellationToken)
        where T : class =>
        query.GroupBy(e => EF.Property<Guid>(e, "DeviceId"))
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    /// <summary>
    /// Surfaced because a switched-off capture type looks exactly like a broken agent from the
    /// outside - the events simply never arrive, and nothing on the dashboard says why.
    /// </summary>
    private static string DisabledCaptures(Device d)
    {
        var off = new List<string>();
        if (!d.CaptureAppUsage) off.Add("apps");
        if (!d.CaptureUrlVisits) off.Add("urls");
        if (!d.CaptureIdle) off.Add("idle");
        if (!d.CaptureSessionBreaks) off.Add("breaks");
        if (!d.CaptureScreenshots) off.Add("screenshots");
        return off.Count == 0 ? "" : string.Join(", ", off);
    }

    private static double? PercentChange(double previous, double current)
    {
        if (previous <= 0)
        {
            return current > 0 ? null : 0; // no baseline to compare against
        }

        return (current - previous) / previous * 100.0;
    }

    private async Task<PeriodTotals> LoadPeriodAsync(
        HashSet<string> excludedUserNames,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var appUsage = _db.AppUsageEvents.Where(e => !excludedUserNames.Contains(e.UserName));
        var idle = _db.IdlePeriods.Where(e => !excludedUserNames.Contains(e.UserName));
        var urlVisits = _db.UrlVisits.Where(e => !excludedUserNames.Contains(e.UserName));
        var breaks = _db.SessionBreaks.Where(e => !excludedUserNames.Contains(e.UserName));
        var screenshots = _db.Screenshots.Where(e => !excludedUserNames.Contains(e.UserName));

        if (!string.IsNullOrWhiteSpace(UserName))
        {
            appUsage = appUsage.Where(e => e.UserName == UserName);
            idle = idle.Where(e => e.UserName == UserName);
            urlVisits = urlVisits.Where(e => e.UserName == UserName);
            breaks = breaks.Where(e => e.UserName == UserName);
            screenshots = screenshots.Where(e => e.UserName == UserName);
        }

        if (DeviceId is { } deviceId)
        {
            appUsage = appUsage.Where(e => e.DeviceId == deviceId);
            idle = idle.Where(e => e.DeviceId == deviceId);
            urlVisits = urlVisits.Where(e => e.DeviceId == deviceId);
            breaks = breaks.Where(e => e.DeviceId == deviceId);
            screenshots = screenshots.Where(e => e.DeviceId == deviceId);
        }

        if (fromUtc is { } from)
        {
            appUsage = appUsage.Where(e => e.StartedAtUtc >= from);
            idle = idle.Where(e => e.StartedAtUtc >= from);
            urlVisits = urlVisits.Where(e => e.StartedAtUtc >= from);
            breaks = breaks.Where(e => e.BreakStartUtc >= from);
            screenshots = screenshots.Where(e => e.CapturedAtUtc >= from);
        }

        if (toUtcExclusive is { } to)
        {
            appUsage = appUsage.Where(e => e.StartedAtUtc < to);
            idle = idle.Where(e => e.StartedAtUtc < to);
            urlVisits = urlVisits.Where(e => e.StartedAtUtc < to);
            breaks = breaks.Where(e => e.BreakStartUtc < to);
            screenshots = screenshots.Where(e => e.CapturedAtUtc < to);
        }

        var activeSeconds = await appUsage.SumAsync(e => (double?)e.DurationSeconds, cancellationToken) ?? 0;
        var idleSeconds = await idle.SumAsync(e => (double?)e.DurationSeconds, cancellationToken) ?? 0;

        var eventCount = await appUsage.CountAsync(cancellationToken)
            + await idle.CountAsync(cancellationToken)
            + await urlVisits.CountAsync(cancellationToken)
            + await breaks.CountAsync(cancellationToken)
            + await screenshots.CountAsync(cancellationToken);

        var activeDeviceIds = await appUsage.Select(e => e.DeviceId)
            .Union(idle.Select(e => e.DeviceId))
            .Union(urlVisits.Select(e => e.DeviceId))
            .Distinct()
            .ToListAsync(cancellationToken);

        var appTotals = await appUsage
            .GroupBy(e => e.ProcessName)
            .Select(g => new { g.Key, Total = g.Sum(e => e.DurationSeconds) })
            .ToListAsync(cancellationToken);

        var urlTuples = await urlVisits
            .Select(e => new { e.Url, e.DurationSeconds })
            .ToListAsync(cancellationToken);
        var siteTotals = urlTuples
            .GroupBy(x => ActivityAggregation.GetUrlHost(x.Url))
            .Select(g => (g.Key, g.Sum(x => x.DurationSeconds)))
            .ToList();

        return new PeriodTotals(
            activeSeconds,
            idleSeconds,
            eventCount,
            activeDeviceIds,
            appTotals.Select(x => (x.Key, x.Total)).ToList(),
            siteTotals);
    }

    public record AgentStatus(
        Guid DeviceId,
        string MachineName,
        string LastUserName,
        DateTimeOffset LastSeenUtc,
        TimeSpan SilentFor,
        string State,
        int EventsLastHour,
        int AppEventsLastHour,
        int IdleEventsLastHour,
        string DisabledCaptures,
        bool IsPinned);

    private record PeriodTotals(
        double ActiveSeconds,
        double IdleSeconds,
        int EventCount,
        List<Guid> ActiveDeviceIds,
        List<(string Label, double Seconds)> AppTotals,
        List<(string Label, double Seconds)> SiteTotals);
}
