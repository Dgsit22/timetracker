using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

/// <summary>
/// What this server is actually configured to do, in one place. Every value here is set outside
/// the app - in .env and docker-compose - so the page is deliberately read-only: it answers "is
/// retention on, and for how long", "did the agent key reach the container", "how much of the
/// database is screenshots", without anyone having to SSH in and read environment variables off a
/// running container.
/// </summary>
[Authorize(Policy = "AdminOnly")]
public class SettingsModel : PageModel
{
    private readonly TimeTrackerDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly RetentionOptions _retention;
    private readonly IWebHostEnvironment _environment;

    public SettingsModel(
        TimeTrackerDbContext db,
        IConfiguration configuration,
        RetentionOptions retention,
        IWebHostEnvironment environment)
    {
        _db = db;
        _configuration = configuration;
        _retention = retention;
        _environment = environment;
    }

    public RetentionOptions Retention => _retention;

    public string EnvironmentName => _environment.EnvironmentName;

    public string ServerVersion { get; private set; } = "1.0.0";

    public bool AgentKeyConfigured { get; private set; }

    public int AgentKeyLength { get; private set; }

    public bool RequestIsHttps { get; private set; }

    public int DeviceCount { get; private set; }

    public int ReportingDeviceCount { get; private set; }

    public DateTimeOffset? OldestEventUtc { get; private set; }

    public List<TableSize> Tables { get; private set; } = new();

    public long ScreenshotBytes { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ServerVersion = typeof(SettingsModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        var apiKey = _configuration["Agent:ApiKey"];
        AgentKeyConfigured = !string.IsNullOrEmpty(apiKey);
        AgentKeyLength = apiKey?.Length ?? 0;
        RequestIsHttps = Request.IsHttps;

        var devices = await _db.Devices
            .Select(d => new { d.LastSeenUtc })
            .ToListAsync(cancellationToken);

        DeviceCount = devices.Count;
        ReportingDeviceCount = devices.Count(d => DateTimeOffset.UtcNow - d.LastSeenUtc < TimeSpan.FromHours(2));

        // Row counts come from the planner's own statistics rather than COUNT(*). This page exists
        // to be opened casually, and five sequential scans over millions of rows is not something
        // a settings page should cost. The numbers are approximate and labelled as such.
        Tables = await _db.Database
            .SqlQuery<TableSize>($"""
                SELECT relname AS "Name",
                       GREATEST(reltuples, 0)::bigint AS "ApproximateRows",
                       pg_total_relation_size(c.oid) AS "Bytes"
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public'
                  AND c.relkind = 'r'
                  AND relname IN ('AppUsageEvents','UrlVisits','IdlePeriods','SessionBreaks','Screenshots','Devices')
                ORDER BY pg_total_relation_size(c.oid) DESC
                """)
            .ToListAsync(cancellationToken);

        ScreenshotBytes = Tables.FirstOrDefault(t => t.Name == "Screenshots")?.Bytes ?? 0;

        OldestEventUtc = await _db.AppUsageEvents
            .OrderBy(e => e.StartedAtUtc)
            .Select(e => (DateTimeOffset?)e.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };
}

public record TableSize(string Name, long ApproximateRows, long Bytes);
