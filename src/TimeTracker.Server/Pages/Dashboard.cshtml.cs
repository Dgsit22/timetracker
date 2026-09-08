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

    private record PeriodTotals(
        double ActiveSeconds,
        double IdleSeconds,
        int EventCount,
        List<Guid> ActiveDeviceIds,
        List<(string Label, double Seconds)> AppTotals,
        List<(string Label, double Seconds)> SiteTotals);
}
