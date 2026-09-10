using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

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
            await _db.SaveChangesAsync(cancellationToken);
        }

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
