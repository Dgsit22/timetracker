using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Shared.Events;

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

    public List<DeviceSummary> DeviceSummaries { get; private set; } = new();

    public DateTimeOffset? TimelineFromUtc { get; private set; }

    public DateTimeOffset? TimelineToUtc { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? UserName { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? DeviceId { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? ToDate { get; set; }

    // Time-of-day window, applied to every day in the date range. Both must be set for it to do
    // anything: half a window is an incomplete thought, not a filter.
    [BindProperty(SupportsGet = true)]
    public TimeOnly? TimeFrom { get; set; }

    [BindProperty(SupportsGet = true)]
    public TimeOnly? TimeTo { get; set; }

    // IANA id of the zone the window is expressed in, posted by the page from the same timezone
    // choice the top bar uses (site.js keeps it in localStorage, so the server cannot know it
    // otherwise). Times on this page are shown in that zone, so the window has to mean that zone.
    [BindProperty(SupportsGet = true)]
    public string? Zone { get; set; }

    /// <summary>The resolved windows, or null when no complete window was given.</summary>
    public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? ActiveWindows { get; private set; }

    /// <summary>What the view labels the time inputs with, e.g. "IST".</summary>
    public string WindowZoneLabel { get; private set; } = "UTC";

    [BindProperty(SupportsGet = true)]
    public string[]? Types { get; set; }

    // Unchecked checkboxes don't post, so an empty/null Types array alone can't tell "fresh page
    // load" apart from "user unchecked every type" - this sentinel (posted alongside Types on every
    // auto-submit once any filter is touched) disambiguates: absent -> default to all types.
    [BindProperty(SupportsGet = true)]
    public bool TypesFilterApplied { get; set; }

    // The page shows a capped slice; the CSV handler raises both caps before running the same
    // query, so an export is the whole filtered range rather than only what is on screen.
    private int _perTypeLimit = 100;
    private int _rowCap = 200;

    /// <summary>
    /// Turns the posted window into concrete UTC intervals, one per day of the range, and records
    /// the zone label the view shows beside the inputs. Returns null when there is nothing to
    /// apply, which leaves every query exactly as it was before this filter existed.
    /// </summary>
    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>? ResolveWindows()
    {
        var zone = ResolveZone();
        WindowZoneLabel = ZoneLabel(zone);

        if (TimeFrom is not { } timeFrom || TimeTo is not { } timeTo)
        {
            return null;
        }

        // The date range bounds which days the window repeats over. Both are defaulted to today on
        // a bare load, so in practice this only guards a hand-written query string.
        if (FromDate is not { } fromDate || ToDate is not { } toDate || toDate < fromDate)
        {
            return null;
        }

        // The window is expressed in the display zone, so a day at the edge of the UTC range can
        // reach a few hours outside it - a day either side of the range covers every supported
        // zone, and windows that fall outside simply match nothing.
        return TimeWindows.Build(fromDate.AddDays(-1), toDate.AddDays(1), timeFrom, timeTo, zone);
    }

    private TimeZoneInfo ResolveZone()
    {
        if (string.IsNullOrWhiteSpace(Zone))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(Zone);
        }
        catch (Exception)
        {
            // An unknown id means a hand-edited query string or a container without tzdata.
            // Falling back to UTC keeps the page working; the label says which zone was used.
            return TimeZoneInfo.Utc;
        }
    }

    private static string ZoneLabel(TimeZoneInfo zone) => zone.Id switch
    {
        "Asia/Kolkata" => "IST",
        "America/New_York" => "EST",
        "UTC" => "UTC",
        _ => zone.Id,
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // Default to today on a bare page load, matching Dashboard. Without it this page opened on
        // all history, so the per-device cards showed lifetime totals (60+ hours of "active time")
        // where an admin reasonably reads them as today's. The quick filters still reach older data.
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

        // ToDate is inclusive of the whole day, so the upper bound is the *next* UTC day's start.
        DateTimeOffset? fromUtc = FromDate is { } fd ? new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DateTimeOffset? toUtcExclusive = ToDate is { } td ? new DateTimeOffset(td.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(1) : null;

        // Resolve the time-of-day window before the range filters below, so every query can
        // carry it. Needs both ends and a concrete date range: "every day" has to know which days.
        var windows = ResolveWindows();
        ActiveWindows = windows;

        if (windows is not null)
        {
            // Events are matched by overlap, not by start time: an idle stretch running 08:40 to
            // 10:20 belongs in a 09:00 window, and filtering on its start alone would drop it from
            // the log while the totals below still counted the part inside. Screenshots are
            // instants, so for them overlap and containment are the same test.
            appUsage = appUsage.Where(TimeWindows.AnyWindow<AppUsageEvent>(
                windows, (s, e) => x => x.StartedAtUtc < e && x.EndedAtUtc > s));
            idle = idle.Where(TimeWindows.AnyWindow<IdlePeriodEvent>(
                windows, (s, e) => x => x.StartedAtUtc < e && x.EndedAtUtc > s));
            urlVisits = urlVisits.Where(TimeWindows.AnyWindow<UrlVisitEvent>(
                windows, (s, e) => x => x.StartedAtUtc < e && x.EndedAtUtc > s));
            breaks = breaks.Where(TimeWindows.AnyWindow<SessionBreakEvent>(
                windows, (s, e) => x => x.BreakStartUtc < e && (x.BreakEndUtc == null || x.BreakEndUtc > s)));
            screenshots = screenshots.Where(TimeWindows.AnyWindow<ScreenshotEvent>(
                windows, (s, e) => x => x.CapturedAtUtc >= s && x.CapturedAtUtc < e));
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

        // Per-device summary cards: driven by Device + date range only, deliberately not UserName
        // or the event-type checkboxes below - it's a device-level aggregate, a separate concern
        // from which raw rows the results table shows. Still excludes hidden users, same as everything.
        var summaryAppUsage = _db.AppUsageEvents.Where(e => !excludedUserNames.Contains(e.UserName));
        var summaryUrlVisits = _db.UrlVisits.Where(e => !excludedUserNames.Contains(e.UserName));

        if (DeviceId is { } summaryDeviceId)
        {
            summaryAppUsage = summaryAppUsage.Where(e => e.DeviceId == summaryDeviceId);
            summaryUrlVisits = summaryUrlVisits.Where(e => e.DeviceId == summaryDeviceId);
        }

        if (windows is not null)
        {
            summaryAppUsage = summaryAppUsage.Where(TimeWindows.AnyWindow<AppUsageEvent>(
                windows, (s, e) => x => x.StartedAtUtc < e && x.EndedAtUtc > s));
            summaryUrlVisits = summaryUrlVisits.Where(TimeWindows.AnyWindow<UrlVisitEvent>(
                windows, (s, e) => x => x.StartedAtUtc < e && x.EndedAtUtc > s));
        }

        if (fromUtc is { } summaryFrom)
        {
            summaryAppUsage = summaryAppUsage.Where(e => e.StartedAtUtc >= summaryFrom);
            summaryUrlVisits = summaryUrlVisits.Where(e => e.StartedAtUtc >= summaryFrom);
        }

        if (toUtcExclusive is { } summaryTo)
        {
            summaryAppUsage = summaryAppUsage.Where(e => e.StartedAtUtc < summaryTo);
            summaryUrlVisits = summaryUrlVisits.Where(e => e.StartedAtUtc < summaryTo);
        }

        // The timeline draws whole days in the viewer's chosen display timezone, which the page
        // only knows client-side, so segments are loaded with enough slack either side of the UTC
        // range to cover any supported zone's midnight (IST +5:30, EST -5). Totals below stay on
        // the UTC range, matching the Dashboard and every other filter on the site.
        var now = DateTimeOffset.UtcNow;
        var slack = TimeSpan.FromHours(14);
        TimelineFromUtc = fromUtc - slack;
        TimelineToUtc = toUtcExclusive is { } rangeEnd && rangeEnd + slack < now ? rangeEnd + slack : now;

        var intervals = await TimeBreakdown.LoadIntervalsAsync(
            _db, excludedUserNames, null, DeviceId, TimelineFromUtc, TimelineToUtc, cancellationToken);
        var segmentsByDevice = TimeBreakdown.Resolve(intervals, TimelineFromUtc, TimelineToUtc.Value)
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (windows is not null)
        {
            // Clipping here covers both the totals below and the timeline the view renders from
            // the same lists, so the drawn day shows only the window - the hours outside it fall
            // back to the timeline's existing "Not tracked" gap styling with no JS change.
            segmentsByDevice = segmentsByDevice.ToDictionary(
                kv => kv.Key,
                kv => TimeWindows.Clip(kv.Value, windows).ToList());
        }

        TimeBreakdown TotalsInRange(Guid deviceId) => TimeBreakdown.Sum(
            (segmentsByDevice.GetValueOrDefault(deviceId) ?? new())
                .Select(x => x with
                {
                    Start = fromUtc is { } f && x.Start < f ? f : x.Start,
                    End = toUtcExclusive is { } t && x.End > t ? t : x.End,
                })
                .Where(x => x.End > x.Start));

        var totalsByDevice = KnownDevices.ToDictionary(d => d.DeviceId, d => TotalsInRange(d.DeviceId));

        var appTotals = await summaryAppUsage
            .GroupBy(e => new { e.DeviceId, e.ProcessName })
            .Select(g => new { g.Key.DeviceId, g.Key.ProcessName, Total = g.Sum(e => e.DurationSeconds) })
            .ToListAsync(cancellationToken);
        var topAppsByDevice = appTotals.GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => ActivityAggregation.BuildTopSlices(g.Select(x => (x.ProcessName, x.Total))));

        // Host extraction has no SQL translation, so pull narrow (device, url, duration) tuples and
        // group client-side - no heavier than the existing table's own per-row UrlVisit projection.
        var urlTuples = await summaryUrlVisits
            .Select(e => new { e.DeviceId, e.Url, e.DurationSeconds })
            .ToListAsync(cancellationToken);
        var topSitesByDevice = urlTuples
            .GroupBy(x => (x.DeviceId, Host: ActivityAggregation.GetUrlHost(x.Url)))
            .Select(g => new { g.Key.DeviceId, g.Key.Host, Total = g.Sum(x => x.DurationSeconds) })
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => ActivityAggregation.BuildTopSlices(g.Select(x => (x.Host, x.Total))));

        var devicesToSummarize = DeviceId is { } filterDeviceId
            ? KnownDevices.Where(d => d.DeviceId == filterDeviceId)
            : KnownDevices.Where(d => totalsByDevice[d.DeviceId].TotalSeconds > 0);

        DeviceSummaries = devicesToSummarize
            .Select(d => new DeviceSummary(
                d.DeviceId, d.MachineName,
                totalsByDevice.GetValueOrDefault(d.DeviceId) ?? new TimeBreakdown(0, 0, 0, 0),
                segmentsByDevice.GetValueOrDefault(d.DeviceId) ?? new List<StateSegment>(),
                topAppsByDevice.GetValueOrDefault(d.DeviceId) ?? new List<CategorySlice>(),
                topSitesByDevice.GetValueOrDefault(d.DeviceId) ?? new List<CategorySlice>(),
                d.IsPinned,
                d.LastSeenUtc))
            // Pinned first, so a machine being watched closely stays at the top even on a quiet
            // day when its active time would otherwise sink below everything else.
            .OrderByDescending(s => s.IsPinned)
            .ThenByDescending(s => s.Time.ActiveSeconds)
            .ToList();

        var includedTypes = TypesFilterApplied
            ? (Types ?? Array.Empty<string>()).ToHashSet()
            : EventTypes.ToHashSet();

        var perTypeLimit = _perTypeLimit;

        var rows = new List<ActivityRow>();

        if (includedTypes.Contains("AppUsage"))
        {
            rows.AddRange(await appUsage.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "AppUsage", e.UserName, e.DeviceId, e.StartedAtUtc,
                    $"{e.ProcessName} - {e.WindowTitle}", e.DurationSeconds, null, null))
                .ToListAsync(cancellationToken));
        }

        if (includedTypes.Contains("UrlVisit"))
        {
            rows.AddRange(await urlVisits.OrderByDescending(e => e.StartedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "UrlVisit", e.UserName, e.DeviceId, e.StartedAtUtc,
                    $"{e.Browser}: {e.PageTitle}", e.DurationSeconds, null, null))
                .ToListAsync(cancellationToken));
        }

        if (includedTypes.Contains("Idle"))
        {
            // The Agent flushes an ongoing idle stretch every minute so totals advance live, which
            // made a 20-minute absence twenty near-identical rows. Contiguous chunks are joined back
            // into the one span they describe; the extra fetch headroom keeps the cap per span.
            // Headroom for the per-minute flush chunks, but bounded: at export caps the old
            // multiplier alone would pull 150k rows into memory to merge.
            var idleChunks = await idle.OrderByDescending(e => e.StartedAtUtc)
                .Take(Math.Min(perTypeLimit * 30, 20000))
                .Select(e => new { e.UserName, e.DeviceId, e.StartedAtUtc, e.EndedAtUtc, e.IdleThresholdSeconds })
                .ToListAsync(cancellationToken);

            var spans = new List<(string UserName, Guid DeviceId, DateTimeOffset Start, DateTimeOffset End, int Threshold)>();
            foreach (var chunk in idleChunks.OrderBy(e => e.DeviceId).ThenBy(e => e.StartedAtUtc))
            {
                if (spans.Count > 0 && spans[^1].DeviceId == chunk.DeviceId && spans[^1].UserName == chunk.UserName
                    && chunk.StartedAtUtc - spans[^1].End <= TimeSpan.FromSeconds(15))
                {
                    var last = spans[^1];
                    spans[^1] = last with { End = chunk.EndedAtUtc > last.End ? chunk.EndedAtUtc : last.End };
                }
                else
                {
                    spans.Add((chunk.UserName, chunk.DeviceId, chunk.StartedAtUtc, chunk.EndedAtUtc, chunk.IdleThresholdSeconds));
                }
            }

            rows.AddRange(spans.OrderByDescending(x => x.Start).Take(perTypeLimit).Select(x => new ActivityRow(
                "Idle", x.UserName, x.DeviceId, x.Start,
                $"No keyboard or mouse input (counts as idle after {(x.Threshold % 60 == 0 ? $"{x.Threshold / 60} min" : $"{x.Threshold}s")})", (x.End - x.Start).TotalSeconds, null)));
        }

        if (includedTypes.Contains("SessionBreak"))
        {
            var breakEntities = await breaks.OrderByDescending(e => e.BreakStartUtc).Take(perTypeLimit)
                .ToListAsync(cancellationToken);
            rows.AddRange(breakEntities.Select(e => new ActivityRow(
                "SessionBreak", e.UserName, e.DeviceId, e.BreakStartUtc,
                DescribeBreak(e.Reason, e.EndReason, e.IdleSecondsAtStart),
                e.BreakEndUtc == null ? null : (e.BreakEndUtc.Value - e.BreakStartUtc).TotalSeconds,
                null,
                e.BreakEndUtc)));
        }

        if (includedTypes.Contains("Screenshot"))
        {
            rows.AddRange(await screenshots.OrderByDescending(e => e.CapturedAtUtc).Take(perTypeLimit)
                .Select(e => new ActivityRow(
                    "Screenshot", e.UserName, e.DeviceId, e.CapturedAtUtc,
                    $"Monitor {e.MonitorIndex} ({e.WidthPx}x{e.HeightPx})", null, e.EventId, null))
                .ToListAsync(cancellationToken));
        }

        Rows = rows.OrderByDescending(r => r.TimestampUtc).Take(_rowCap).ToList();
    }

    /// <summary>
    /// Exports exactly what the current filters describe - same date range, users, devices and
    /// event types as the page, just without its display cap.
    /// </summary>
    public async Task<IActionResult> OnGetExportAsync(CancellationToken cancellationToken)
    {
        _perTypeLimit = 5000;
        _rowCap = 20000;

        await OnGetAsync(cancellationToken);

        var machineNames = DeviceSummaries.ToDictionary(d => d.DeviceId, d => d.MachineName);

        var csv = new StringBuilder();
        csv.AppendLine(Csv.Line("Timestamp (UTC)", "Type", "User", "Machine", "Details",
            "Duration (s)", "Ended (UTC)"));

        foreach (var row in Rows)
        {
            csv.AppendLine(Csv.Line(
                row.TimestampUtc,
                row.Kind,
                row.UserName,
                machineNames.GetValueOrDefault(row.DeviceId, row.DeviceId.ToString()),
                row.Details,
                row.DurationSeconds,
                row.EndUtc));
        }

        var from = FromDate?.ToString("yyyyMMdd") ?? "all";
        var to = ToDate?.ToString("yyyyMMdd") ?? "all";

        return File(Csv.ToUtf8WithBom(csv), "text/csv", $"timetracker-activity-{from}-{to}.csv");
    }

    private static string DescribeBreak(SessionBreakReason reason, SessionBreakEndReason? endReason, double? idleSecondsAtStart)
    {
        var start = reason switch
        {
            // Rows recorded before lock kinds were told apart carry Lock with no idle measurement,
            // so they can't honestly be called the user's own lock.
            SessionBreakReason.Lock when idleSecondsAtStart is null => "Screen locked",
            SessionBreakReason.Lock => "Locked by user",
            SessionBreakReason.AutoLock when idleSecondsAtStart is { } idle =>
                $"Locked automatically after {ActivityAggregation.FormatRowDuration(idle)} with no input",
            SessionBreakReason.AutoLock => "Locked automatically",
            SessionBreakReason.Logoff => "Signed out",
            SessionBreakReason.MachineSleep => "Asleep",
            SessionBreakReason.MachineShutdown => "Shut down or restarted",
            _ => reason.ToString(),
        };

        var end = endReason switch
        {
            SessionBreakEndReason.Unlock => "unlocked",
            SessionBreakEndReason.Logon => "signed back in",
            SessionBreakEndReason.MachineWake => "woke up",
            null => "still away",
            _ => endReason.ToString(),
        };

        return $"{start}, then {end}";
    }
}

public record ActivityRow(
    string Kind,
    string UserName,
    Guid DeviceId,
    DateTimeOffset TimestampUtc,
    string Details,
    double? DurationSeconds,
    Guid? ScreenshotId,
    DateTimeOffset? EndUtc = null);

public record DeviceSummary(
    Guid DeviceId,
    string MachineName,
    TimeBreakdown Time,
    List<StateSegment> Segments,
    List<CategorySlice> TopApps,
    List<CategorySlice> TopSites,
    bool IsPinned,
    DateTimeOffset LastSeenUtc)
{
    /// <summary>
    /// The Agent checks in at least hourly even on a quiet machine, so silence past that means it
    /// has stopped reporting. Two hours rather than one, matching the Devices page, so a single
    /// missed check-in is not called an outage.
    /// </summary>
    public bool IsReporting => DateTimeOffset.UtcNow - LastSeenUtc < TimeSpan.FromHours(2);
}
