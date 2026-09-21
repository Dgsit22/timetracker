using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.ViewComponents;

/// <summary>
/// The header's alert bell. A view component rather than layout code with a DbContext in it: the
/// layout renders on every page, and this keeps the query and its view in one place that the
/// layout simply invokes.
/// </summary>
public class NotificationsViewComponent : ViewComponent
{
    private const int MaxShown = 6;

    private readonly TimeTrackerDbContext _db;

    public NotificationsViewComponent(TimeTrackerDbContext db)
    {
        _db = db;
    }

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var open = await _db.Alerts
            .Where(a => a.ResolvedUtc == null)
            .OrderByDescending(a => a.LastSeenUtc)
            .Take(MaxShown)
            .ToListAsync();

        // The badge counts only what has not been acknowledged - dismissing says "I know", so it
        // should quiet the badge while leaving the alert listed until the condition itself clears.
        var unacknowledged = await _db.Alerts
            .CountAsync(a => a.ResolvedUtc == null && a.AcknowledgedUtc == null);

        return View(new NotificationsViewModel(open, unacknowledged));
    }
}

public record NotificationsViewModel(List<Alert> Open, int UnacknowledgedCount);
