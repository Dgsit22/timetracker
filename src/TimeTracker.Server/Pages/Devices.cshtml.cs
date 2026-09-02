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

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Devices = await _db.Devices.OrderBy(d => d.MachineName).ToListAsync(cancellationToken);
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
