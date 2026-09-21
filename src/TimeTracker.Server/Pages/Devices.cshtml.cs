using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Shared.Devices;

namespace TimeTracker.Server.Pages;

[Authorize(Policy = "AdminOnly")]
public class DevicesModel : PageModel
{
    private readonly TimeTrackerDbContext _db;

    public DevicesModel(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public List<Device> Devices { get; private set; } = new();

    public Dictionary<Guid, TimeSpan> IdleTodayByDevice { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Devices = await _db.Devices
            .OrderByDescending(d => d.IsPinned)
            .ThenBy(d => d.MachineName)
            .ToListAsync(cancellationToken);

        // "Today" is a UTC calendar day, consistent with FirstSeenUtc/LastSeenUtc already shown
        // raw-UTC on this page - not converted to any admin's local time.
        var todayStartUtc = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var todayEndUtc = todayStartUtc.AddDays(1);

        IdleTodayByDevice = await _db.IdlePeriods
            .Where(p => p.StartedAtUtc >= todayStartUtc && p.StartedAtUtc < todayEndUtc)
            .GroupBy(p => p.DeviceId)
            .Select(g => new { DeviceId = g.Key, TotalSeconds = g.Sum(p => p.DurationSeconds) })
            .ToDictionaryAsync(x => x.DeviceId, x => TimeSpan.FromSeconds(x.TotalSeconds), cancellationToken);
    }

    /// <summary>
    /// The Agent checks in at least hourly even when the machine is quiet (SyncClient sends an
    /// empty heartbeat batch), so silence well past that means it is not reporting - stopped,
    /// powered off, or unable to reach the server. Two hours rather than one avoids calling a
    /// device offline over a single missed check-in.
    /// </summary>
    public const int OfflineAfterHours = 2;

    public static bool IsReporting(Device device) =>
        DateTimeOffset.UtcNow - device.LastSeenUtc < TimeSpan.FromHours(OfflineAfterHours);

    public static string FormatSilence(Device device)
    {
        var silence = DateTimeOffset.UtcNow - device.LastSeenUtc;

        if (silence < TimeSpan.FromHours(OfflineAfterHours))
        {
            return "Reporting";
        }

        if (silence < TimeSpan.FromDays(1))
        {
            return $"Silent {(int)silence.TotalHours}h";
        }

        return $"Silent {(int)silence.TotalDays}d";
    }

    public static string FormatIdle(TimeSpan idle)
    {
        if (idle <= TimeSpan.Zero)
        {
            return "0m";
        }

        var hours = (int)idle.TotalHours;
        var minutes = idle.Minutes;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    public async Task<IActionResult> OnPostAsync(
        Guid deviceId,
        bool captureAppUsage,
        bool captureUrlVisits,
        bool captureIdle,
        bool captureSessionBreaks,
        bool captureScreenshots,
        int screenshotIntervalMinutes,
        CancellationToken cancellationToken)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
        if (device is not null)
        {
            device.CaptureAppUsage = captureAppUsage;
            device.CaptureUrlVisits = captureUrlVisits;
            device.CaptureIdle = captureIdle;
            device.CaptureSessionBreaks = captureSessionBreaks;
            device.CaptureScreenshots = captureScreenshots;
            // Clamped rather than rejected: the form only offers valid choices, so anything
            // outside the range is a hand-crafted request, and a sane value beats an error page.
            device.ScreenshotIntervalMinutes = Math.Clamp(
                screenshotIntervalMinutes,
                DeviceCapturePolicyDto.MinScreenshotIntervalMinutes,
                DeviceCapturePolicyDto.MaxScreenshotIntervalMinutes);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Marks every open alert as read. Acknowledging is not resolving: the alert stays listed
    /// until the condition itself clears, it just stops counting against the bell.
    /// </summary>
    public async Task<IActionResult> OnPostAcknowledgeAlertsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        await _db.Alerts
            .Where(a => a.ResolvedUtc == null && a.AcknowledgedUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.AcknowledgedUtc, now), cancellationToken);

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTogglePinAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, cancellationToken);
        if (device is not null)
        {
            device.IsPinned = !device.IsPinned;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Removes the device and everything it ever reported. The event tables carry a plain DeviceId
    /// column with no foreign key, so nothing cascades on its own - without deleting the events
    /// here they would linger forever as rows belonging to a machine that no longer exists, still
    /// counted in every total while being impossible to attribute to anything on screen.
    /// Irreversible, hence the confirmation on the button.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await _db.AppUsageEvents.Where(e => e.DeviceId == deviceId).ExecuteDeleteAsync(cancellationToken);
        await _db.IdlePeriods.Where(e => e.DeviceId == deviceId).ExecuteDeleteAsync(cancellationToken);
        await _db.UrlVisits.Where(e => e.DeviceId == deviceId).ExecuteDeleteAsync(cancellationToken);
        await _db.SessionBreaks.Where(e => e.DeviceId == deviceId).ExecuteDeleteAsync(cancellationToken);
        await _db.Screenshots.Where(e => e.DeviceId == deviceId).ExecuteDeleteAsync(cancellationToken);
        await _db.Devices.Where(d => d.DeviceId == deviceId).ExecuteDeleteAsync(cancellationToken);

        return RedirectToPage();
    }
}
