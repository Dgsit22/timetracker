using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

public class ActivityModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public ActivityModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<ActivityRow> Rows { get; private set; } = new();

    public List<string> KnownUsers { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? UserName { get; set; }

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

        const int perTypeLimit = 100;

        var rows = new List<ActivityRow>();

        rows.AddRange(await appUsage.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
            .Select(e => new ActivityRow(
                "AppUsage", e.UserName, e.DeviceId, e.StartedAtUtc,
                $"{e.ProcessName} - {e.WindowTitle}", e.DurationSeconds, null))
            .ToListAsync(cancellationToken));

        rows.AddRange(await urlVisits.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
            .Select(e => new ActivityRow(
                "UrlVisit", e.UserName, e.DeviceId, e.StartedAtUtc,
                $"{e.Browser}: {e.PageTitle}", e.DurationSeconds, null))
            .ToListAsync(cancellationToken));

        rows.AddRange(await idle.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
            .Select(e => new ActivityRow(
                "Idle", e.UserName, e.DeviceId, e.StartedAtUtc,
                $"Idle >= {e.IdleThresholdSeconds}s", e.DurationSeconds, null))
            .ToListAsync(cancellationToken));

        var breakEntities = await breaks.OrderByDescending(e => e.BreakStartUtc).Take(perTypeLimit)
            .ToListAsync(cancellationToken);
        rows.AddRange(breakEntities.Select(e => new ActivityRow(
            "SessionBreak", e.UserName, e.DeviceId, e.BreakStartUtc,
            $"{e.Reason} -> {(e.EndReason == null ? "(open)" : e.EndReason.ToString())}",
            e.BreakEndUtc == null ? null : (e.BreakEndUtc.Value - e.BreakStartUtc).TotalSeconds,
            null)));

        rows.AddRange(await screenshots.OrderByDescending(e => e.CapturedAtUtc).Take(perTypeLimit)
            .Select(e => new ActivityRow(
                "Screenshot", e.UserName, e.DeviceId, e.CapturedAtUtc,
                $"Monitor {e.MonitorIndex} ({e.WidthPx}x{e.HeightPx})", null, e.EventId))
            .ToListAsync(cancellationToken));

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
