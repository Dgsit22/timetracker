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
