namespace TimeTracker.Shared.Events;

public record AppUsageEventDto(
    Guid EventId,
    Guid DeviceId,
    string ProcessName,
    string WindowTitle,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    double DurationSeconds);
