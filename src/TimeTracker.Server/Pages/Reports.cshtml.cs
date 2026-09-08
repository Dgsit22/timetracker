using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

/// <summary>
/// Timesheet-style per-user summary (Active time, Idle time, session-break count) over a
/// selectable date range, with CSV export. Not explicitly specced beyond a nav label -
/// this is the concrete, scoped interpretation of "Reports" that fits the app's actual data.
/// </summary>
[Authorize]
public class ReportsModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public ReportsModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<string> KnownUsers { get; private set; } = new();

    public List<Group> KnownGroups { get; private set; } = new();

    public List<UserReportRow> Rows { get; private set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? UserName { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? GroupId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ToDate { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("User,Active Time (s),Idle Time (s),Session Breaks");
        foreach (var row in Rows)
        {
            csv.AppendLine($"{row.UserName},{row.ActiveSeconds:F0},{row.IdleSeconds:F0},{row.SessionBreakCount}");
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "timetracker-report.csv");
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var excludedUserNames = await ActivityAggregation.GetExcludedUserNamesAsync(_db, cancellationToken);

        KnownGroups = await _db.Groups.OrderBy(g => g.Name).ToListAsync(cancellationToken);

        var allUsers = (await _db.AppUsageEvents.Select(e => e.UserName)
            .Union(_db.IdlePeriods.Select(e => e.UserName))
            .Union(_db.UrlVisits.Select(e => e.UserName))
            .Union(_db.SessionBreaks.Select(e => e.UserName))
            .Union(_db.Screenshots.Select(e => e.UserName))
            .Distinct()
            .OrderBy(u => u)
            .ToListAsync(cancellationToken))
            .Where(u => !excludedUserNames.Contains(u))
            .ToList();
        KnownUsers = allUsers;

        HashSet<string>? scopedUserNames = null;
        if (!string.IsNullOrWhiteSpace(UserName))
        {
            scopedUserNames = new HashSet<string> { UserName };
        }
        else if (GroupId is { } groupId)
        {
            var members = await _db.GroupMembers.Where(m => m.GroupId == groupId).Select(m => m.UserName).ToListAsync(cancellationToken);
            scopedUserNames = members.ToHashSet();
        }

        var appUsage = _db.AppUsageEvents.Where(e => !excludedUserNames.Contains(e.UserName));
        var idle = _db.IdlePeriods.Where(e => !excludedUserNames.Contains(e.UserName));
        var breaks = _db.SessionBreaks.Where(e => !excludedUserNames.Contains(e.UserName));

        if (scopedUserNames is not null)
        {
            appUsage = appUsage.Where(e => scopedUserNames.Contains(e.UserName));
            idle = idle.Where(e => scopedUserNames.Contains(e.UserName));
            breaks = breaks.Where(e => scopedUserNames.Contains(e.UserName));
        }

        var fromUtc = FromDate is { } fd ? new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : (DateTimeOffset?)null;
        var toUtcExclusive = ToDate is { } td ? new DateTimeOffset(td.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1) : (DateTimeOffset?)null;

        if (fromUtc is { } from)
        {
            appUsage = appUsage.Where(e => e.StartedAtUtc >= from);
            idle = idle.Where(e => e.StartedAtUtc >= from);
            breaks = breaks.Where(e => e.BreakStartUtc >= from);
        }

        if (toUtcExclusive is { } to)
        {
            appUsage = appUsage.Where(e => e.StartedAtUtc < to);
            idle = idle.Where(e => e.StartedAtUtc < to);
            breaks = breaks.Where(e => e.BreakStartUtc < to);
        }

        var activeByUser = await appUsage.GroupBy(e => e.UserName)
            .Select(g => new { g.Key, Total = g.Sum(e => e.DurationSeconds) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, cancellationToken);

        var idleByUser = await idle.GroupBy(e => e.UserName)
            .Select(g => new { g.Key, Total = g.Sum(e => e.DurationSeconds) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, cancellationToken);

        var breakCountByUser = await breaks.GroupBy(e => e.UserName)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var relevantUsers = scopedUserNames ?? allUsers.ToHashSet();

        Rows = relevantUsers
            .Where(u => activeByUser.ContainsKey(u) || idleByUser.ContainsKey(u) || breakCountByUser.ContainsKey(u))
            .Select(u => new UserReportRow(
                u,
                activeByUser.GetValueOrDefault(u),
                idleByUser.GetValueOrDefault(u),
                breakCountByUser.GetValueOrDefault(u)))
            .OrderByDescending(r => r.ActiveSeconds)
            .ToList();
    }
}

public record UserReportRow(string UserName, double ActiveSeconds, double IdleSeconds, int SessionBreakCount);
