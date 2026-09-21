using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Shared.Diagnostics;

namespace TimeTracker.Server.Pages;

/// <summary>
/// Faults from every Agent, plus the ones this server noticed on their behalf.
///
/// The two sources matter equally. An Agent reports what it can see - a refused API key, a failed
/// policy refresh, an exception in a tracker - but it cannot report that it has stopped talking
/// to the server, because saying so requires the connection that is missing. Those entries are
/// written from this side by AlertMonitor, which is why a device going quiet still produces a
/// line here rather than an unexplained gap.
/// </summary>
[Authorize(Policy = "AdminOnly")]
public class LogsModel : PageModel
{
    private const int PageSize = 200;

    private readonly TimeTrackerDbContext _db;

    public LogsModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<AgentLogEntry> Entries { get; private set; } = new();

    public List<Device> KnownDevices { get; private set; } = new();

    public int TotalMatching { get; private set; }

    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid? DeviceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public AgentLogLevel? Level { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Source { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ToDate { get; set; }

    public List<string> KnownSources { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        KnownDevices = await _db.Devices.OrderBy(d => d.MachineName).ToListAsync(cancellationToken);

        KnownSources = await _db.AgentLogs
            .Select(e => e.Source)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(cancellationToken);

        var query = _db.AgentLogs.AsQueryable();

        if (DeviceId is { } deviceId)
        {
            query = query.Where(e => e.DeviceId == deviceId);
        }

        if (Level is { } level)
        {
            // Treated as a floor rather than an exact match: someone filtering to warnings wants
            // the errors too, because errors are worse, not different.
            query = query.Where(e => e.Level >= level);
        }

        if (!string.IsNullOrWhiteSpace(Source))
        {
            query = query.Where(e => e.Source == Source);
        }

        if (FromDate is { } from)
        {
            var fromUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            query = query.Where(e => e.OccurredAtUtc >= fromUtc);
        }

        if (ToDate is { } to)
        {
            var toUtc = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1);
            query = query.Where(e => e.OccurredAtUtc < toUtc);
        }

        TotalMatching = await query.CountAsync(cancellationToken);

        Entries = await query
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        // Counted over the last day rather than the filtered set: this is the "is anything wrong
        // right now" number, and it should not change when someone narrows the list.
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        ErrorCount = await _db.AgentLogs.CountAsync(
            e => e.Level == AgentLogLevel.Error && e.OccurredAtUtc >= since, cancellationToken);
        WarningCount = await _db.AgentLogs.CountAsync(
            e => e.Level == AgentLogLevel.Warning && e.OccurredAtUtc >= since, cancellationToken);
    }

    public bool IsTruncated => TotalMatching > Entries.Count;

    public static string LevelClass(AgentLogLevel level) => level switch
    {
        AgentLogLevel.Error => "is-error",
        AgentLogLevel.Warning => "is-warning",
        _ => "is-info",
    };
}
