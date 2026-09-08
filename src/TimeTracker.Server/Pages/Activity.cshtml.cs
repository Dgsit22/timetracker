using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

[Authorize]
public class ActivityModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public ActivityModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public static readonly string[] EventTypes = { "AppUsage", "UrlVisit", "Idle", "SessionBreak", "Screenshot" };

    public List<ActivityRow> Rows { get; private set; } = new();

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

    [BindProperty(SupportsGet = true)]
    public string[]? Types { get; set; }

    // Unchecked checkboxes don't post, so an empty/null Types array alone can't tell "fresh page
    // load" apart from "user unchecked every type" - this sentinel (posted alongside Types on every
    // auto-submit once any filter is touched) disambiguates: absent -> default to all types.
    [BindProperty(SupportsGet = true)]
    public bool TypesFilterApplied { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        KnownUsers = await _db.AppUsageEvents.Select(e => e.UserName)
            .Union(_db.IdlePeriods.Select(e => e.UserName))
            .Union(_db.UrlVisits.Select(e => e.UserName))
            .Union(_db.SessionBreaks.Select(e => e.UserName))
            .Union(_db.Screenshots.Select(e => e.UserName))
            .Distinct()
            .OrderBy(u => u)
            .ToListAsync(cancellationToken);

        KnownDevices = await _db.Devices.OrderBy(d => d.MachineName).ToListAsync(cancellationToken);

        var appUsage = _db.AppUsageEvents.AsQueryable();
        var idle = _db.IdlePeriods.AsQueryable();
        var urlVisits = _db.UrlVisits.AsQueryable();
        var breaks = _db.SessionBreaks.AsQueryable();
        var screenshots = _db.Screenshots.AsQueryable();

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

        // ToDate is inclusive of the whole day, so the upper bound is the *next* UTC day's start.
        DateTimeOffset? fromUtc = FromDate is { } fd ? new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DateTimeOffset? toUtcExclusive = ToDate is { } td ? new DateTimeOffset(td.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1) : null;

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

        var includedTypes = TypesFilterApplied
            ? (Types ?? Array.Empty<string>()).ToHashSet()
            : EventTypes.ToHashSet();

        const int perTypeLimit = 100;

        var rows = new List<ActivityRow>();

        if (includedTypes.Contains("AppUsage"))
        {
            rows.AddRange(await appUsage.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "AppUsage", e.UserName, e.DeviceId, e.StartedAtUtc,
                    $"{e.ProcessName} - {e.WindowTitle}", e.DurationSeconds, null))
                .ToListAsync(cancellationToken));
        }

        if (includedTypes.Contains("UrlVisit"))
        {
            rows.AddRange(await urlVisits.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "UrlVisit", e.UserName, e.DeviceId, e.StartedAtUtc,
                    $"{e.Browser}: {e.PageTitle}", e.DurationSeconds, null))
                .ToListAsync(cancellationToken));
        }

        if (includedTypes.Contains("Idle"))
        {
            rows.AddRange(await idle.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "Idle", e.UserName, e.DeviceId, e.StartedAtUtc,
                    $"Idle >= {e.IdleThresholdSeconds}s", e.DurationSeconds, null))
                .ToListAsync(cancellationToken));
        }

        if (includedTypes.Contains("SessionBreak"))
        {
            var breakEntities = await breaks.OrderByDescending(e => e.BreakStartUtc).Take(perTypeLimit)
                .ToListAsync(cancellationToken);
            rows.AddRange(breakEntities.Select(e => new ActivityRow(
                "SessionBreak", e.UserName, e.DeviceId, e.BreakStartUtc,
                $"{e.Reason} -> {(e.EndReason == null ? "(open)" : e.EndReason.ToString())}",
                e.BreakEndUtc == null ? null : (e.BreakEndUtc.Value - e.BreakStartUtc).TotalSeconds,
                null)));
        }

        if (includedTypes.Contains("Screenshot"))
        {
            rows.AddRange(await screenshots.OrderByDescending(e => e.CapturedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "Screenshot", e.UserName, e.DeviceId, e.CapturedAtUtc,
                    $"Monitor {e.MonitorIndex} ({e.WidthPx}x{e.HeightPx})", null, e.EventId))
                .ToListAsync(cancellationToken));
        }

        Rows = rows.OrderByDescending(r => r.TimestampUtc).Take(200).ToList();
    }
}

public record ActivityRow(
    string Kind,
    string UserName,
    Guid DeviceId,
    DateTimeOffset TimestampUtc,
    string Details,
    double? DurationSeconds,
    Guid? ScreenshotId);
