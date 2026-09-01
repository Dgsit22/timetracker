namespace TimeTracker.Shared.Events;

public record UrlVisitEventDto(
    Guid EventId,
    Guid DeviceId,
    BrowserKind Browser,
    string? Url,
    string PageTitle,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    double DurationSeconds,
    UrlCaptureMethod CaptureMethod);
