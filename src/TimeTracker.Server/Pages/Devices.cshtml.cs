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
        Devices = await _db.Devices.OrderBy(d => d.MachineName).ToListAsync(cancellationToken);

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
}
