using TimeTracker.Shared.Events;

namespace TimeTracker.Server.Data;

public class AppUsageEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string ProcessName { get; set; } = default!;
    public string WindowTitle { get; set; } = default!;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public class IdlePeriodEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string UserName { get; set; } = default!;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public int IdleThresholdSeconds { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public class UrlVisitEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string UserName { get; set; } = default!;
    public BrowserKind Browser { get; set; }
    public string? Url { get; set; }
    public string PageTitle { get; set; } = default!;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset EndedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public UrlCaptureMethod CaptureMethod { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public class SessionBreakEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string UserName { get; set; } = default!;
    public DateTimeOffset BreakStartUtc { get; set; }
    public DateTimeOffset? BreakEndUtc { get; set; }
    public SessionBreakReason Reason { get; set; }
    public SessionBreakEndReason? EndReason { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

public class ScreenshotEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string UserName { get; set; } = default!;
    public DateTimeOffset CapturedAtUtc { get; set; }
    public int MonitorIndex { get; set; }
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public string ContentType { get; set; } = default!;
    public byte[] ImageBytes { get; set; } = default!;
    public DateTimeOffset ReceivedAtUtc { get; set; }
}

/// <summary>
/// One row per Agent installation. Auto-registered on first sync; an admin can
/// dial back which event types a given machine is allowed to report.
/// </summary>
public class Device
{
    public Guid DeviceId { get; set; }
    public string MachineName { get; set; } = default!;
    public string LastUserName { get; set; } = default!;
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }

    public bool CaptureAppUsage { get; set; } = true;
    public bool CaptureUrlVisits { get; set; } = true;
    public bool CaptureIdle { get; set; } = true;
    public bool CaptureSessionBreaks { get; set; } = true;
    public bool CaptureScreenshots { get; set; } = true;
}

/// <summary>
/// A named bucket of tracked usernames (the raw Windows username the Agent reports -
/// see UserName on the event types above) - unrelated to ApplicationUser/Identity,
/// which is who logs into this admin console, not who's being monitored.
/// </summary>
public class Group
{
    public Guid GroupId { get; set; }
    public string Name { get; set; } = default!;
}

/// <summary>Composite-keyed membership row: one tracked username in one Group.</summary>
public class GroupMember
{
    public Guid GroupId { get; set; }
    public string UserName { get; set; } = default!;
}

/// <summary>
/// Hides a tracked username's activity from Activity/Dashboard/Reports - display-only,
/// the Agent still captures and stores everything as normal. Targets exactly one of
/// UserName (a specific tracked user) or GroupId (every member of that group).
/// </summary>
public class ExclusionRule
{
    public Guid ExclusionRuleId { get; set; }
    public string? UserName { get; set; }
    public Guid? GroupId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
